import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import type { MapShellConfig } from '../core/core-types';
import type { RaceParticipantDto, RaceReplayPayload } from './race-api';
import { installFakeRaf, type FakeRaf } from '../core/raf-test-harness';

// RF-02 pin: an in-session paint must survive a checkpoint Apply on BOTH views of the field.
//
// A checkpoint Apply rebuilds the field (rebuildField → buildRace). The markers are re-painted from the
// remembered `painted` map (attachRaceLayers re-applies it), but before the fix the elevation/speed charts
// were re-seeded from race.meta's BUILD-TIME colours and never re-painted — so the map showed the painted
// colour while the profile reverted to the default. The fix writes the paint back onto the participant
// objects buildRace reads, so a rebuild carries it into both views (and into the results-table swatch).
//
// The page is booted with a fake map; the markers, charts, results table and checkpoint editor are stubbed
// so the test can drive the real onPainted/onChanged seams and observe what each view is told.

function handler() { return { enable: vi.fn(), disable: vi.fn() }; }

class FakeMap {
	dragRotate = handler(); dragPan = handler(); scrollZoom = handler();
	touchZoomRotate = handler();
	touchPitch = handler();
	isMoving = (): boolean => false; keyboard = handler(); doubleClickZoom = handler();
	private _canvas = document.createElement('canvas');
	private _sources = new Set<string>();
	getCanvas() { return this._canvas; }
	getCenter(): [number, number] { return [8.1, 47.9]; }
	jumpTo = vi.fn();
	addSource = vi.fn((id: string) => { this._sources.add(id); });
	addLayer = vi.fn();
	getLayer = vi.fn(() => undefined);
	getSource = vi.fn((id: string) => (this._sources.has(id) ? { setData: vi.fn() } : undefined));
	fitBounds = vi.fn();
	once = vi.fn();
}

let fakeMap: FakeMap;
let shellConfig: MapShellConfig;

interface MarkerStub {
	setColors: ReturnType<typeof vi.fn>;
	[k: string]: ReturnType<typeof vi.fn>;
}
let markerInstances: MarkerStub[] = [];

interface ChartsStub {
	setColors: ReturnType<typeof vi.fn>;
	setField: ReturnType<typeof vi.fn>;
	[k: string]: ReturnType<typeof vi.fn>;
}
let chartsInstances: ChartsStub[] = [];

interface ResultsTableOptions { onPainted: (c: ReadonlyMap<number, string>) => void; [k: string]: unknown; }
let resultsOptions: ResultsTableOptions | null = null;
interface CheckpointEditorOptions { onChanged: (cps: RaceReplayPayload['checkpoints']) => void; [k: string]: unknown; }
let editorOptions: CheckpointEditorOptions | null = null;

vi.mock('../core/map-shell', () => ({
	createMapShell: (cfg: MapShellConfig) => { shellConfig = cfg; return { switchToBasemap: vi.fn(), setActiveBasemap: vi.fn(), destroy: vi.fn() }; },
}));
vi.mock('../maplibre/maplibre-terrain', () => ({
	initMapLibreTerrain: () => fakeMap,
	setMaplibreStyle: () => Promise.resolve(),
}));
vi.mock('./race-markers', () => ({
	createRaceMarkers: () => {
		const m: MarkerStub = { setHighlight: vi.fn(), setLabelled: vi.fn(), setColors: vi.fn(), setEmphasis: vi.fn(), setVisible: vi.fn(), setDimmed: vi.fn(), flush: vi.fn(), flushNow: vi.fn(), destroy: vi.fn() };
		markerInstances.push(m); return m;
	},
}));
vi.mock('./race-results-table', () => ({
	initRaceResultsTable: (opts: ResultsTableOptions) => { resultsOptions = opts; return { destroy: vi.fn() }; },
}));
vi.mock('./race-charts', () => ({
	createRaceCharts: () => {
		const c: ChartsStub = { setCheckpoints: vi.fn(), setField: vi.fn(), setColors: vi.fn(), setEmphasis: vi.fn(), setFilter: vi.fn(), setSelected: vi.fn(), setSpeedProfile: vi.fn(), setPlaceable: vi.fn(), setPlayhead: vi.fn(), setLeadLine: vi.fn(), tick: vi.fn(), attachMap: vi.fn(), setMapMarkerSuppressed: vi.fn(), destroy: vi.fn() };
		chartsInstances.push(c); return c;
	},
}));
vi.mock('./race-checkpoint-editor', () => ({
	initRaceCheckpointEditor: (opts: CheckpointEditorOptions) => { editorOptions = opts; return { setCheckpoints: vi.fn(), place: vi.fn(), move: vi.fn(), select: vi.fn(), destroy: vi.fn() }; },
}));
vi.mock('./race-import-panel', () => ({ initRaceImportPanel: vi.fn() }));
vi.mock('./race-riders-panel', () => ({ initRaceRidersPanel: vi.fn() }));
vi.mock('./race-settings-panel', () => ({ initRaceSettingsPanel: vi.fn() }));
vi.mock('./race-clear-field', () => ({ initRaceClearField: vi.fn() }));
vi.mock('../core/map-toolbar', () => ({ setupMapToolbar: () => vi.fn() }));
vi.mock('./race-api', () => ({ fetchRaceReplay: () => Promise.resolve(payload()) }));

