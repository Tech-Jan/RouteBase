import type { EffortPerformanceData } from './segment-stats-effort-types';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// SS-01 characterization pins for the surviving MapLibre replay path. The legacy
// replay engine (camera, viewport canvases, DOM markers) is deleted across
// SS-04…SS-06; these lock the MapLibre track/marker layer build, the single
// setData-per-flush marker update, the CameraControls follow activation (native
// handler disable/enable), and destroy teardown order + replay-flag clearing.
//
// The pins drive the replay through its public API with a MapLibre engine literal.
// SS-04/SS-05 drop the engine param from init/bind (single engine); the `initReplay`
// helper below is the single call-site those commits adjust — assertions stay fixed.

const COORDS: number[][] = [[1, 10], [2, 11], [3, 12], [2, 13], [1, 14]]; // [lat, lng]

interface FakeSource { setData: ReturnType<typeof vi.fn>; }

function handlerGroup(): { enable: ReturnType<typeof vi.fn>; disable: ReturnType<typeof vi.fn> } {
	return { enable: vi.fn(), disable: vi.fn() };
}

class FakeMap {
	calls: Array<[string, string]> = [];   // add/removeSource + add/removeLayer, in order
	fitBoundsArgs: unknown[] = [];
	dragPan = handlerGroup();
	dragRotate = handlerGroup();
	scrollZoom = handlerGroup();
	touchZoomRotate = handlerGroup();
	touchPitch = handlerGroup();
	keyboard = handlerGroup();
	doubleClickZoom = handlerGroup();
	isMoving = (): boolean => false;
	private _canvas = document.createElement('canvas');
	private _container = document.createElement('div');
	private _sources = new Map<string, FakeSource>();
	private _layers = new Set<string>();

	addSource(id: string, _spec: unknown): void { this.calls.push(['addSource', id]); this._sources.set(id, { setData: vi.fn() }); }
	addLayer(spec: { id: string }, _beforeId?: string): void { this.calls.push(['addLayer', spec.id]); this._layers.add(spec.id); }
	removeLayer(id: string): void { this.calls.push(['removeLayer', id]); this._layers.delete(id); }
	removeSource(id: string): void { this.calls.push(['removeSource', id]); this._sources.delete(id); }
	getSource(id: string): FakeSource | undefined { return this._sources.get(id); }
	getLayer(id: string): { id: string } | undefined { return this._layers.has(id) ? { id } : undefined; }
	fitBounds(...args: unknown[]): void { this.fitBoundsArgs.push(args); }
	jumpTo(): void {}
	// Spied, not plain: the LV-04 pin asserts the follow path never reads the map's current view.
	getZoom = vi.fn(() => 15);
	getBearing = vi.fn(() => 0);
	getPitch = vi.fn(() => 30);
	getCanvas(): HTMLCanvasElement { return this._canvas; }
	getContainer(): HTMLDivElement { return this._container; }
	on(): void {}
	off(): void {}
}

let fakeMap: FakeMap;

function perfDatum(idx: number): EffortPerformanceData {
	// Three GPS points along the segment; distinct times so interpolation is well-defined.
	return {
		effortId: idx, activityId: 100 + idx, activityName: 'Effort ' + idx, activityDate: '2026-01-0' + (idx + 1),
		elapsedTime: 100, points: [
			{ distance: 0, time: 0, speed: 5, altitude: 0, lat: 1, lng: 10 },
			{ distance: 250, time: 50, speed: 5, altitude: 0, lat: 2, lng: 12 },
			{ distance: 500, time: 100, speed: 5, altitude: 0, lat: 1, lng: 14 },
		],
	};
}

function seedConfig(): void {
	const cfg = {
		hasSegment: true, hasGpsData: true, hasEfforts: true, mapTilerApiKey: '',
		segmentId: 5, kind: 'polyline',
		segmentCoords: COORDS,
		effortRows: [
			{ effortIndex: 0, colorIndex: 0, gpsInSegmentId: 1, activityId: 100, activityName: 'A0', activityType: 'Run', isOwn: true, userDisplayName: null, dateIso: '2026-01-01', matchPct: 100, elapsedSec: 100, distanceM: 500, avgSpeedMs: 5, avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0 },
			{ effortIndex: 1, colorIndex: 1, gpsInSegmentId: 2, activityId: 101, activityName: 'A1', activityType: 'Run', isOwn: true, userDisplayName: null, dateIso: '2026-01-02', matchPct: 100, elapsedSec: 100, distanceM: 500, avgSpeedMs: 5, avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0 },
		],
	};
	document.body.innerHTML = '<script type="application/json" id="segmentStatisticsConfig">' + JSON.stringify(cfg) + '</script>';
}

