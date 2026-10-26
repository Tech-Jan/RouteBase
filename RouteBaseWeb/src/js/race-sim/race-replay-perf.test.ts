import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { MapShellConfig } from '../core/core-types';
import type { RaceParticipantDto, RaceReplayPayload } from './race-api';
import { installFakeRaf, type FakeRaf } from '../core/raf-test-harness';

// RF-01 pin: a single render() must not full-sort the whole field a second time just to read the leader.
//
// The throttled follow sidebar legitimately sorts once per UI tick (field.order() — it needs the full
// ranking). But render() also called field.order() every frame purely to read the leader's (max) chainage
// for the charts' playhead — an O(P·log P) alloc+sort per frame, the exact per-frame cost the rework had
// removed. The fix reads field.leadChainage() (O(P), no alloc) instead, so a forced render sorts at most
// once (the sidebar), not twice.

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
let raf: FakeRaf;
let shellConfig: MapShellConfig;

vi.mock('../core/map-shell', () => ({
	createMapShell: (cfg: MapShellConfig) => { shellConfig = cfg; return { switchToBasemap: vi.fn(), setActiveBasemap: vi.fn(), destroy: vi.fn() }; },
}));
vi.mock('../maplibre/maplibre-terrain', () => ({
	initMapLibreTerrain: () => fakeMap,
	setMaplibreStyle: () => Promise.resolve(),
}));
vi.mock('./race-markers', () => ({
	createRaceMarkers: () => ({ setHighlight: vi.fn(), setLabelled: vi.fn(), setColors: vi.fn(), setEmphasis: vi.fn(), setVisible: vi.fn(), setDimmed: vi.fn(), flush: vi.fn(), flushNow: vi.fn(), destroy: vi.fn() }),
}));
vi.mock('./race-results-table', () => ({
	initRaceResultsTable: () => ({ destroy: vi.fn() }),
}));
vi.mock('./race-charts', () => ({
	createRaceCharts: () => ({ setCheckpoints: vi.fn(), setField: vi.fn(), setColors: vi.fn(), setEmphasis: vi.fn(), setFilter: vi.fn(), setSelected: vi.fn(), setSpeedProfile: vi.fn(), setPlaceable: vi.fn(), setPlayhead: vi.fn(), setLeadLine: vi.fn(), tick: vi.fn(), attachMap: vi.fn(), setMapMarkerSuppressed: vi.fn(), destroy: vi.fn() }),
}));
vi.mock('./race-checkpoint-editor', () => ({
	initRaceCheckpointEditor: () => ({ setCheckpoints: vi.fn(), place: vi.fn(), move: vi.fn(), select: vi.fn(), destroy: vi.fn() }),
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
		participants: [simulated(1, 600), simulated(2, 700), simulated(3, 650)],
		costCurve: null, costCurveMinBin: 0,
	};
}

async function bootPage(): Promise<void> {
	await import('./race-replay');
	shellConfig.createMapLibre!('raceMap', 'style', null, 'liberty');
	shellConfig.onMapLibreReady!(fakeMap as unknown as maplibregl.Map, 'style', null);
	await vi.waitFor(() => expect(document.querySelectorAll('.race-sb-row[data-index]').length).toBeGreaterThan(0));
}

beforeEach(() => {
	vi.resetModules();
	fakeMap = new FakeMap();
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
afterEach(() => { raf.restore(); vi.unstubAllGlobals(); document.body.innerHTML = ''; });

describe('RF-01 — one render must not full-sort the field twice', () => {
	it('render() sorts the field at most once per forced frame (charts read the leader, not a 2nd sort)', async () => {
		const fieldMod = await import('./race-field');
		const orderSpy = vi.spyOn(fieldMod.RaceField.prototype, 'order');
		// render() reads the leader through leadChainage() — so it doubles as the witness that a render
		// actually ran (RF-01 is about HOW render reads the leader, which is meaningless with no render).
		const leadSpy = vi.spyOn(fieldMod.RaceField.prototype, 'leadChainage');
		await bootPage();

		orderSpy.mockClear();
		leadSpy.mockClear();
		// One forced render via a seek. The seek is rAF-coalesced (PERF-1), so the frame flush is what
		// actually renders — without it this assertion would pass on ZERO renders and prove nothing.
		const seek = document.getElementById('raceReplaySeek') as HTMLInputElement;
		seek.value = '50';
		seek.dispatchEvent(new Event('input'));
		raf.flushFrames();

		expect(leadSpy.mock.calls.length, 'the seek did render').toBeGreaterThan(0);
		expect(orderSpy.mock.calls.length, 'order() calls in one forced render').toBeLessThanOrEqual(1);
	});
});
