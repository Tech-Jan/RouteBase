import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import type { MapShellConfig } from '../core/core-types';
import type { RaceParticipantDto, RaceReplayPayload } from './race-api';
import { installFakeRaf, type FakeRaf } from '../core/raf-test-harness';

/**
 * /Races/Replay's map lifecycle — the three things that had no owner:
 *
 *   RS-01  the Style menu was INERT (no switchMapLibreStyle → map-shell's in-place branch returns
 *          without touching the map, while the toolbar label updates anyway, so it LOOKS like it worked);
 *   RS-06  a bfcache restore destroyed the map while the rAF loop kept driving the old markers, wedging
 *          the Play button permanently;
 *   PR-09  every map re-init re-fetched the whole payload and re-ran the pacing model for 424 riders.
 *
 * Driven through the real playback shell and the real field builder; the map, the marker layer, the fetch
 * and the two side panels are stubbed.
 */

class FakeMap {
	dragRotate = handler();
	dragPan = handler();
	scrollZoom = handler();
	touchZoomRotate = handler();
	touchPitch = handler();
	isMoving = (): boolean => false;
	keyboard = handler();
	doubleClickZoom = handler();
	private _canvas = document.createElement('canvas');
	private _sources = new Set<string>();
	/** style.load listeners, so a test can land the switch when it chooses. */
	styleLoad: Array<() => void> = [];

	getCanvas(): HTMLCanvasElement { return this._canvas; }
	getCenter(): [number, number] { return [8.1, 47.9]; }
	jumpTo = vi.fn();
	addSource = vi.fn((id: string) => { this._sources.add(id); });
	addLayer = vi.fn();
	getLayer = vi.fn(() => undefined);
	// A real GeoJSON source has setData — drawCourseLine calls it to recolour the legs in place.
	getSource = vi.fn((id: string) => (this._sources.has(id) ? { setData: vi.fn() } : undefined));
	fitBounds = vi.fn();
	once = vi.fn((event: string, cb: () => void) => {
		if (event === 'style.load') this.styleLoad.push(cb);
	});
	/** What a real style switch does to us: every source we added is gone. */
	dropSources(): void { this._sources.clear(); }
}

function handler(): { enable: ReturnType<typeof vi.fn>; disable: ReturnType<typeof vi.fn> } {
	return { enable: vi.fn(), disable: vi.fn() };
}

let fakeMap: FakeMap;
let shellConfig: MapShellConfig;
let fetchCount = 0;
let buildCount = 0;
interface MarkerStub {
	setHighlight: ReturnType<typeof vi.fn>;
	setLabelled: ReturnType<typeof vi.fn>;
	setColors: ReturnType<typeof vi.fn>;
	setEmphasis: ReturnType<typeof vi.fn>;
	setVisible: ReturnType<typeof vi.fn>;
	setDimmed: ReturnType<typeof vi.fn>;
	flush: ReturnType<typeof vi.fn>;
	flushNow: ReturnType<typeof vi.fn>;
	destroy: ReturnType<typeof vi.fn>;
}
let markerInstances: MarkerStub[] = [];

/** The results table is the page's paint/filter UI — captured so a test can drive those callbacks. */
interface ResultsTableOptions {
	onPainted: (colors: ReadonlyMap<number, string>) => void;
	onFiltered: (indices: readonly number[], filtering: boolean) => void;
	onModeChange: (mode: 'all' | 'highlight' | 'hide') => void;
	onRowClick: (index: number) => void;
}
let resultsOptions: ResultsTableOptions | null = null;

/** The checkpoint editor is the owner's leg-boundary UI — captured so a test can move a boundary. */
interface CheckpointEditorOptions {
	onChanged: (checkpoints: RaceReplayPayload['checkpoints']) => void;
	onSelect: (id: number) => void;
}
let editorOptions: CheckpointEditorOptions | null = null;
const setStyleSpy = vi.fn();
const shellDestroy = vi.fn();
const toolbarTeardown = vi.fn();

