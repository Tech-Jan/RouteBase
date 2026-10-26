/**
 * Segment Statistics — chart scale/extent helpers (extracted from segment-stats-charts.ts, SC2-05).
 *
 * Pure x/y extent + tick math. The live context is RECEIVED as a parameter rather than
 * reached for via the module singleton, so a test drives them with a fake ctx and no vi.mock.
 * No side effects, no chart wiring — the core charts module (and its tests) call these directly.
 */
import type { EffortDataPoint } from './segment-stats-effort-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { robustXMax, visibleYSeries } from '../core/chart-robust-scale';
import { must } from '../core/invariant';

// X-value of a point in the current x-axis mode. Mirrors the per-render `pointX`
// helper but reads ctx.currentXAxis live so the elevation backdrop plugin (which
// runs on every redraw) stays in sync without rebuilding.
export function effortPointX(ctx: SegmentStatisticsContext, p: EffortDataPoint, totalDistance: number): number {
	if (ctx.currentXAxis !== "percent") return p.time;
	if (p.segmentProgress != null) return p.segmentProgress * 100;
	return totalDistance > 0 ? (p.distance / totalDistance) * 100 : 0;
}

// X-axis max for the comparison charts' initial/reset view. Percent mode always spans 0–100; in time
// mode it's a robust cap over the *visible* efforts (robustXMax): the longest effort normally, but a
// wild outlier (one 60 h ride vs mostly 10 h) opens at ~P90 so the bulk fills the plot — zoom-out
// still reaches it (the wheel/pan clamp uses the raw extent). Falls back to all efforts when the
// visible set is empty (avoids a 0/undefined axis during init, before the efforts table pushes one).
export function computeMaxX(ctx: SegmentStatisticsContext, usePercent: boolean): number | undefined {
	if (usePercent) return 100;
	const data = ctx.performanceData;
	if (!data || data.length === 0) return undefined;
	const visible = ctx.getVisibleEffortIndices();
	const indices = visible.length ? visible : data.map((_, i) => i);
	const lastXs: number[] = [];
	for (const i of indices) {
		const pts = data[i]?.points;
		if (!pts || !pts.length) continue;
		lastXs.push(must(pts.at(-1), 'non-empty (guarded above)').time);
	}
	return robustXMax(lastXs);
}

// Y-series feeding robustYBounds. Normal mode: visibleYSeries keyed by effortIndex (meta.hidden and
// the build-time .hidden flag both derive from ctx.getVisibleEffortIndices()). Aggregate mode: datasets
// are band/median/highlighted (NOT effort-indexed) — every one is on-screen, so take them all.
export function visibleYData(ctx: SegmentStatisticsContext, datasets: Array<{ data?: Array<{ y?: number | null }> }>): Array<Array<{ y?: number | null }>> {
	if (ctx.aggregateMode) {
		return datasets.filter((d) => d.data && d.data.length).map((d) => d.data as Array<{ y?: number | null }>);
	}
	return visibleYSeries(datasets, ctx.getVisibleEffortIndices());
}

// True (UNCAPPED) x-extent over all efforts — the zoom-out clamp for the wheel handler. computeMaxX
// caps the *initial* view at a percentile, but wheel/pan must still reach the outlier (e.g. a 60 h
// effort), so the clamp uses the raw extent (mirrors setupPanning's _ensureBoundsCache for drag/pinch).
export function rawXExtent(ctx: SegmentStatisticsContext): { min: number; max: number } {
	if (ctx.currentXAxis === "percent") return { min: 0, max: 100 };
	let maxX = 0;
	for (const e of ctx.performanceData ?? []) {
		const pts = e?.points;
		if (pts && pts.length) { const last = must(pts.at(-1), 'non-empty (guarded)').time; if (last > maxX) maxX = last; }
	}
	return { min: 0, max: maxX > 0 ? maxX : 100 };
}

// Tick callback helper: the index-0 (bottom/left-most) tick shows the bare
// unit while the chart is at rest, freeing the gutter the axis title used to
// occupy. Once zoomed/panned it reverts to the formatted value so the visible
// range stays readable.
export function cornerTick(
	chart: import("chart.js").Chart,
	value: number,
	index: number,
	unit: string,
	fmt: (v: number) => string,
): string {
	return index === 0 && !chart.isZoomedOrPanned() ? unit : fmt(value);
}
