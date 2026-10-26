import type { SegmentEffort } from './segment-stats-effort-types';
import type { SegmentStatsMapShared, SegmentStatsMapHoverApi } from './segment-stats-map-types';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// SS-01 characterization pins for the surviving MapLibre segment-stats map engine.
// The legacy raster engine is deleted across SS-02…SS-06; these lock the MapLibre
// source/layer build, marker placement, fitBounds framing, effort-layer handler
// registration, and hide/show-static-layer visibility toggling first so the
// collapse can't silently regress them.
//
// Setup quirk: segment-stats modules gate on `ctx` at module-eval and
// segment-statistics.ts parses #segmentStatisticsConfig at top level, so each
// test seeds the JSON tag, dynamically imports the module under a fresh registry,
// and publishes a recording fake map via setMaplibreMap (maplibre-state).

const COORDS: number[][] = [[1, 10], [2, 11], [3, 12], [2, 13], [1, 14]]; // [lat, lng]

interface FakeSource { setData: ReturnType<typeof vi.fn>; }

type Handler = (ev: unknown) => void;

class FakeMarker {
	el: HTMLElement;
	removed = false;
	constructor(opts: { element: HTMLElement }) { this.el = opts.element; markers.push(this); }
	setLngLat(): this { return this; }
	addTo(): this { return this; }
	remove(): void { this.removed = true; }
	getElement(): HTMLElement { return this.el; }
}
class FakePopup {
	html: string | null = null;
	setLngLat(): this { return this; }
	setHTML(h: string): this { this.html = h; return this; }
	addTo(): this { return this; }
}

class FakeMap {
	calls: Array<[string, string]> = [];                       // add/removeSource + add/removeLayer, in order
	layout: Array<[string, string, unknown]> = [];             // setLayoutProperty(layer, prop, value)
	paint: Array<[string, string, unknown]> = [];              // setPaintProperty(layer, prop, value)
	fitBoundsArgs: unknown[] = [];
	layerHandlers: Array<{ type: string; layer: string }> = []; // on(type, layer, fn)
	offCalls: Array<{ type: string; layer: string }> = [];
	private _sources = new Map<string, FakeSource>();
	private _layers = new Set<string>();

	addSource(id: string, _spec: unknown): void {
		this.calls.push(['addSource', id]);
		this._sources.set(id, { setData: vi.fn() });
	}
	addLayer(spec: { id: string }, _beforeId?: string): void {
		this.calls.push(['addLayer', spec.id]);
		this._layers.add(spec.id);
	}
	removeLayer(id: string): void { this.calls.push(['removeLayer', id]); this._layers.delete(id); }
	removeSource(id: string): void { this.calls.push(['removeSource', id]); this._sources.delete(id); }
	getSource(id: string): FakeSource | undefined { return this._sources.get(id); }
	getLayer(id: string): { id: string } | undefined { return this._layers.has(id) ? { id } : undefined; }
	setLayoutProperty(layer: string, prop: string, value: unknown): void { this.layout.push([layer, prop, value]); }
	setPaintProperty(layer: string, prop: string, value: unknown): void { this.paint.push([layer, prop, value]); }
	fitBounds(...args: unknown[]): void { this.fitBoundsArgs.push(args); }
	on(type: string, layer: string, _fn: Handler): void { this.layerHandlers.push({ type, layer }); }
	off(type: string, layer: string, _fn: Handler): void { this.offCalls.push({ type, layer }); }
	getCanvas(): HTMLElement { return document.createElement('canvas'); }
	getZoom(): number { return 14; }
}

let markers: FakeMarker[] = [];
let fakeMap: FakeMap;

function effort(idx: number): SegmentEffort {
	return {
		trackId: 100 + idx, name: 'Effort ' + idx, date: '2026-01-0' + (idx + 1), time: 100 + idx,
		entryId: 0, exitId: 0, rank: idx + 1, colorIndex: idx, color: idx === 0 ? '#dc3545' : '#007bff',
	};
}

function shared(): SegmentStatsMapShared {
	return {
		segmentCoords: COORDS,
		efforts: [effort(0), effort(1)],
		effortGpsArrays: [],
		shapes: [],
		getActiveEngine: () => ({ engine: 'maplibre', styleUrl: null }),
	};
}

