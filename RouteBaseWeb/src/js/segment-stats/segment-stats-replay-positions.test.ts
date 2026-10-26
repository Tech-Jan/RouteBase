import type { EffortPerformanceData, EffortDataPoint } from './segment-stats-effort-types';
import type { SegmentStatsReplayEffortsApi, SegmentStatsReplayMarkersApi } from './segment-stats-replay-types';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// PS-08 characterization pins for replay marker POSITIONS (segment-stats-replay-efforts.ts)
// — the byte-identity anchor for PS-03 lazy full-res replay. Replay interpolates marker
// lat/lng between bracketing GPS points; the payload-shaping work must leave these exact.
// Assertions use toBe (full float equality), not closeTo, so any resample is caught.
//
// Fixtures are all-loaded (≥2 points) so the maxTime pin survives the PS-03 E.build hoist.
// Harness: seed #segmentStatisticsConfig, import segment-statistics (ctx) +
// segment-stats-replay-efforts under a fresh registry; drive the engine with a
// recording SegmentStatsReplayMarkersApi fake.

interface Pt {
	distance: number; time: number; speed: number;
	altitude: number | null; lat: number | null; lng: number | null;
	segmentProgress?: number | null;
}

// Effort 0: times [0,50,100], lat/lng (1,10)→(2,12)→(1,14), distance [0,250,500], elapsed 100.
function effort0(): EffortPerformanceData {
	const p: Pt[] = [
		{ distance: 0, time: 0, speed: 5, altitude: 0, lat: 1, lng: 10 },
		{ distance: 250, time: 50, speed: 5, altitude: 0, lat: 2, lng: 12 },
		{ distance: 500, time: 100, speed: 5, altitude: 0, lat: 1, lng: 14 },
	];
	return { effortId: 0, activityId: 100, activityName: 'E0', activityDate: '2026-01-01', elapsedTime: 100, points: p as unknown as EffortDataPoint[] };
}

// Effort 1: longer (elapsed 200), times [0,100,200], distance [0,300,600], lat/lng (5,50)→(6,52)→(5,54).
function effort1(): EffortPerformanceData {
	const p: Pt[] = [
		{ distance: 0, time: 0, speed: 5, altitude: 0, lat: 5, lng: 50 },
		{ distance: 300, time: 100, speed: 5, altitude: 0, lat: 6, lng: 52 },
		{ distance: 600, time: 200, speed: 5, altitude: 0, lat: 5, lng: 54 },
	];
	return { effortId: 1, activityId: 101, activityName: 'E1', activityDate: '2026-01-02', elapsedTime: 200, points: p as unknown as EffortDataPoint[] };
}

// Recording marker API: captures the last (lat,lng) set per effort + finished flags.
function recordingMarkers(): { api: SegmentStatsReplayMarkersApi; pos: Map<number, [number, number]>; finished: Map<number, boolean> } {
	const pos = new Map<number, [number, number]>();
	const finished = new Map<number, boolean>();
	const api = {
		bind: vi.fn(), create: vi.fn(),
		setMarkerPosition: (idx: number, lat: number, lng: number): void => { pos.set(idx, [lat, lng]); },
		setMarkerFinished: (idx: number, f: boolean): void => { finished.set(idx, f); },
		setMarkerHighlight: vi.fn(), flushMarkerGeoSource: vi.fn(),
		resetMarkerFeatures: vi.fn(), destroyAll: vi.fn(),
		getDiagnosticSnapshot: vi.fn(),
	} as unknown as SegmentStatsReplayMarkersApi;
	return { api, pos, finished };
}