vi.mock('../core/map-shell', () => ({
	createMapShell: (cfg: MapShellConfig) => {
		shellConfig = cfg;
		return { switchToBasemap: vi.fn(), setActiveBasemap: vi.fn(), destroy: shellDestroy };
	},
}));
vi.mock('../maplibre/maplibre-terrain', () => ({
	initMapLibreTerrain: () => fakeMap,
	setMaplibreStyle: (...args: unknown[]) => { setStyleSpy(...args); return Promise.resolve(); },
}));
vi.mock('./race-markers', () => ({
	createRaceMarkers: () => {
		const instance: MarkerStub = {
			setHighlight: vi.fn(), setLabelled: vi.fn(),
			setColors: vi.fn(), setEmphasis: vi.fn(), setVisible: vi.fn(), setDimmed: vi.fn(),
			flush: vi.fn(), flushNow: vi.fn(), destroy: vi.fn(),
		};
		markerInstances.push(instance);
		return instance;
	},
}));
vi.mock('./race-results-table', () => ({
	initRaceResultsTable: (opts: ResultsTableOptions) => {
		resultsOptions = opts;
		return { destroy: vi.fn(), setPositions: vi.fn(), redraw: vi.fn() };
	},
}));
// The two charts + their hover live behind createRaceCharts now; stub that one seam so the lifecycle pins
// stay clear of the real canvas renderers, the ResizeObserver and the map hover listener.
vi.mock('./race-charts', () => ({
	createRaceCharts: () => ({
		setCheckpoints: vi.fn(), setField: vi.fn(), setColors: vi.fn(), setEmphasis: vi.fn(), setFilter: vi.fn(),
		setSelected: vi.fn(), setSpeedProfile: vi.fn(), setPlayhead: vi.fn(), setLeadLine: vi.fn(), tick: vi.fn(),
		attachMap: vi.fn(), setMapMarkerSuppressed: vi.fn(), destroy: vi.fn(),
	}),
}));
vi.mock('./race-checkpoint-editor', () => ({
	initRaceCheckpointEditor: (opts: CheckpointEditorOptions) => {
		editorOptions = opts;
		return {
			setCheckpoints: vi.fn(), place: vi.fn(), move: vi.fn(), select: vi.fn(), destroy: vi.fn(),
		};
	},
}));
vi.mock('./race-import-panel', () => ({ initRaceImportPanel: vi.fn() }));
vi.mock('./race-riders-panel', () => ({ initRaceRidersPanel: vi.fn() }));
vi.mock('../core/map-toolbar', () => ({ setupMapToolbar: () => toolbarTeardown }));
vi.mock('./race-api', () => ({
	fetchRaceReplay: () => { fetchCount++; return Promise.resolve(payload()); },
}));
vi.mock('./race-build', async (importOriginal) => {
	const actual = await importOriginal<typeof import('./race-build')>();
	return {
		...actual,
		buildRace: (p: RaceReplayPayload) => { buildCount++; return actual.buildRace(p); },
	};
});

const NODES = 201;
const STEP = 10;
const TOTAL = (NODES - 1) * STEP;

function simulated(id: number, finishSeconds: number): RaceParticipantDto {
	return {
		participantId: id, kind: 'simulated', name: `Rider ${id}`, bib: `${id}`,
		category: null, gender: null, club: null, color: null, emphasised: false, calloutOff: false, officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null, startOffsetSeconds: null, wave: null, discipline: null, seed: id,
		splits: [{ checkpointId: 3, elapsedSeconds: finishSeconds }],
		track: null,
	};
}

function payload(): RaceReplayPayload {
	return {
		raceId: 1,
		raceName: 'Black Forest',
		course: {
			lat: Array.from({ length: NODES }, () => 47.9),
			lng: Array.from({ length: NODES }, (_, i) => 8.1 + i * 0.0001),
			elevation: Array.from({ length: NODES }, () => 500),
			gradePercent: Array.from({ length: NODES }, (_, i) => (i < NODES / 2 ? 8 : -8)),
			stepMeters: STEP,
			totalMeters: TOTAL,
		},
		checkpoints: [
			{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: null, legColor: null },
			{ checkpointId: 3, seq: 1, name: 'Finish', chainageMeters: TOTAL, node: NODES - 1, legName: null, legColor: null },
		],
		participants: [simulated(1, 600), simulated(2, 700)],
		costCurve: null,
		costCurveMinBin: 0,
	};
}

