// @vitest-environment node
import type { EffortPerformanceData, EffortDataPoint } from './segment-stats-effort-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { describe, it, expect } from 'vitest';
import { computeMaxX, rawXExtent, visibleYData } from './segment-stats-charts-scale';

// SC2-05 split pins for the extracted scale/extent helpers. computeMaxX caps the initial view at a
// robust P90 when the longest effort is a wild outlier, while rawXExtent (the wheel/pan clamp) returns
// the true raw extent so zoom-out still reaches it (the CG-02 wheel-bounds contract).
//
// these helpers now RECEIVE ctx as a parameter, so the test drives them with a fake context
// object — no #segmentStatisticsConfig island, no real ./segment-statistics import, no vi.resetModules.

function mkEffort(idx: number, lastTime: number): EffortPerformanceData {
	const points = [
		{ distance: 0, time: 0, speed: 5, altitude: 0, lat: 0, lng: 0 },
		{ distance: 100, time: lastTime, speed: 5, altitude: 0, lat: 0, lng: 0 },
	] as unknown as EffortDataPoint[];
	return { effortId: idx, activityId: 100 + idx, activityName: 'E' + idx, activityDate: '2026-01-01', elapsedTime: lastTime, points };
}

// Minimal fake context — only the fields the scale helpers read (performanceData, the visible-effort
// set, the x-axis mode, aggregateMode). getVisibleEffortIndices mirrors the real ctx's derivation.
function mkCtx(perf: EffortPerformanceData[], visible: number[], xAxis = 'time'): SegmentStatisticsContext {
	const effortVisibility = new Set<number>(visible);
	return {
		performanceData: perf,
		effortVisibility,
		currentXAxis: xAxis,
		aggregateMode: false,
		getVisibleEffortIndices: () => Array.from(effortVisibility),
	} as unknown as SegmentStatisticsContext;
}

describe('segment-stats-charts-scale — SC2-05 pins', () => {
	it('computeMaxX returns 100 in percent mode', () => {
		const ctx = mkCtx([mkEffort(0, 40)], [0], 'percent');
		expect(computeMaxX(ctx, true)).toBe(100);
	});

	it('computeMaxX caps at the robust P90 while rawXExtent returns the raw extent', () => {
		// 9 efforts at 10s + one 100s outlier: P90 ≈ 10, so the initial view opens ~10.5, raw extent = 100.
		const perf: EffortPerformanceData[] = [];
		for (let i = 0; i < 9; i++) perf.push(mkEffort(i, 10));
		perf.push(mkEffort(9, 100));
		const ctx = mkCtx(perf, perf.map((_, i) => i));
		expect(computeMaxX(ctx, false)).toBeCloseTo(10.5, 5);
		expect(rawXExtent(ctx)).toEqual({ min: 0, max: 100 });
	});

	it('computeMaxX falls back to all efforts when the visible set is empty', () => {
		const ctx = mkCtx([mkEffort(0, 40), mkEffort(1, 60)], []);
		expect(computeMaxX(ctx, false)).toBe(60); // <3 efforts → absMax over the whole set
	});

	it('visibleYData is index-filtered in normal mode, all datasets in aggregate mode', () => {
		const ctx = mkCtx([mkEffort(0, 10), mkEffort(1, 10), mkEffort(2, 10)], [0, 2]);
		const datasets = [{ data: [{ y: 1 }] }, { data: [{ y: 2 }] }, { data: [{ y: 3 }] }];
		expect(visibleYData(ctx, datasets)).toEqual([[{ y: 1 }], [{ y: 3 }]]);
		ctx.aggregateMode = true;
		expect(visibleYData(ctx, datasets)).toEqual([[{ y: 1 }], [{ y: 2 }], [{ y: 3 }]]);
	});
});