function seedConfig(): void {
	const cfg = {
		hasSegment: true, hasGpsData: true, hasEfforts: true, mapTilerApiKey: '',
		segmentId: 5, kind: 'polyline',
		segmentCoords: [[1, 10], [2, 11]],
		effortRows: [
			{ effortIndex: 0, colorIndex: 0, gpsInSegmentId: 1, activityId: 100, activityName: 'E0', activityType: 'Run', isOwn: true, userDisplayName: null, dateIso: '2026-01-01', matchPct: 100, elapsedSec: 100, distanceM: 500, avgSpeedMs: 5, avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0 },
			{ effortIndex: 1, colorIndex: 1, gpsInSegmentId: 2, activityId: 101, activityName: 'E1', activityType: 'Run', isOwn: true, userDisplayName: null, dateIso: '2026-01-02', matchPct: 100, elapsedSec: 200, distanceM: 600, avgSpeedMs: 3, avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0 },
		],
	};
	document.body.innerHTML = '<script type="application/json" id="segmentStatisticsConfig">' + JSON.stringify(cfg) + '</script>';
}

async function loadEngine(): Promise<{ E: SegmentStatsReplayEffortsApi }> {
	await import('./segment-statistics');
	const { replayEffortsApi } = await import('./segment-stats-replay-efforts');
	return { E: replayEffortsApi };
}

const COLORS = ['#111111', '#222222'];

beforeEach(() => { seedConfig(); });

afterEach(async () => {
	document.body.innerHTML = '';
	const { vi: v } = await import('vitest');
	v.resetModules();
});

describe('segment-stats replay positions — PS-08 pins', () => {
	it('advance interpolates marker lat/lng linearly between bracketing points', async () => {
		const { E } = await loadEngine();
		const { api, pos } = recordingMarkers();
		E.build([effort0()], [0], COLORS);

		// t=0 → first point exactly.
		E.advance(0, api, null);
		expect(pos.get(0)).toEqual([1, 10]);
		// t=25 → halfway p0→p1: lat 1.5, lng 11 (0.5 on integers → exact floats).
		E.advance(25, api, null);
		expect(pos.get(0)![0]).toBe(1.5);
		expect(pos.get(0)![1]).toBe(11);
		// t=50 → p1 exactly.
		E.advance(50, api, null);
		expect(pos.get(0)).toEqual([2, 12]);
		// t=75 → halfway p1→p2: lat 1.5, lng 13.
		E.advance(75, api, null);
		expect(pos.get(0)![0]).toBe(1.5);
		expect(pos.get(0)![1]).toBe(13);
	});

	it('percent mode advances all efforts to the same completion fraction', async () => {
		const { E } = await loadEngine();
		const { api, pos } = recordingMarkers();
		E.build([effort0(), effort1()], [0, 1], COLORS);
		E.setMode('percent');

		// _maxTime = 200; currentTime 50 → 25% completion shared by both efforts.
		E.updateAllMarkers(50, api);
		// Effort 0 at 25% (halfway to its first midpoint): (1.5, 11).
		expect(pos.get(0)![0]).toBe(1.5);
		expect(pos.get(0)![1]).toBe(11);
		// Effort 1 at 25%: (5.5, 51) — same fraction, its own geometry.
		expect(pos.get(1)![0]).toBe(5.5);
		expect(pos.get(1)![1]).toBe(51);
	});

	it('seekReset past an effort end snaps its marker to the exact last point', async () => {
		const { E } = await loadEngine();
		const { api, pos, finished } = recordingMarkers();
		E.build([effort0(), effort1()], [0, 1], COLORS);

		// currentTime 150 (time mode): effort 0 (elapsed 100) finished → snap to last point (1,14);
		// effort 1 (elapsed 200) not yet finished.
		E.seekReset(150, api);
		expect(finished.get(0)).toBe(true);
		expect(pos.get(0)).toEqual([1, 14]);
		expect(finished.get(1)).toBe(false);
	});

	it('maxTime spans selected efforts (survives the PS-03 build hoist)', async () => {
		const { E } = await loadEngine();
		E.build([effort0(), effort1()], [0, 1], COLORS);
		expect(E.getMaxTime()).toBe(200); // max of the two elapsed times
	});
});
