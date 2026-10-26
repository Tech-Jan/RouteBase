import type { GpsPoint } from '../core/core-types';
import { must } from '../core/invariant';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { ActivityReplay } from './activity-replay';
import {
	setReplayActive, isReplayActive, setBlueMarkerVisible, isBlueMarkerVisible, setCurrentMarker,
} from '../core/map-state';

// AD-06 characterization pins for the surviving MapLibre replay engine.
// The engine is the MapLibre twin that outlives the Leaflet deletion (CM-02/D2),
// so these lock its distance/time normalization, marker layer lifecycle, camera
// change-detection and teardown before the AD-01..AD-05 refactor/deletion.
// Fake-map pattern mirrors live-recording-map-maplibre.test.ts.

interface FakeSource {
	setData: ReturnType<typeof vi.fn>;
}

/** maplibregl.Map-shaped stub: records jumpTo/easeTo, backs sources/layers by Map/Set. */
class FakeMap {
	calls: Array<[string, string]> = [];
	jumpToCalls: Array<Record<string, unknown>> = [];
	easeToCalls: Array<Record<string, unknown>> = [];
	private _sources = new Map<string, FakeSource>();
	private _layers = new Set<string>();
	private _canvas = document.createElement('canvas');
	private _container = document.createElement('div');

	addSource(id: string, _spec: unknown): void {
		this.calls.push(['addSource', id]);
		this._sources.set(id, { setData: vi.fn() });
	}
	addLayer(spec: { id: string }): void {
		this.calls.push(['addLayer', spec.id]);
		this._layers.add(spec.id);
	}
	removeLayer(id: string): void {
		this.calls.push(['removeLayer', id]);
		this._layers.delete(id);
	}
	removeSource(id: string): void {
		this.calls.push(['removeSource', id]);
		this._sources.delete(id);
	}
	getSource(id: string): FakeSource | undefined {
		return this._sources.get(id);
	}
	getLayer(id: string): { id: string } | undefined {
		return this._layers.has(id) ? { id } : undefined;
	}
	// CameraControls / marker init wait on 'idle'; fire immediately in tests.
	once(_ev: string, cb: () => void): void { cb(); }
	jumpTo(cam: Record<string, unknown>): void { this.jumpToCalls.push(cam); }
	easeTo(cam: Record<string, unknown>): void { this.easeToCalls.push(cam); }
	getTerrain(): null { return null; }
	getPitch(): number { return 0; }
	getCanvas(): HTMLCanvasElement { return this._canvas; }
	getContainer(): HTMLElement { return this._container; }
}