// Single call-site for the replay boot (engine dimension collapsed in SS-04).
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function initReplay(replayApi: any, tracksApi: any, markersApi: any): boolean {
	void tracksApi; void markersApi; // bound internally by replayApi.init
	return replayApi.init(fakeMap, [perfDatum(0), perfDatum(1)], [0, 1], false);
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
async function loadModules(): Promise<any> {
	const replay = await import('./segment-stats-replay');
	const tracks = await import('./segment-stats-replay-tracks');
	const markers = await import('./segment-stats-replay-markers');
	// Same (reset) registry as replay.ts → shared slot instance the replay flag lives in.
	const shared = await import('./seg-stats-shared');
	// Publish the active map in the same registry (replaces the former window.maplibreInstance seed).
	const { setMaplibreMap } = await import('../core/map-state');
	setMaplibreMap(fakeMap as unknown as maplibregl.Map);
	return { replay, tracks, markers, shared };
}

beforeEach(() => {
	vi.resetModules();
	fakeMap = new FakeMap();
	seedConfig();
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(globalThis as any).maplibregl = {};
	// The active map is published via setMaplibreMap in loadModules (reset registry).
	// CameraControls is resolved via ESM import by replay.ts (loadModules imports the module).
});

afterEach(() => {
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	delete (globalThis as any).maplibregl;
	// The replay-active flag now lives in seg-stats-shared module state, which
	// vi.resetModules() re-initializes to false before each test — no manual reset.
	document.body.innerHTML = '';
});

describe('segment-stats MapLibre replay — SS-01 pins', () => {
	it('replay init draws seg-bg + effort track lines and fits bounds', async () => {
		const { replay, tracks, markers } = await loadModules();
		const ok = initReplay(replay.replayApi, tracks.replayTracksApi, markers.replayMarkersApi);
		expect(ok).toBe(true);

		expect(fakeMap.getSource('replay-seg-bg')).toBeDefined();
		expect(fakeMap.getLayer('replay-seg-bg-line')).toBeDefined();
		expect(fakeMap.getSource('replay-efforts')).toBeDefined();
		expect(fakeMap.getLayer('replay-efforts-line')).toBeDefined();
		expect(fakeMap.fitBoundsArgs).toHaveLength(1);
	});

	it('replay markers: create adds 3 circle layers; a dirty flush is one setData, a clean flush is none', async () => {
		const { replay, tracks, markers } = await loadModules();
		initReplay(replay.replayApi, tracks.replayTracksApi, markers.replayMarkersApi);

		expect(fakeMap.getLayer('replay-markers-halo')).toBeDefined();
		expect(fakeMap.getLayer('replay-markers-border')).toBeDefined();
		expect(fakeMap.getLayer('replay-markers-fill')).toBeDefined();

		const src = fakeMap.getSource('replay-markers')!;
		src.setData.mockClear();

		markers.replayMarkersApi.setMarkerPosition(0, 1.5, 11);
		markers.replayMarkersApi.setMarkerPosition(1, 2.5, 12);
		markers.replayMarkersApi.flushMarkerGeoSource();
		expect(src.setData).toHaveBeenCalledTimes(1); // N moves coalesce into one setData

		src.setData.mockClear();
		markers.replayMarkersApi.flushMarkerGeoSource();
		expect(src.setData).toHaveBeenCalledTimes(0); // nothing dirty → no redundant setData
	});

	it('setFollowEffort activates camera controls + takes panning; -1 hands it back', async () => {
		const { replay, tracks, markers } = await loadModules();
		initReplay(replay.replayApi, tracks.replayTracksApi, markers.replayMarkersApi);

		replay.replayApi.setFollowEffort(0);
		expect(fakeMap.dragPan.disable).toHaveBeenCalled();
		// dragRotate stays ON: rotating is the user's, the centre is the follow's.
		expect(fakeMap.dragRotate.disable).not.toHaveBeenCalled();

		replay.replayApi.setFollowEffort(-1);
		expect(fakeMap.dragPan.enable).toHaveBeenCalled();
	});

	// LV-04 regression pins. /Segments/Statistics is the reference surface for the follow-camera seam:
	// exact handler counts and the activate/deactivate ordering around them. The SET changed when
	// gestures went back to MapLibre — following now takes only the centre-movers — so these pin the
	// selective policy, not the old "all six off".

	it('pins follow-mode camera ownership: centre handlers off on follow, everything back on unfollow', async () => {
		const { replay, tracks, markers } = await loadModules();
		const { CameraControls } = await import('../maplibre/camera-controls');
		const activateSpy = vi.spyOn(CameraControls.prototype, 'activate');
		const deactivateSpy = vi.spyOn(CameraControls.prototype, 'deactivate');
		initReplay(replay.replayApi, tracks.replayTracksApi, markers.replayMarkersApi);

		const off = ['dragPan', 'doubleClickZoom', 'keyboard'] as const;
		const kept = ['scrollZoom', 'touchZoomRotate', 'touchPitch', 'dragRotate'] as const;
		const names = [...off, ...kept] as const;
		// init settles into "not following" (handlers on) — count from there.
		for (const n of names) { fakeMap[n].enable.mockClear(); fakeMap[n].disable.mockClear(); }
		activateSpy.mockClear();
		deactivateSpy.mockClear();

		replay.replayApi.setFollowEffort(0);
		for (const n of off) {
			expect(fakeMap[n].disable, n).toHaveBeenCalledTimes(1);
			expect(fakeMap[n].enable, n).not.toHaveBeenCalled();
		}
		// The gesture handlers are re-enabled, not disabled: pinch and 2-finger tilt keep working, now
		// anchored on the followed marker instead of the finger midpoint.
		for (const n of kept) {
			expect(fakeMap[n].disable, n).not.toHaveBeenCalled();
			expect(fakeMap[n].enable, n).toHaveBeenCalledTimes(1);
		}
		expect(activateSpy).toHaveBeenCalledTimes(1);
		expect(deactivateSpy).not.toHaveBeenCalled();

		replay.replayApi.setFollowEffort(-1);
		for (const n of names) expect(fakeMap[n].enable, n).toHaveBeenCalledWith();
		expect(deactivateSpy).toHaveBeenCalledTimes(1);

		activateSpy.mockRestore();
		deactivateSpy.mockRestore();
		replay.replayApi.destroy();
	});

	it('pins that engaging follow never seeds the auto camera from the map view (segment-stats is not /LiveView)', async () => {
		const { replay, tracks, markers } = await loadModules();
		initReplay(replay.replayApi, tracks.replayTracksApi, markers.replayMarkersApi);

		replay.replayApi.setFollowEffort(0);

		// The replay page's auto camera is its OWN base framing (baseZoom 15 / basePitch 30), re-applied
		// every frame by _updateCamera. /LiveView seeds its camera from map.getZoom()/getBearing()/
		// getPitch() on lock-on; doing that here would overwrite the base framing and change what a
		// paused follow looks like. The follow path must never read the map's current view.
		expect(fakeMap.getZoom).not.toHaveBeenCalled();
		expect(fakeMap.getBearing).not.toHaveBeenCalled();
		expect(fakeMap.getPitch).not.toHaveBeenCalled();

		replay.replayApi.destroy();
	});

	it('RS2-02: pause while following drives the idle loop; resume stops it', async () => {
		const { replay } = await loadModules();
		const { CameraControls } = await import('../maplibre/camera-controls');
		const startSpy = vi.spyOn(CameraControls.prototype, 'startIdleLoop');
		const stopSpy = vi.spyOn(CameraControls.prototype, 'stopIdleLoop');
		const { replayTracksApi } = await import('./segment-stats-replay-tracks');
		const { replayMarkersApi } = await import('./segment-stats-replay-markers');
		initReplay(replay.replayApi, replayTracksApi, replayMarkersApi);

		replay.replayApi.setFollowEffort(0);
		startSpy.mockClear();
		replay.replayApi.pause();
		expect(startSpy).toHaveBeenCalled(); // paused-follow keeps re-applying camera offsets

		replay.replayApi.resume();
		expect(stopSpy).toHaveBeenCalled(); // resume tears the idle loop down
		replay.replayApi.pause(); // stop the rAF the resume kicked off
	});

	it('RS2-04: resume after completion restarts from 0', async () => {
		const { replay } = await loadModules();
		const { replayTracksApi } = await import('./segment-stats-replay-tracks');
		const { replayMarkersApi } = await import('./segment-stats-replay-markers');
		initReplay(replay.replayApi, replayTracksApi, replayMarkersApi);

		// Freeze the clock so resume's synchronous first frame advances the playhead by exactly 0.
		const nowSpy = vi.spyOn(performance, 'now').mockReturnValue(1000);

		replay.replayApi.seek(100); // seekReset marks every effort finished
		expect(replay.replayApi.getEffortStates().every((s: { finished: boolean }) => s.finished)).toBe(true);

		replay.replayApi.resume();

		expect(replay.replayApi.getEffortStates().every((s: { finished: boolean }) => !s.finished)).toBe(true);
		expect(replay.replayApi.getCurrentTime()).toBe(0);
		expect(replay.replayApi.isPlaying()).toBe(true);
		replay.replayApi.pause(); // stop the rAF the resume kicked off (P-B4 pattern)
		nowSpy.mockRestore();
	});

	it('CR1-07: completion while following keeps the paused camera live (idle loop starts)', async () => {
		const { replay } = await loadModules();
		const { CameraControls } = await import('../maplibre/camera-controls');
		const startSpy = vi.spyOn(CameraControls.prototype, 'startIdleLoop');
		const { replayTracksApi } = await import('./segment-stats-replay-tracks');
		const { replayMarkersApi } = await import('./segment-stats-replay-markers');
		initReplay(replay.replayApi, replayTracksApi, replayMarkersApi);

		// Manual rAF queue + frozen clock so the completion frame is driven deterministically.
		let now = 1000;
		const rafQueue: FrameRequestCallback[] = [];
		vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => { rafQueue.push(cb); return rafQueue.length; });
		vi.stubGlobal('cancelAnimationFrame', () => {});
		const nowSpy = vi.spyOn(performance, 'now').mockImplementation(() => now);

		replay.replayApi.start();
		replay.replayApi.setFollowEffort(0);
		replay.replayApi.setSpeed(1000);
		startSpy.mockClear();

		// One 200ms frame at 1000x = 200s playhead — past every effort's 100s finish.
		now += 200;
		const frames = rafQueue.splice(0);
		frames.forEach((cb) => cb(now));

		expect(replay.replayApi.getEffortStates().every((s: { finished: boolean }) => s.finished)).toBe(true);
		expect(replay.replayApi.isPlaying()).toBe(false);
		expect(startSpy).toHaveBeenCalledTimes(1); // follow camera idles at the finish (parity with chain A)

		vi.unstubAllGlobals();
		nowSpy.mockRestore();
		startSpy.mockRestore();
	});

	it('destroy removes marker + track layers before their sources and clears the replay-active flag', async () => {
		const { replay, tracks, markers, shared } = await loadModules();
		initReplay(replay.replayApi, tracks.replayTracksApi, markers.replayMarkersApi);
		replay.replayApi.start();

		fakeMap.calls.length = 0;
		replay.replayApi.destroy();

		// Every source removal is preceded by removing a layer that references it.
		const sourceRemovals = fakeMap.calls.filter(([op]) => op === 'removeSource');
		expect(sourceRemovals.length).toBeGreaterThan(0);
		for (const [, srcId] of sourceRemovals) {
			const srcIdx = fakeMap.calls.findIndex(([op, id]) => op === 'removeSource' && id === srcId);
			const anyLayerBefore = fakeMap.calls.slice(0, srcIdx).some(([op]) => op === 'removeLayer');
			expect(anyLayerBefore).toBe(true);
		}
		expect(shared.getSegmentReplayActive()).toBe(false);
	});
});