const NODES = 201, STEP = 10, TOTAL = (NODES - 1) * STEP;
function simulated(id: number, finishSeconds: number): RaceParticipantDto {
	return { participantId: id, kind: 'simulated', name: `Rider ${id}`, bib: `${id}`, category: null, gender: null, club: null, color: null, emphasised: false, calloutOff: false, officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null, startOffsetSeconds: null, wave: null, discipline: null, seed: id, splits: [{ checkpointId: 3, elapsedSeconds: finishSeconds }], track: null };
}
function payload(): RaceReplayPayload {
	return {
		raceId: 1, raceName: 'X',
		course: { lat: Array.from({ length: NODES }, () => 47.9), lng: Array.from({ length: NODES }, (_, i) => 8.1 + i * 0.0001), elevation: Array.from({ length: NODES }, () => 500), gradePercent: Array.from({ length: NODES }, (_, i) => (i < NODES / 2 ? 8 : -8)), stepMeters: STEP, totalMeters: TOTAL },
		checkpoints: [ { checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: null, legColor: null }, { checkpointId: 3, seq: 1, name: 'Finish', chainageMeters: TOTAL, node: NODES - 1, legName: null, legColor: null } ],
		participants: [simulated(1, 600), simulated(2, 700)],
		costCurve: null, costCurveMinBin: 0,
	};
}

async function bootPage(): Promise<void> {
	await import('./race-replay');
	shellConfig.createMapLibre!('raceMap', 'style', null, 'liberty');
	shellConfig.onMapLibreReady!(fakeMap as unknown as maplibregl.Map, 'style', null);
	await vi.waitFor(() => expect(document.querySelectorAll('.race-sb-row[data-index]').length).toBeGreaterThan(0));
}

/** Did the charts stub end up told that rider `index` is `color` — via EITHER channel (setColors map or setField colours array)? */
function chartsShow(charts: ChartsStub, index: number, color: string): boolean {
	const viaSetColors = charts.setColors.mock.calls.some((c) => c[0] instanceof Map && (c[0] as Map<number, string>).get(index) === color);
	const viaSetField = charts.setField.mock.calls.some((c) => Array.isArray((c[0] as { colors?: string[] })?.colors) && (c[0] as { colors: string[] }).colors[index] === color);
	return viaSetColors || viaSetField;
}

let raf: FakeRaf;

beforeEach(() => {
	vi.resetModules();
	fakeMap = new FakeMap();
	markerInstances = []; chartsInstances = []; resultsOptions = null; editorOptions = null;
	document.body.innerHTML = `
		<div id="raceReplay" data-race-id="1" data-can-edit="true">
			<div id="raceHero">
				<div id="raceMap"></div><div id="raceStyleSwitcher"></div>
				<div id="raceLoading"></div><div id="raceError" hidden></div>
				<button id="raceMapFullscreen"></button>
				<div id="raceReplaySidebar"><div id="raceReplaySidebarHeader"><button id="raceReplayFollowToggle"></button></div><div id="raceReplaySidebarBody"></div></div>
				<button id="raceReplayPlayPause"></button>
				<span id="raceReplayCurrentTime"></span>
				<input id="raceReplaySeek" type="range" value="0"><select id="raceReplaySpeed"></select>
				<button id="raceReplayExit"></button>
				<canvas id="raceProfile"></canvas>
			</div>
			<div id="raceResultsToolbar"></div><div id="raceResultsGrid"></div><div id="raceCheckpoints"></div>
		</div>`;
	(window as unknown as { matchMedia: (q: string) => { matches: boolean } }).matchMedia = () => ({ matches: false });
	raf = installFakeRaf();
});
afterEach(() => {
	raf.restore(); vi.unstubAllGlobals(); document.body.innerHTML = ''; });

describe('RF-02 — paint survives a checkpoint Apply on BOTH views', () => {
	it('re-applies the in-session paint to the charts after rebuildField, as it does to the markers', async () => {
		await bootPage();
		const charts = must(chartsInstances[0], 'one charts instance');

		// Paint rider index 0 red — reaches the charts live.
		const paint = new Map<number, string>([[0, '#ff0000']]);
		resultsOptions!.onPainted(paint);
		expect(charts.setColors).toHaveBeenCalledWith(paint);

		charts.setColors.mockClear();
		charts.setField.mockClear();
		const markersBefore = markerInstances.length;

		// Apply a checkpoint edit → rebuildField.
		editorOptions!.onChanged(payload().checkpoints);

		// A new markers layer is built and RE-PAINTED with the remembered colour.
		const newMarkers = must(markerInstances[markerInstances.length - 1], 'rebuilt markers layer');
		expect(markerInstances.length).toBeGreaterThan(markersBefore);
		const markerRepainted = newMarkers.setColors.mock.calls.some((c) => c[0] instanceof Map && (c[0] as Map<number, string>).get(0) === '#ff0000');
		expect(markerRepainted, 'markers re-painted after rebuild').toBe(true);

		// The charts must show the paint too, or the profile shows the default while the map shows red.
		expect(chartsShow(charts, 0, '#ff0000'), 'charts show the paint after rebuild (divergence bug if false)').toBe(true);
	});
});
