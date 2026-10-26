// @vitest-environment node
import type { EffortPerformanceData, EffortDataPoint } from './segment-stats-effort-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { describe, it, expect } from 'vitest';
import { pickDeltaReferenceIndex, createDeltaSystem } from './segment-stats-delta';
import { must } from '../core/invariant';

// SC2-08 pin: the delta reference effort prefers the first *visible* loaded effort (so the zero-line
// baseline is on screen), except an explicit selection always wins even when hidden.
//
// pickDeltaReferenceIndex and createDeltaSystem now RECEIVE ctx (delta's `deps.ctx`), so these
// pins drive them with a fake context — no #segmentStatisticsConfig island, no vi.resetModules.

function mkEffort(idx: number): EffortPerformanceData {
	const points = [
		{ distance: 0, time: 0, speed: 5, altitude: 0, lat: 0, lng: 0 },
		{ distance: 100, time: 10, speed: 5, altitude: 0, lat: 0, lng: 0 },
	] as unknown as EffortDataPoint[];
	return { effortId: idx, activityId: 100 + idx, activityName: 'E' + idx, activityDate: '2026-01-01', elapsedTime: 10, points };
}

// Minimal fake context — only the fields the delta helpers read (performanceData, the visible-effort
// set, the pinned selection, the per-chart delta flags). effortColors is unused by the paths tested here.
function mkCtx(perf: EffortPerformanceData[], visible: number[], selected: number | null): SegmentStatisticsContext {
	const effortVisibility = new Set<number>(visible);
	return {
		performanceData: perf,
		effortVisibility,
		selectedEffortIndex: selected,
		deltaCharts: new Set<string>(),
		currentXAxis: 'time',
		effortColors: ['#aa0000', '#00aa00', '#0000aa'],
		getVisibleEffortIndices: () => Array.from(effortVisibility),
	} as unknown as SegmentStatisticsContext;
}

describe('pickDeltaReferenceIndex — SC2-08', () => {
	const perf = () => [mkEffort(0), mkEffort(1), mkEffort(2)];
	it('uses the first visible loaded effort when nothing is selected', () => {
		expect(pickDeltaReferenceIndex(mkCtx(perf(), [1, 2], null))).toBe(1);
	});
	it('keeps an explicit selection as the reference even if hidden', () => {
		expect(pickDeltaReferenceIndex(mkCtx(perf(), [1], 2))).toBe(2);
	});
	it('falls back to the first loaded effort when nothing is visible', () => {
		expect(pickDeltaReferenceIndex(mkCtx(perf(), [], null))).toBe(0);
	});
});

// interpMetricAt (private) is the entire "Δ view" feature's math (finding #9) — every other delta test
// picks WHICH effort is the reference (pickDeltaReferenceIndex above) but never checks the interpolated
// VALUES a delta line actually draws. Driven here through seriesProvider (the public surface that wraps
// it), with deltaCharts populated, so a binary-search bound bug or span<=0 mishandling would fail these.
function effortAt(idxAndDistances: [number, number[]], altitudes: number[]): EffortPerformanceData {
	const [idx, distances] = idxAndDistances;
	const points = distances.map((d, i) => (
		{ distance: d, time: d, speed: 5, altitude: altitudes[i], lat: 0, lng: 0 }
	)) as unknown as EffortDataPoint[];
	return { effortId: idx, activityId: 100 + idx, activityName: 'E' + idx, activityDate: '2026-01-01', elapsedTime: distances[distances.length - 1] ?? 0, points };
}

describe('interpMetricAt via seriesProvider (finding #9)', () => {
	it('clamps below the reference range, interpolates linearly inside it, and clamps above it', () => {
		// Reference (index 0): x=[0,10,20], altitude=[100,110,130].
		const reference = effortAt([0, [0, 10, 20]], [100, 110, 130]);
		// Target (index 1): flat altitude=100 at x=[0,5,15,25] — probes clamp-low, midpoint, midpoint, clamp-high.
		const target = effortAt([1, [0, 5, 15, 25]], [100, 100, 100, 100]);
		const ctx = mkCtx([reference, target], [0, 1], 0); // selection 0 pins the reference (no SC2-08 ambiguity)
		ctx.deltaCharts.add('elevation');

		const system = createDeltaSystem({
			ctx,
			statsBorderColor: (c) => c,
			effortPointX: (p) => p.distance, // x-axis = raw distance, so expected values are hand-checkable
		});
		const series = system.seriesProvider('elevation')(1);

		expect(series).toHaveLength(4);
		expect(series[0]).toEqual({ x: 0, y: 0 });     // x<=xs[0]: clamped to ys[0]=100 → 100-100
		expect(must(series[1], 'point 1').y).toBeCloseTo(-5, 10);        // x=5, halfway 0..10 (100..110) → interp 105 → 100-105
		expect(must(series[2], 'point 2').y).toBeCloseTo(-20, 10);       // x=15, halfway 10..20 (110..130) → interp 120 → 100-120
		expect(series[3]).toEqual({ x: 25, y: -30 });  // x>=xs[n-1]: clamped to ys[n-1]=130 → 100-130
	});

	it('total (non-delta) mode draws the raw metric untouched — no reference subtraction at all', () => {
		const reference = effortAt([0, [0, 10]], [100, 200]);
		const target = effortAt([1, [0, 10]], [50, 60]);
		const ctx = mkCtx([reference, target], [0, 1], 0); // 'elevation' NOT in delta mode

		const system = createDeltaSystem({ ctx, statsBorderColor: (c) => c, effortPointX: (p) => p.distance });
		const series = system.seriesProvider('elevation')(1);

		expect(series).toEqual([{ x: 0, y: 50 }, { x: 10, y: 60 }]);
	});

	it('a single-point reference has zero span everywhere — every x clamps to that one value', () => {
		const reference = effortAt([0, [5]], [77]);
		const target = effortAt([1, [0, 5, 10]], [77, 77, 77]);
		const ctx = mkCtx([reference, target], [0, 1], 0);
		ctx.deltaCharts.add('elevation');

		const system = createDeltaSystem({ ctx, statsBorderColor: (c) => c, effortPointX: (p) => p.distance });
		const series = system.seriesProvider('elevation')(1);

		expect(series.map((p) => p.y)).toEqual([0, 0, 0]);
	});
});