// 5-point track: metre-scale .distance, "HH:MM:SS" .time strings (30s spacing → 120s span).
function makeTrack(): GpsPoint[] {
	return [
		{ lat: 40.000, lng: -105.000, distance: 0,    time: '10:00:00', altitude: 1600, speed: 0 },
		{ lat: 40.001, lng: -105.001, distance: 1000, time: '10:00:30', altitude: 1610, speed: 10 },
		{ lat: 40.002, lng: -105.002, distance: 2000, time: '10:01:00', altitude: 1620, speed: 10 },
		{ lat: 40.003, lng: -105.003, distance: 3000, time: '10:01:30', altitude: 1630, speed: 10 },
		{ lat: 40.004, lng: -105.004, distance: 4000, time: '10:02:00', altitude: 1640, speed: 10 },
	] as unknown as GpsPoint[];
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const asMap = (m: FakeMap): maplibregl.Map => m as any;

let replay: ActivityReplay | null = null;

beforeEach(() => {
	setReplayActive(false);
	setBlueMarkerVisible(true);
	setCurrentMarker(null);
});

afterEach(() => {
	if (replay) {
		try { replay.destroy(); } catch { /* teardown */ }
		replay = null;
	}
});

describe('ActivityReplay (MapLibre) — AD-06 pins', () => {
	it('constructor normalizes distances and adopts time-string duration', () => {
		const map = new FakeMap();
		const track = makeTrack();
		replay = new ActivityReplay(asMap(map), track, 0);

		// metres → km (values > 100 divided; 0 stays 0)
		expect(track.map((p) => p.distance)).toEqual([0, 1, 2, 3, 4]);
		// duration = span of the "HH:MM:SS" strings (0 → 120s)
		expect(replay.getTotalTime()).toBe(120);
	});

	it('start jumps to first point and flags replay active', () => {
		const map = new FakeMap();
		const track = makeTrack();
		replay = new ActivityReplay(asMap(map), track, 0);

		replay.start();

		expect(map.jumpToCalls[0]).toEqual({
			center: [-105.0, 40.0],
			zoom: 15.5,
			pitch: 0,
			bearing: 0,
		});
		expect(isReplayActive()).toBe(true);
		expect(isBlueMarkerVisible()).toBe(false);

		replay.pause();
		expect(replay.isPlaying).toBe(false);
	});

	it('initMarker creates border+fill circle layers once', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);

		replay.initMarker();
		const addedLayers = () => map.calls.filter(([op]) => op === 'addLayer').map(([, id]) => id);
		expect(addedLayers()).toEqual([
			'activity-replay-marker-border',
			'activity-replay-marker-fill',
		]);

		// Second call must not re-add layers (source already present).
		replay.initMarker();
		expect(addedLayers()).toEqual([
			'activity-replay-marker-border',
			'activity-replay-marker-fill',
		]);
	});

	it('seek(50) moves marker via setData and reports progress', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		replay.initMarker();

		const progress: Array<[number, number]> = [];
		replay.onProgress = (pct, idx) => { progress.push([pct, idx]); };

		replay.seek(50);

		const src = map.getSource('activity-replay-marker')!;
		const lastArg = src.setData.mock.calls.at(-1)![0] as GeoJSON.FeatureCollection<GeoJSON.Point>;
		const coords = must(lastArg.features[0], 'one marker feature').geometry.coordinates;
		expect(coords[0]).toBeCloseTo(-105.002, 3);
		expect(coords[1]).toBeCloseTo(40.002, 3);

		expect(progress.at(-1)).toEqual([50, 2]);
	});

	it('reports the UNROUNDED index too — the elevation strip converts it to its own axis', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		replay.initMarker();

		const reported: number[] = [];
		replay.onProgress = (_pct, _idx, _point, fracIndex) => { reported.push(fracIndex); };

		replay.currentIndex = 2.75;
		replay.updateFrame(2);

		// The rounded index drives the clock and the seek bar; anything on a different axis needs the
		// fraction, or it quantises to whole samples.
		expect(reported.at(-1)).toBe(2.75);
	});

	it('updateFrame skips jumpTo when camera unchanged', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);

		// currentIndex defaults to 0 → both frames resolve to the same camera center.
		replay.updateFrame(0);
		replay.updateFrame(0);

		expect(map.jumpToCalls).toHaveLength(1);
	});

	it('pause starts the camera idle loop; resume and destroy stop it (RP-02)', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		const startSpy = vi.spyOn(replay.cameraControls, 'startIdleLoop').mockImplementation(() => {});
		const stopSpy = vi.spyOn(replay.cameraControls, 'stopIdleLoop').mockImplementation(() => {});

		replay.start();
		replay.pause();
		expect(startSpy).toHaveBeenCalledTimes(1);

		replay.resume();
		expect(stopSpy).toHaveBeenCalledTimes(1);

		replay.pause();
		replay.destroy();
		replay = null;
		expect(stopSpy).toHaveBeenCalledTimes(2);
	});

	it('resume after completion restarts playback from the beginning (RP-04)', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		replay.setMode('time');

		// Simulate a finished replay sitting at the end.
		replay.currentTime = replay.getTotalTime();
		replay.currentIndex = 4;
		const onComplete = vi.fn();
		replay.onComplete = onComplete;

		replay.resume();

		expect(replay.currentIndex).toBeLessThan(1); // restarted from ~0, not stuck at 4
		expect(replay.isPlaying).toBe(true);
		expect(onComplete).not.toHaveBeenCalled();
	});

	it('complete() pauses and starts the idle loop; onComplete fires once', () => {
		// CR1-06 pin: A's completion idles the camera (idleOnComplete=true semantics) — must
		// survive the playback-shell extraction verbatim.
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		const startSpy = vi.spyOn(replay.cameraControls, 'startIdleLoop').mockImplementation(() => {});
		const onComplete = vi.fn();
		replay.onComplete = onComplete;

		replay.complete();

		expect(replay.isPlaying).toBe(false);
		expect(startSpy).toHaveBeenCalledTimes(1);
		expect(onComplete).toHaveBeenCalledTimes(1);
	});

	it('seekToIndex takes the same path as a percentage seek, in index space', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);

		replay.seekToIndex(3);

		expect(replay.currentIndex).toBe(3);
		expect(replay.getCurrentTime()).toBe(90); // 30s point spacing
	});

	it('destroy restores marker, eases camera flat and clears flag', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		replay.initMarker();

		replay.destroy();
		replay = null; // already torn down; skip afterEach double-destroy

		expect(map.easeToCalls.at(-1)).toEqual({ pitch: 0, bearing: 0, duration: 1000 });
		// Layers removed before the source.
		const removals = map.calls.filter(([op]) => op.startsWith('remove'));
		expect(removals).toEqual([
			['removeLayer', 'activity-replay-marker-fill'],
			['removeLayer', 'activity-replay-marker-border'],
			['removeSource', 'activity-replay-marker'],
		]);
		expect(isReplayActive()).toBe(false);
		expect(isBlueMarkerVisible()).toBe(true);
	});
});