function seedConfig(): void {
	const cfg = {
		hasSegment: true, hasGpsData: true, hasEfforts: true, mapTilerApiKey: '',
		segmentId: 5, kind: 'polyline',
		segmentCoords: COORDS,
		effortRows: [
			{ effortIndex: 0, colorIndex: 0, gpsInSegmentId: 1, activityId: 100, activityName: 'A0', activityType: 'Run', isOwn: true, userDisplayName: null, dateIso: '2026-01-01', matchPct: 100, elapsedSec: 100, distanceM: 500, avgSpeedMs: 5, avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0 },
			{ effortIndex: 1, colorIndex: 1, gpsInSegmentId: 2, activityId: 101, activityName: 'A1', activityType: 'Run', isOwn: true, userDisplayName: null, dateIso: '2026-01-02', matchPct: 100, elapsedSec: 101, distanceM: 500, avgSpeedMs: 5, avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0 },
		],
	};
	document.body.innerHTML = '<script type="application/json" id="segmentStatisticsConfig">' + JSON.stringify(cfg) + '</script>';
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
async function loadModule(): Promise<any> {
	const mod = await import('./segment-stats-map-maplibre');
	// The engine reads the active map from core/map-state (import inside the reset registry).
	const { setMaplibreMap } = await import('../core/map-state');
	setMaplibreMap(fakeMap as unknown as maplibregl.Map);
	mod.bindShared(shared());
	return mod;
}

beforeEach(() => {
	vi.resetModules();
	markers = [];
	fakeMap = new FakeMap();
	seedConfig();
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(globalThis as any).maplibregl = { Marker: FakeMarker, Popup: FakePopup };
});

afterEach(() => {
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	delete (globalThis as any).maplibregl;
	document.body.innerHTML = '';
});

describe('segment-stats MapLibre engine — SS-01 pins', () => {
	it('showSegmentRoute draws route, S/E markers and fits bounds', async () => {
		const { maplibreApi } = await loadModule();
		maplibreApi.showSegmentRoute(true);

		const added = fakeMap.calls.filter(([op]) => op === 'addSource' || op === 'addLayer');
		expect(added).toEqual([['addSource', 'seg-route-src'], ['addLayer', 'seg-route']]);
		expect(markers).toHaveLength(2); // S + E
		expect(fakeMap.fitBoundsArgs[0]).toEqual([[[10, 1], [14, 3]], { padding: 40 }]);
	});

	it('clearAll removes tracked layers, sources, markers and layer handlers', async () => {
		const { maplibreApi } = await loadModule();
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		(globalThis as any).fetch = vi.fn().mockResolvedValue({
			ok: true,
			json: async () => ({
				'100': [{ lat: 1, lng: 10 }, { lat: 2, lng: 12 }, { lat: 1, lng: 14 }],
				'101': [{ lat: 1, lng: 10 }, { lat: 2, lng: 12 }, { lat: 1, lng: 14 }],
			}),
		});

		maplibreApi.showSegmentRoute(true);
		await maplibreApi.showEfforts([0, 1], false);
		// Effort layer registered its hover/click handlers
		expect(fakeMap.layerHandlers.map((h) => h.type).sort()).toEqual(['click', 'mouseenter', 'mouseleave']);

		maplibreApi.clearAll();

		expect(fakeMap.getLayer('seg-route')).toBeUndefined();
		expect(fakeMap.getSource('seg-route-src')).toBeUndefined();
		expect(fakeMap.getLayer('seg-efforts-line')).toBeUndefined();
		expect(fakeMap.getSource('seg-efforts')).toBeUndefined();
		expect(markers.every((m) => m.removed)).toBe(true);
		expect(fakeMap.offCalls.map((h) => h.type).sort()).toEqual(['click', 'mouseenter', 'mouseleave']);
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		delete (globalThis as any).fetch;
	});

	it('hideStaticLayers / showStaticLayers toggle visibility on layers and markers', async () => {
		const { maplibreApi } = await loadModule();
		maplibreApi.showSegmentRoute(true); // adds seg-route layer + 2 markers

		maplibreApi.hideStaticLayers();
		expect(fakeMap.layout).toContainEqual(['seg-route', 'visibility', 'none']);
		expect(markers.every((m) => m.getElement().style.display === 'none')).toBe(true);

		maplibreApi.showStaticLayers();
		expect(fakeMap.layout).toContainEqual(['seg-route', 'visibility', 'visible']);
		expect(markers.every((m) => m.getElement().style.display === '')).toBe(true);
	});
});

// the engine no longer reaches for the `ctx` singleton — initSegmentStatsMapLibre(ctx) receives
// it and every method that needs the visible-effort set reads it through that parameter. This pins the
// seam: rebind the api onto a FAKE ctx and assert the engine consults IT, not the imported singleton.
describe('segment-stats MapLibre engine — receives ctx', () => {
	it('showSelectedEfforts reads the visible-effort set from the RECEIVED ctx, not the import', async () => {
		const mod = await loadModule();
		const fakeCtx = { getVisibleEffortIndices: vi.fn(() => [] as number[]), effortVisibility: new Set<number>() };
		// Rebind every api method onto the fake context (what a real page's initSegmentStatsMap would do
		// with its own ctx). No vi.mock of segment-statistics — a plain object passed in.
		mod.initSegmentStatsMapLibre(fakeCtx as unknown as import('./segment-stats-page-types').SegmentStatisticsContext);

		await mod.maplibreApi.showSelectedEfforts();

		expect(fakeCtx.getVisibleEffortIndices).toHaveBeenCalled(); // engine consulted the received ctx
	});
});

// Camera stays put on a view/style switch, and switching re-attaches efforts from the in-memory
// GPS cache instead of re-fetching. Pins the fix for the "strange camera replacement" + effort reload.
describe('segment-stats MapLibre engine — stable camera + effort cache re-attach', () => {
	const bulkResponse = {
		ok: true,
		json: async () => ({
			'100': [{ lat: 1, lng: 10 }, { lat: 2, lng: 12 }, { lat: 1, lng: 14 }],
			'101': [{ lat: 1, lng: 10 }, { lat: 2, lng: 12 }, { lat: 1, lng: 14 }],
		}),
	};

	it('showSegmentRoute(false) draws route + markers but does NOT fit bounds', async () => {
		const { maplibreApi } = await loadModule();
		maplibreApi.showSegmentRoute(false);

		const added = fakeMap.calls.filter(([op]) => op === 'addSource' || op === 'addLayer');
		expect(added).toEqual([['addSource', 'seg-route-src'], ['addLayer', 'seg-route']]);
		expect(markers).toHaveLength(2);        // S + E still drawn
		expect(fakeMap.fitBoundsArgs).toHaveLength(0); // camera untouched
	});

	it('a second showEfforts re-attaches from cache: no refetch, no camera reframe', async () => {
		const { maplibreApi } = await loadModule();
		const fetchMock = vi.fn().mockResolvedValue(bulkResponse);
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		(globalThis as any).fetch = fetchMock;

		// First display: cache empty → bulk fetch populates shared().effortGpsArrays.
		await maplibreApi.showEfforts([0, 1], false);
		expect(fetchMock).toHaveBeenCalledTimes(1);
		expect(fakeMap.getSource('seg-efforts')).toBeDefined();

		// A view/style switch wipes the layers (clearAll) but the GPS cache survives.
		maplibreApi.clearAll();
		expect(fakeMap.getSource('seg-efforts')).toBeUndefined();
		fetchMock.mockClear();
		fakeMap.fitBoundsArgs = [];

		// Re-display from cache: source rebuilt, no network, no fitBounds.
		await maplibreApi.showEfforts([0, 1], false);
		expect(fetchMock).not.toHaveBeenCalled();
		expect(fakeMap.getSource('seg-efforts')).toBeDefined();
		expect(fakeMap.fitBoundsArgs).toHaveLength(0);

		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		delete (globalThis as any).fetch;
	});
});

// SS-03: the map→chart proximity hover, ported to an engine-agnostic adapter. Pins
// that the MapLibre-wired adapter drives the charts to the nearest visible effort
// point and that replay suppresses the hit-test.
// H1: the hit-test linearizes the projection at the mouse (anchor + axis probes)
// instead of projecting every GPS point — per-point MapLibre project() calls run a
// DEM elevation lookup each when terrain is on and froze the browser for ~6s per
// mousemove on large effort sets. The pins below lock the O(1)-projection contract.
describe('segment-stats map→chart proximity hover — SS-03 pin + H1 regression', () => {
	const raf = (): Promise<void> => new Promise((r) => setTimeout(r, 30)); // let the rAF hit-test fire

	// Linear fake projection (100 px per degree) — makes the H1 Jacobian probes exact.
	const linearProject = (lat: number, lng: number): { x: number; y: number } =>
		({ x: lng * 100, y: -lat * 100 });

	// seg-stats-shared is a leaf slot module; import it inside the (reset) registry that
	// seedHoverCtx imports map-hover from, so provider + consumer share one instance.
	let sharedMod: typeof import('./seg-stats-shared') | null = null;

	// Seeds ctx + shared state for one visible effort and returns the recording chart spy.
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	async function seedHoverCtx(gps: Array<{ lat: number; lng: number }>): Promise<{ ctx: any; mapHoverApi: SegmentStatsMapHoverApi; setActive: ReturnType<typeof vi.fn> }> {
		const { ctx } = await import('./segment-statistics');
		// Import the real hover module so it assigns ctx.showSegmentPositionMarker (MH-03) — the test no
		// longer injects a standalone fake that masked the never-wired seam.
		await import('./segment-stats-hover');
		const { mapHoverApi } = await import('./segment-stats-map-hover');
		sharedMod = await import('./seg-stats-shared');
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		sharedMod.registerMapShared({ effortGpsArrays: [gps] } as any);
		ctx.effortVisibility.add(0);
		ctx.performanceData = [{
			effortId: 0, activityId: 100, elapsedTime: 100, points: [
				{ distance: 0, time: 0, speed: 5, altitude: 0, lat: 1, lng: 10 },
				{ distance: 250, time: 50, speed: 5, altitude: 0, lat: 1.5, lng: 11 },
				{ distance: 500, time: 100, speed: 5, altitude: 0, lat: 2, lng: 12 },
			],
		}];
		const setActive = vi.fn();
		ctx.performanceChart = {
			data: { datasets: [{ hidden: false }] },
			getActiveElements: () => [],
			setActiveElements: setActive,
			tooltip: { setActiveElements: vi.fn() },
			// eslint-disable-next-line @typescript-eslint/no-explicit-any
		} as any;
		// Spy on the REAL wired seam (throws if MH-03's assignment regresses to undefined).
		vi.spyOn(ctx, 'showSegmentPositionMarker');
		return { ctx, mapHoverApi, setActive };
	}

	afterEach(() => {
		sharedMod?.setSegmentReplayActive(false);
		sharedMod?.registerMapShared(null);
		sharedMod = null;
	});

	it('picks the nearest visible effort point, drives the chart, and is suppressed during replay', async () => {
		// Effort 0 GPS: the hit-test steps by 3, so it samples index 0 and 3; index 3 is nearest.
		const gps = [
			{ lat: 1, lng: 10 }, { lat: 1.1, lng: 10.1 }, { lat: 1.2, lng: 10.2 }, { lat: 2, lng: 12 },
		];
		const { ctx, mapHoverApi, setActive } = await seedHoverCtx(gps);

		mapHoverApi.bind({ project: linearProject, onDragStart: () => {}, onDragEnd: () => {} });

		// Replay active → the hit-test is suppressed (returns before scheduling the rAF).
		sharedMod!.setSegmentReplayActive(true);
		mapHoverApi.handleMouseMove({ lng: 12, lat: 2 });
		await raf();
		expect(setActive).not.toHaveBeenCalled();

		// Replay inactive → nearest sampled point (index 3, exactly under the mouse) drives the chart.
		sharedMod!.setSegmentReplayActive(false);
		mapHoverApi.handleMouseMove({ lng: 12, lat: 2 });
		await raf();
		expect(setActive).toHaveBeenCalled();
		expect(setActive.mock.calls.at(-1)![0][0].datasetIndex).toBe(0);
		expect(ctx.showSegmentPositionMarker).toHaveBeenCalled();
	});

	it('H1: one hover scan projects O(1) probe points — never one per GPS point', async () => {
		// 3000-point track: the frozen build issued ~one project() per sampled point here.
		const gps = Array.from({ length: 3000 }, (_, i) => ({ lat: 1 + i * 1e-5, lng: 10 + i * 1e-5 }));
		const { mapHoverApi, setActive } = await seedHoverCtx(gps);
		const project = vi.fn(linearProject);
		mapHoverApi.bind({ project, onDragStart: () => {}, onDragEnd: () => {} });

		mapHoverApi.handleMouseMove({ lng: 10, lat: 1 });
		await raf();

		expect(setActive).toHaveBeenCalled(); // scan ran and found the point under the mouse
		expect(project.mock.calls.length).toBeLessThanOrEqual(3); // anchor + 2 axis probes
	});

	it('H1: a mousemove burst coalesces into at most one hit-test scan', async () => {
		const gps = [{ lat: 1, lng: 10 }, { lat: 1.1, lng: 10.1 }, { lat: 1.2, lng: 10.2 }, { lat: 2, lng: 12 }];
		const { mapHoverApi } = await seedHoverCtx(gps);
		const project = vi.fn(linearProject);
		mapHoverApi.bind({ project, onDragStart: () => {}, onDragEnd: () => {} });

		// 25 moves inside one throttle window: throttle + the single-rAF guard must
		// collapse them into one scan (≤3 projections total), not 25 scans.
		for (let i = 0; i < 25; i++) mapHoverApi.handleMouseMove({ lng: 12, lat: 2 });
		await raf();

		expect(project.mock.calls.length).toBeLessThanOrEqual(3);
	});
});