/** Boot the page: import (which self-inits), hand it a map, then let start() resolve. */
async function bootPage(): Promise<void> {
	await import('./race-replay');
	shellConfig.createMapLibre!('raceMap', 'style', null, 'liberty');
	shellConfig.onMapLibreReady!(fakeMap as unknown as maplibregl.Map, 'style', null);
	await vi.waitFor(() => expect(document.querySelectorAll('.race-sb-row[data-index]').length).toBeGreaterThan(0));
}

/** What map-shell does on a pageshow(persisted): the page's hook, then destroy, then a fresh init. */
async function bfcacheRestore(): Promise<void> {
	shellConfig.onBfcacheRestore!();
	shellConfig.destroyMapLibre!(fakeMap as unknown as maplibregl.Map);
	fakeMap = new FakeMap();
	shellConfig.createMapLibre!('raceMap', 'style', null, 'liberty');
	shellConfig.onMapLibreReady!(fakeMap as unknown as maplibregl.Map, 'style', null);
	await vi.waitFor(() => expect(markerInstances.length).toBeGreaterThan(1));
}

let raf: FakeRaf;

beforeEach(() => {
	vi.resetModules();
	fakeMap = new FakeMap();
	fetchCount = 0;
	buildCount = 0;
	markerInstances = [];
	resultsOptions = null;
	editorOptions = null;
	setStyleSpy.mockClear();
	shellDestroy.mockClear();
	toolbarTeardown.mockClear();
	document.body.innerHTML = `
		<div id="raceReplay" data-race-id="1" data-is-owner="true" data-can-edit="true">
			<div id="raceHero">
				<div id="raceMap"></div><div id="raceStyleSwitcher"></div>
				<div id="raceLoading"></div><div id="raceError" hidden></div>
				<button id="raceMapFullscreen"></button>
				<div id="raceReplaySidebar">
					<div id="raceReplaySidebarHeader"><button id="raceReplayFollowToggle"></button></div>
					<div id="raceReplaySidebarBody"></div>
				</div>
				<button id="raceReplayPlayPause"></button>
				<span id="raceReplayCurrentTime"></span>
				<input id="raceReplaySeek" type="range" value="0">
				<select id="raceReplaySpeed"></select>
				<button id="raceReplayExit"></button>
				<canvas id="raceProfile"></canvas>
			</div>
			<div id="raceResultsToolbar"></div><div id="raceResultsGrid"></div>
			<div id="raceCheckpoints"></div>
		</div>`;
	// jsdom has no matchMedia; the overlay probes it for the mobile collapse default.
	(window as unknown as { matchMedia: (q: string) => { matches: boolean } }).matchMedia = () => ({ matches: false });
	raf = installFakeRaf();
});

afterEach(() => {
	raf.restore();
	vi.unstubAllGlobals();
	document.body.innerHTML = '';
});

describe('RS-01 — the basemap Style switcher actually switches', () => {
	it('the page supplies switchMapLibreStyle, so map-shell has something to call', async () => {
		await bootPage();

		expect(shellConfig.switchMapLibreStyle).toBeTypeOf('function');
	});

	it('a style switch hands the new style to MapLibre and re-attaches the race on style.load', async () => {
		await bootPage();
		const before = markerInstances.length;

		shellConfig.switchMapLibreStyle!(fakeMap as unknown as maplibregl.Map, 'terrain-url', 'terrain');

		expect(setStyleSpy).toHaveBeenCalledWith('terrain-url', 'terrain');
		// Nothing is re-attached yet — the new style's sources do not exist until style.load.
		expect(markerInstances).toHaveLength(before);

		fakeMap.dropSources();                 // what a real setStyle does to our sources
		fakeMap.styleLoad.forEach((cb) => cb());

		expect(markerInstances).toHaveLength(before + 1);
		expect(fakeMap.addSource).toHaveBeenCalledWith('race-course', expect.anything());
	});

	it('waits on style.load, never on isStyleLoaded (which lies while tiles are in flight)', async () => {
		await bootPage();

		shellConfig.switchMapLibreStyle!(fakeMap as unknown as maplibregl.Map, 'terrain-url', 'terrain');

		expect(fakeMap.once).toHaveBeenCalledWith('style.load', expect.any(Function));
	});

	it('re-fetches nothing on a style switch: the field is map-independent', async () => {
		await bootPage();
		expect(fetchCount).toBe(1);

		shellConfig.switchMapLibreStyle!(fakeMap as unknown as maplibregl.Map, 'terrain-url', 'terrain');
		fakeMap.dropSources();
		fakeMap.styleLoad.forEach((cb) => cb());

		expect(fetchCount).toBe(1);
		expect(buildCount).toBe(1);
	});
});