// The photo viewer opens over a running replay, takes the camera (suspendFollowForModal) and pans
// through the ride's photos. The playhead follows those photos, so closing resumes from the one the
// user stopped on instead of snapping back to where the rider was when the viewer opened.
describe('ActivityReplay — the photo viewer moves the playhead', () => {
	/** Keep the paused idle loop out of the camera assertions — it writes on its own rAF schedule. */
	function quietIdle(r: ActivityReplay): void {
		vi.spyOn(r.cameraControls, 'startIdleLoop').mockImplementation(() => {});
		vi.spyOn(r.cameraControls, 'stopIdleLoop').mockImplementation(() => {});
	}

	const markerLngLat = (map: FakeMap): number[] => {
		const src = map.getSource('activity-replay-marker')!;
		const last = src.setData.mock.calls.at(-1)![0] as GeoJSON.FeatureCollection<GeoJSON.Point>;
		return must(last.features[0], 'one marker feature').geometry.coordinates;
	};

	it('seekToPosition lands on the track sample nearest the photo and reports it', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		replay.initMarker();

		const progress: Array<[number, number]> = [];
		replay.onProgress = (pct, idx) => { progress.push([pct, idx]); };

		replay.seekToPosition([-105.0029, 40.0031]); // a photo standing beside sample 3

		expect(replay.currentIndex).toBe(3);
		expect(progress.at(-1)).toEqual([75, 3]);
		expect(replay.getCurrentTime()).toBe(90); // the clock readout follows the playhead
	});

	it('moves the marker but NOT the camera while the viewer holds it', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		quietIdle(replay);
		replay.start();
		replay.pause();
		replay.suspendFollowForModal();
		const camWrites = map.jumpToCalls.length;

		replay.seekToPosition([-105.004, 40.004]);

		expect(markerLngLat(map)[0]).toBeCloseTo(-105.004, 3); // playhead moved…
		expect(map.jumpToCalls).toHaveLength(camWrites); // …view stayed on the photo
	});

	// A photo is a glance, not a camera reset: re-applying the base framing on close threw away the
	// tilt/rotation/zoom the user had set up before opening it.
	it('hands the camera back without re-framing it', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		quietIdle(replay);
		replay.start(); // start() DOES frame: zoom 15.5, pitch 0, bearing 0
		replay.pause();
		replay.suspendFollowForModal();
		const before = map.jumpToCalls.length;

		replay.restoreFollowAfterModal();

		const writes = map.jumpToCalls.slice(before);
		expect(writes.every((c) => !('zoom' in c) && !('pitch' in c) && !('bearing' in c))).toBe(true);
		// The centre still snaps back to the rider — only the framing is left alone.
		const center = writes.at(-1)!.center as [number, number];
		expect(center[0]).toBeCloseTo(-105, 3);
		expect(center[1]).toBeCloseTo(40, 3);
	});

	// Placement is a free map click, so a photo can legitimately sit nowhere near the route. There is
	// deliberately NO distance ceiling: the answer to "where was the rider when this was taken" is the
	// nearest sample however far away it is, and a silent refusal would strand the playhead instead.
	it('still lands on the nearest sample for a photo far off the route', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		replay.initMarker();

		// ~55 km out, along the track's own +lat/−lng heading so sample 4 is unambiguously the nearest.
		// Far enough that the grid's 3×3 neighbourhood is empty and the sparse-data fallback answers.
		replay.seekToPosition([-105.5, 40.5]);

		expect(replay.currentIndex).toBe(4); // the end — nearest of the five, not a rejection
	});

	// -1 from findNearestPointIndexExact means "no sample to answer with". Leaving the playhead alone is
	// the only safe reading — clamping to 0 would silently rewind the ride.
	it('leaves the playhead where it was when the track can answer nothing', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		replay.initMarker();
		replay.seekToIndex(2);

		replay.originalTrack = []; // the only way the lookup can come back empty
		replay.seekToPosition([-105.004, 40.004]);

		expect(replay.currentIndex).toBe(2);
	});

	it('re-centres on the photo the viewer stopped at once the camera comes back', () => {
		const map = new FakeMap();
		replay = new ActivityReplay(asMap(map), makeTrack(), 0);
		quietIdle(replay);
		replay.start(); // camera framed on sample 0
		replay.pause();
		replay.suspendFollowForModal();
		replay.seekToPosition([-105.004, 40.004]);

		replay.restoreFollowAfterModal();

		expect(map.jumpToCalls.at(-1)!.center).toEqual([-105.004, 40.004]);
		expect(replay.getFollowMode()).toBe(true); // the user's preference was never touched
	});
});
