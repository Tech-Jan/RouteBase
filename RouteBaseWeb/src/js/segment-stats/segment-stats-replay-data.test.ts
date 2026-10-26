import type { EffortDataPoint } from './segment-stats-effort-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { loadFullResEfforts, _resetFullResCache } from './segment-stats-replay-data';
import { must } from '../core/invariant';

// PS-03 tests for the lazy full-res replay loader (segment-stats-replay-data.ts):
// - skips efforts already full-res (pointsTruncated === false)
// - fetches truncated efforts in 50-chunks with maxPointsPerEffort 0
// - the session cache prevents a refetch on a second replay
//
// the loader now RECEIVES the page context as an explicit `ctx` param, so the harness builds a
// fake context and passes it — no #segmentStatisticsConfig island, no import of the real
// segment-statistics module. The session cache is module state, cleared via _resetFullResCache each test.

interface FetchCall { url: string; effortIds: number[]; maxPointsPerEffort: number }

// Build a columnar wire response for the requested ids (2 points each) — hydrated by the loader.
function wireResponse(effortIds: number[]): unknown[] {
	return effortIds.map((id) => ({
		effortId: id,
		activityId: 1000 + id,
		elapsedTime: 100,
		pointsTruncated: false,
		points: { distance: [0, 100], time: [0, 50], speed: [5, 5], lat: [1, 2], lng: [10, 12] },
	}));
}

function recordingFetch(calls: FetchCall[]): typeof fetch {
	return vi.fn(async (url: string, init?: RequestInit) => {
		const body = JSON.parse(String(init?.body ?? '{}')) as { effortIds: number[]; maxPointsPerEffort: number };
		calls.push({ url: String(url), effortIds: body.effortIds, maxPointsPerEffort: body.maxPointsPerEffort });
		return { ok: true, status: 200, json: async () => wireResponse(body.effortIds) } as unknown as Response;
	}) as unknown as typeof fetch;
}

// Fake page context with `count` efforts: effortIndex i ↔ gpsInSegmentId (i+1); `truncated` marks which
// indices are downsampled (and therefore need a full-res refetch). Only the fields the loader reads
// (config.segmentId, config.effortRows, performanceData) are populated — the rest is cast away.
function makeCtx(count: number, truncated: (i: number) => boolean): SegmentStatisticsContext {
	const effortRows = Array.from({ length: count }, (_, i) => ({
		effortIndex: i, colorIndex: i % 10, gpsInSegmentId: i + 1, activityId: 100 + i,
		activityName: 'E' + i, activityType: 'Run', isOwn: true, userDisplayName: null,
		dateIso: '2026-01-01', matchPct: 100, elapsedSec: 100, distanceM: 500, avgSpeedMs: 5,
		avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0,
	}));
	const performanceData = Array.from({ length: count }, (_, i) => ({
		effortId: i + 1, activityId: 100 + i, elapsedTime: 100, pointsTruncated: truncated(i),
		points: [
			{ distance: 0, time: 0, speed: 5, altitude: null, lat: 1, lng: 10 },
			{ distance: 100, time: 50, speed: 5, altitude: null, lat: 2, lng: 12 },
		] as unknown as EffortDataPoint[],
	}));
	return { config: { segmentId: 42, effortRows }, performanceData } as unknown as SegmentStatisticsContext;
}

beforeEach(() => { _resetFullResCache(); });

afterEach(() => {
	vi.restoreAllMocks();
	vi.unstubAllGlobals();
});

describe('segment-stats-replay-data — PS-03 lazy full-res', () => {
	it('skips efforts already full-res (pointsTruncated === false)', async () => {
		// Only effort 0 is truncated; 1 and 2 are already full-res.
		const ctx = makeCtx(3, (i) => i === 0);
		const calls: FetchCall[] = [];
		vi.stubGlobal('fetch', recordingFetch(calls));

		const ready: number[] = [];
		await loadFullResEfforts(ctx, [0, 1, 2], (idxs) => ready.push(...idxs), new AbortController().signal);

		expect(calls).toHaveLength(1);
		const call0 = must(calls[0], 'one fetch call');
		expect(call0.effortIds).toEqual([1]); // only truncated effort 0 → gpsInSegmentId 1
		expect(call0.maxPointsPerEffort).toBe(0);
		expect(ready).toEqual([0]);
	});

	it('fetches truncated efforts in 50-effort chunks with maxPointsPerEffort 0', async () => {
		const ctx = makeCtx(60, () => true); // all truncated
		const calls: FetchCall[] = [];
		vi.stubGlobal('fetch', recordingFetch(calls));

		const visible = Array.from({ length: 60 }, (_, i) => i);
		await loadFullResEfforts(ctx, visible, () => {}, new AbortController().signal);

		expect(calls).toHaveLength(2);             // 60 → 50 + 10
		expect(must(calls[0], 'first chunk').effortIds).toHaveLength(50);
		expect(must(calls[1], 'second chunk').effortIds).toHaveLength(10);
		expect(calls.every((c) => c.maxPointsPerEffort === 0)).toBe(true);
	});

	it('session cache prevents a refetch on a second replay', async () => {
		const ctx = makeCtx(3, () => true); // all truncated
		const calls: FetchCall[] = [];
		vi.stubGlobal('fetch', recordingFetch(calls));

		await loadFullResEfforts(ctx, [0, 1, 2], () => {}, new AbortController().signal);
		expect(calls).toHaveLength(1); // one chunk (3 efforts)

		// Second replay of the same efforts — cache hit, no new fetch.
		await loadFullResEfforts(ctx, [0, 1, 2], () => {}, new AbortController().signal);
		expect(calls).toHaveLength(1);
	});
});