describe('RS-06 — a bfcache restore does not wedge the Play button', () => {
	it('stops playback and drops the stale markers before the map is destroyed', async () => {
		await bootPage();
		document.getElementById('raceReplayPlayPause')!.dispatchEvent(new MouseEvent('click'));   // play

		shellConfig.onBfcacheRestore!();

		// The loop is stopped, so no rAF frame can reach the destroyed map's sources — which is what used
		// to throw out of the rAF callback and leave isPlaying true for ever.
		expect(document.getElementById('raceReplayPlayPause')!.innerHTML).toContain('bi-play-fill');
		expect(must(markerInstances[0], 'first marker layer').destroy).toHaveBeenCalledTimes(1);
	});

	it('destroyMapLibre drops the marker layers with the map', async () => {
		await bootPage();

		shellConfig.destroyMapLibre!(fakeMap as unknown as maplibregl.Map);

		expect(must(markerInstances[0], 'first marker layer').destroy).toHaveBeenCalled();
	});

	it('the page teardown releases the toolbar, the markers and the shell', async () => {
		const { initRaceReplayPage } = await import('./race-replay');
		shellConfig.createMapLibre!('raceMap', 'style', null, 'liberty');
		shellConfig.onMapLibreReady!(fakeMap as unknown as maplibregl.Map, 'style', null);
		await vi.waitFor(() => expect(markerInstances.length).toBe(1));

		// A second init returns the teardown for THAT instance; the first one is the module's self-init.
		toolbarTeardown.mockClear();
		const destroy = initRaceReplayPage();
		destroy!();

		expect(toolbarTeardown).toHaveBeenCalledTimes(1);
		expect(shellDestroy).toHaveBeenCalled();
	});
});

describe('PR-09 — a map re-init does not rebuild 424 riders', () => {
	it('re-fetches the payload once and re-runs the pacing model once, across a bfcache re-init', async () => {
		await bootPage();
		expect(fetchCount).toBe(1);
		expect(buildCount).toBe(1);
		expect(markerInstances).toHaveLength(1);

		await bfcacheRestore();

		// The field survives (it is pure and map-independent); only the map-bound half is redone.
		expect(fetchCount).toBe(1);
		expect(buildCount).toBe(1);
		expect(markerInstances).toHaveLength(2);
		expect(must(markerInstances[0], 'first marker layer').destroy).toHaveBeenCalledTimes(1);
	});

	it('the re-attached map gets the course line and the riders back', async () => {
		await bootPage();

		await bfcacheRestore();

		expect(fakeMap.addSource).toHaveBeenCalledWith('race-course', expect.anything());
		expect(must(markerInstances[1], 'second marker layer').flushNow).toHaveBeenCalled();
	});
});

/**
 * A map re-init must not silently undo what the user did to the map.
 *
 * attachRaceLayers rebuilds the marker layer from `race.meta`, which carries only the BUILD-time colour —
 * and the display controller keeps its mode/filter to itself and was never re-applied. So a style switch
 * or a Back-navigation reverted every painted rider to the default and un-hid the whole field, while the
 * PROFILE (which is not rebuilt) kept the paint and the filter. Two views of one field, disagreeing —
 * and the database still holding colours the map denied.
 */
describe('a map re-init restores what the user set', () => {
	it('re-applies painted colours to the rebuilt marker layer', async () => {
		await bootPage();

		resultsOptions!.onPainted(new Map([[0, '#e64980']]));
		expect(must(markerInstances[0], 'first marker layer').setColors).toHaveBeenCalledWith(new Map([[0, '#e64980']]));

		await bfcacheRestore();

		expect(must(markerInstances[1], 'second marker layer').setColors).toHaveBeenCalledWith(new Map([[0, '#e64980']]));
	});

	it('re-applies the Hide filter, instead of quietly showing the whole field again', async () => {
		await bootPage();

		// A filter, then Hide. setMode applies at once (a click is not a keystroke), folding in the
		// pending coalesced filter — so the first marker layer is filtered synchronously.
		resultsOptions!.onFiltered([1], true);
		resultsOptions!.onModeChange('hide');
		expect(must(markerInstances[0], 'first marker layer').setVisible).toHaveBeenCalledWith([1]);

		await bfcacheRestore();

		expect(must(markerInstances[1], 'second marker layer').setVisible).toHaveBeenCalledWith([1]);
	});
});

/**
 * A checkpoint is not decoration. Its node is an ANCHOR the pacing model hits exactly, so moving one
 * changes WHERE every simulated rider changes pace — and its id keys a leg column in the results grid.
 *
 * The edit only redrew the profile and the coloured course line. The picture moved and the race did not:
 * the owner dragged the ski→run boundary and the 424 riders went on pacing to the old one, while the
 * grid kept showing the old leg columns.
 */
/**
 * Following a rider must survive a map re-init — the camera, not just the halo.
 *
 * destroyMapLibre nulls `camera`, and the re-init builds a fresh CameraControls with isActive = false. But
 * `followIndex` is closure state that survives, so after a Back-navigation the rider was still haloed,
 * still labelled and still flagged as followed in the leaderboard, while updateFollowCamera() early-returned
 * on `!camera.isActive` and the camera never tracked him again. Clicking his row to fix it UNfollowed him,
 * so the user had to click twice.
 *
 * engageFollow takes the map's native gesture handlers, which is what makes this observable.
 */
describe('follow survives a map re-init', () => {
	it('re-engages the camera on the new map, not just the halo', async () => {
		await bootPage();

		// A tap (pointerdown+up), not a click — the sidebar follows on pointer-up now (rows rebuild each tick).
		const row = document.querySelector<HTMLElement>('.race-sb-row[data-index]')!;
		row.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 0, clientY: 0 }));
		row.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 0, clientY: 0 }));
		expect(fakeMap.dragPan.disable).toHaveBeenCalled();   // the camera has the old map

		await bfcacheRestore();

		// `fakeMap` is now the NEW map. The followed rider must have its handlers too.
		expect(fakeMap.dragPan.disable).toHaveBeenCalled();
	});
});

describe('a checkpoint edit re-paces the field', () => {
	it('re-runs the pacing model when a leg boundary moves', async () => {
		await bootPage();
		expect(buildCount).toBe(1);

		// The owner drags the mid-course checkpoint. This is what the editor hands back on a successful
		// write — the server's checkpoints, with the new chainage and node.
		editorOptions!.onChanged([
			{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: null, legColor: null },
			{ checkpointId: 2, seq: 1, name: 'Ski→Run', chainageMeters: 1200, node: 120, legName: 'Run', legColor: null },
			{ checkpointId: 3, seq: 2, name: 'Finish', chainageMeters: TOTAL, node: NODES - 1, legName: null, legColor: null },
		]);

		expect(buildCount).toBe(2);
	});
});

/**
 * The anti-inert case. This file used to stub `requestAnimationFrame` as `() => 1`, which swallows every
 * frame callback — so the playback shell's self-rescheduling loop never ticked and NOTHING here could
 * assert frame-driven behaviour. Every test above still passed, because they all assert synchronously.
 *
 * That is the trap `core/raf-test-harness` exists for: with the inert stub this test cannot be written at
 * all, and its absence looks identical to coverage. Both the shell and the transport run for real here.
 */
describe('the replay loop actually advances when frames fire', () => {
	it('moves the clock on a flushed frame — impossible to assert under an inert rAF stub', async () => {
		await bootPage();

		// The shell reads performance.now() itself for the per-frame dt; the harness's own frame time is
		// ignored, so drive a clock by hand or dt is 0 and the playhead never moves.
		let clock = 1000;
		const nowSpy = vi.spyOn(performance, 'now').mockImplementation(() => clock);
		try {
			const clockEl = document.getElementById('raceReplayCurrentTime')!;
			document.getElementById('raceReplayPlayPause')!.dispatchEvent(new MouseEvent('click'));
			const atPlay = clockEl.textContent;

			clock += 5000;
			raf.flushFrames(2);

			expect(clockEl.textContent).not.toBe(atPlay);
		} finally {
			nowSpy.mockRestore();
		}
	});
});
