// =============================================================================
// Segment chart delta system
// -----------------------------------------------------------------------------
// All "delta vs reference effort" logic for the speed / elevation / effort-cost
// charts lives here: picking the baseline, interpolating the reference metric,
// and building / refreshing a chart's datasets. Each chart can independently
// switch into delta mode — every effort is drawn as its value MINUS the
// reference effort's value at the same x (selected effort, else fastest), so the
// reference line sits on zero. Total mode (default) draws the raw metric.
//
// `createDeltaSystem` takes the two small charts.ts-local helpers it needs
// (statsBorderColor, effortPointX) as dependencies, keeping the dataset/Chart.js
// wiring in segment-stats-charts.ts thin while this logic stays in one place.
// =============================================================================

import type { ChartJsInstance } from '../core/core-types';
import { scaleInternals } from '../core/chart-internals';
import { must } from '../core/invariant';
import type { EffortPerformanceData, EffortDataPoint } from './segment-stats-effort-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { robustYBounds, visibleYSeries } from '../core/chart-robust-scale';
import { gaussianSmoothTime, getSpeedSmoothingSeconds } from '../core/smoothing';
import { binnedMean } from '../core/series-binning';
import type { SeriesMode } from '../core/chart-series-modes';
import { setLineSelection } from '../core/chart-line-select';
import { formatElapsedHms } from './seg-stats-shared';
import { getAccountSettings } from '../core/account-settings';

/** Chart keys that support the delta toggle. */
export type ChartKey = "speed" | "elevation" | "effort";

/** A plotted point in the active x-axis mode, with delta already applied when the chart is in delta mode. */
export interface XYPoint { x: number; y: number; }

/** X-value of an effort point in the active x-axis mode (time seconds or % complete). */
export type PointXFn = (p: EffortDataPoint, totalDistance: number) => number;

/** Reference curve: parallel x / metric-value arrays, ascending in x. */
interface MetricReference {
	xs: number[];
	ys: number[];
}

/**
 * Reference effort for the delta view: the user's selected effort if one is
 * highlighted, otherwise the first *visible* loaded effort (SC2-08), else the
 * first loaded effort. Returns -1 when there is no data.
 * @internal exported for tests.
 */
export function pickDeltaReferenceIndex(ctx: SegmentStatisticsContext): number {
	const data = ctx.performanceData;
	if (!data || data.length === 0) return -1;
	const sel = ctx.selectedEffortIndex;
	// During progressive load some entries are placeholders (points: []) — only reference a loaded one.
	// A *selected* effort stays the reference even if hidden (an explicit pin wins, matching
	// applyStatsDimming's pin semantics).
	if (sel != null && sel >= 0 && sel < data.length && data[sel]?.points.length) return sel;
	// SC2-08: prefer the first visible loaded effort so the delta baseline is on screen. Hiding the
	// fastest (index 0) with a delta chart on otherwise baselines every line against a line that isn't
	// drawn (the zero-line then corresponds to nothing visible).
	const vis = ctx.getVisibleEffortIndices().filter((i) => data[i]?.points.length).sort((a, b) => a - b);
	if (vis.length) return must(vis[0], 'non-empty (guarded above)');
	const first = data.findIndex((e) => e && e.points.length > 0);
	return first >= 0 ? first : 0;
}

/**
 * Build the reference effort's (x, metric) lookup in ascending-x order. `ys` is
 * supplied by the caller (already aligned to `effort.points`) so callers can pass
 * derived series such as smoothed speed, not just a raw point field.
 */
function buildMetricReference(effort: EffortPerformanceData, pointX: PointXFn, ys: number[]): MetricReference {
	const points = effort.points;
	const totalDistance = points.length ? must(points.at(-1), 'non-empty (guarded)').distance : 0;
	const xs: number[] = new Array(points.length);
	for (let i = 0; i < points.length; i++) {
		xs[i] = pointX(must(points[i], 'i < length (loop-bounded)'), totalDistance);
	}
	return { xs, ys };
}

/**
 * Linearly interpolate the reference metric at x. Clamps to the curve's endpoints
 * outside its range (an effort that runs longer/further than the reference keeps
 * the reference's final value as the baseline).
 */
function interpMetricAt(ref: MetricReference, x: number): number {
	const { xs, ys } = ref;
	const n = xs.length;
	if (n === 0) return 0;
	// ?? NaN through this kernel: every index sits in [0, n-1] and xs/ys are lockstep by the
	// MetricReference contract — dead fallbacks; NaN compares/propagates exactly as undefined did.
	if (x <= (xs[0] ?? NaN)) return ys[0] ?? NaN;
	if (x >= (xs[n - 1] ?? NaN)) return ys[n - 1] ?? NaN;

	// Binary search for the first index with xs[idx] >= x.
	let lo = 0, hi = n - 1;
	while (lo < hi) {
		const mid = (lo + hi) >> 1;
		if ((xs[mid] ?? NaN) < x) lo = mid + 1;
		else hi = mid;
	}
	const i1 = lo;
	const i0 = i1 - 1;
	const span = (xs[i1] ?? NaN) - (xs[i0] ?? NaN);
	if (span <= 0) return ys[i1] ?? NaN;
	const t = (x - (xs[i0] ?? NaN)) / span;
	return (ys[i0] ?? NaN) + t * ((ys[i1] ?? NaN) - (ys[i0] ?? NaN));
}

interface MetricConfig {
	titleBase: string;   // chart title prefix, e.g. "Speed"
	unit: string;        // hover-readout unit suffix incl. leading space, e.g. " km/h"
	decimals: number;    // hover-readout decimal places
	baseBeginAtZero: boolean;                 // y beginAtZero when NOT in delta
	chart: () => ChartJsInstance | null;
	yOf: (effort: EffortPerformanceData) => number[];  // per-point series, aligned to effort.points
}

/** Dependencies provided by the charts module (kept there as they have other uses). */
export interface DeltaDeps {
	/** Live page context, RECEIVED instead of reached for via the module singleton. */
	ctx: SegmentStatisticsContext;
	/** Effort line colour, dimmed when the effort's statsbox row is unchecked. */
	statsBorderColor: (color: string, index: number) => string;
	/** Per-point x in the active x-axis mode. */
	effortPointX: PointXFn;
}

export interface DeltaSystem {
	buildMetricDatasets: (key: ChartKey, borderWidth: number, tension: number) => unknown[];
	refreshDeltaChart: (key: ChartKey) => void;
	makeFormatter: (key: ChartKey) => (v: number | null | undefined) => string;
	chartTitle: (key: ChartKey) => string;
	chartIsDelta: (key: ChartKey) => boolean;
	/** A provider for one effort's plotted {x,y} series for the given chart (delta + x-mode applied once).
	 * Aggregate mode reuses this so band/median bins are computed from the exact plotted values. */
	seriesProvider: (key: ChartKey) => (index: number) => XYPoint[];
	/** Switch the speed series between the recorded channel and the smoothed one. Caller redraws. */
	setSpeedMode: (mode: SeriesMode) => void;
}

export function createDeltaSystem(deps: DeltaDeps): DeltaSystem {
	const { ctx, statsBorderColor, effortPointX } = deps;

	let speedMode: SeriesMode = 'smoothBin';

	// The same estimator the details-page speed chart uses: a Gaussian over a window of SECONDS (effort
	// points carry their own elapsed time, so the window means the same riding however densely the
	// device sampled), then quantised into distance bins and drawn through the bin centres.
	//
	// Binned PER EFFORT, over that effort's own distance — each line is its own ride, so they cannot
	// share a bin grid.
	function smoothedSpeeds(effort: EffortPerformanceData): number[] {
		const raw = effort.points.map((p) => p.speed);
		if (speedMode === 'recorded') return raw;
		const times = effort.points.map((p) => p.time);
		const smoothed = gaussianSmoothTime(raw, times, getSpeedSmoothingSeconds());
		const distances = effort.points.map((p) => p.distance);
		return binnedMean(smoothed, distances, 0, smoothed.length - 1, getAccountSettings().charts.speedBinCount);
	}

	const METRICS: Record<ChartKey, MetricConfig> = {
		speed: {
			titleBase: "Speed", unit: " km/h", decimals: 1, baseBeginAtZero: true,
			chart: () => ctx.performanceChart, yOf: smoothedSpeeds,
		},
		elevation: {
			titleBase: "Elevation", unit: " m", decimals: 0, baseBeginAtZero: false,
			chart: () => ctx.elevationChart, yOf: (e) => e.points.map((p) => p.altitude ?? 0),
		},
		effort: {
			titleBase: "Effort cost", unit: " kJ", decimals: 1, baseBeginAtZero: false,
			chart: () => ctx.effortChart, yOf: (e) => e.points.map((p) => p.cumulativeCostKj ?? 0),
		},
	};

	function chartIsDelta(key: ChartKey): boolean {
		return ctx.deltaCharts.has(key);
	}

	function chartTitle(key: ChartKey): string {
		const xAxisLabel = ctx.currentXAxis === "percent" ? "% Complete" : "Time";
		const base = METRICS[key].titleBase;
		return (chartIsDelta(key) ? "Δ " + base + " vs " : base + " vs ") + xAxisLabel;
	}

	// Hover-readout value formatter; reads delta state live so it adds a +/- sign in delta mode.
	function makeFormatter(key: ChartKey): (v: number | null | undefined) => string {
		const m = METRICS[key];
		return (v) => {
			if (v == null) return "--";
			const sign = chartIsDelta(key) && v > 0 ? "+" : "";
			return sign + v.toFixed(m.decimals) + m.unit;
		};
	}

	// One effort's plotted {x,y} series for `key`: applies the live x-axis mode and (if the chart is in
	// delta mode) subtracts the reference effort's interpolated value. `seriesProvider` builds the shared
	// reference ONCE per chart, then returns this per-effort closure — reused by both buildMetricDatasets
	// and the aggregate band/median binning so the two never diverge on delta/x-mode handling.
	function seriesProvider(key: ChartKey): (index: number) => XYPoint[] {
		const data = ctx.performanceData;
		const m = METRICS[key];
		const delta = chartIsDelta(key);
		const refIndex = delta && data ? pickDeltaReferenceIndex(ctx) : -1;
		const refLookup = refIndex >= 0 && data?.[refIndex]
			? buildMetricReference(data[refIndex], effortPointX, m.yOf(data[refIndex]))
			: null;
		return (index: number): XYPoint[] => {
			const effort = data?.[index];
			if (!effort) return [];
			const points = effort.points;
			const totalDistance = points.length ? must(points.at(-1), 'non-empty (guarded)').distance : 0;
			const ys = m.yOf(effort);
			const xyData: XYPoint[] = new Array(points.length);
			for (let i = 0; i < points.length; i++) {
				const x = effortPointX(must(points[i], 'i < length (loop-bounded)'), totalDistance);
				const y = ys[i] ?? 0;
				xyData[i] = { x, y: refLookup ? y - interpMetricAt(refLookup, x) : y };
			}
			return xyData;
		};
	}

	// Builds a chart's datasets from ctx.performanceData using the live x-axis + per-chart delta
	// state. Delta subtracts the reference effort's interpolated value at each x; the effort-cost
	// chart additionally dashes estimated (no power meter) efforts.
	function buildMetricDatasets(key: ChartKey, borderWidth: number, tension: number): unknown[] {
		const data = ctx.performanceData;
		if (!data) return [];
		const visible = ctx.getVisibleEffortIndices();
		const seriesOf = seriesProvider(key);

		// Entries may be placeholders (points: []) during progressive load — they naturally yield an
		// empty dataset below, keeping the dataset position equal to effortIndex until the chunk lands.
		return data.map((effort: EffortPerformanceData, index: number) => {
			const color = must(ctx.effortColors[index % ctx.effortColors.length], 'in-bounds modulo of non-empty palette');
			const timeStr = formatElapsedHms(effort.elapsedTime);
			return {
				label: effort.activityName + " (" + timeStr + ")",
				data: seriesOf(index),
				borderColor: statsBorderColor(color, index),
				backgroundColor: color + "20",
				borderWidth,
				borderDash: key === "effort" && effort.costEstimated ? [6, 4] : undefined,
				fill: false,
				tension,
				pointRadius: 0,
				pointHoverRadius: 0,
				hidden: !visible.includes(index),
				parsing: false as const,
				normalized: true,
				_effortIndex: index, // maps this dataset back to its effort for hover/selection (see aggregate mode)
			};
		});
	}

	// Rebuilds ONLY the given chart's data in place (delta toggle / selection change), preserving
	// x-zoom. Clears the frozen y-extent + sets beginAtZero so the total↔delta range swap re-fits,
	// updates the title (read live), and re-applies any surviving line selection.
	function refreshDeltaChart(key: ChartKey): void {
		const m = METRICS[key];
		const chart = m.chart();
		if (!chart) return;
		const borderWidth = getAccountSettings().charts.borderWidth;
		const tension = getAccountSettings().charts.tension;
		const datasets = buildMetricDatasets(key, borderWidth, tension);
		chart.data.datasets = datasets as typeof chart.data.datasets;

		const titleOpts = chart.options.plugins?.title as { text?: string } | undefined;
		if (titleOpts) titleOpts.text = chartTitle(key);

		// Re-fit the y-axis to robust (P1/P99) bounds over the visible efforts' *new* values — the
		// total↔delta swap changes the range entirely (delta centres on 0). Outliers clip off the plot.
		const beginAtZero = !chartIsDelta(key) && m.baseBeginAtZero;
		const bounds = robustYBounds(visibleYSeries(datasets as Array<{ data?: Array<{ y?: number | null }> }>, ctx.getVisibleEffortIndices()), { beginAtZero });
		const yScale = chart.scales.y ? scaleInternals(chart.scales.y) : undefined;
		if (yScale) {
			yScale.options.min = bounds ? bounds.min : undefined;
			yScale.options.max = bounds ? bounds.max : undefined;
			yScale.options.beginAtZero = beginAtZero;
		}

		chart.update("none");
		if (ctx.selectedEffortIndex != null)
			setLineSelection(chart, ctx.selectedEffortIndex);
	}

	/** Switch the speed series between the recorded channel and the smoothed one. Caller redraws. */
	function setSpeedMode(mode: SeriesMode): void { speedMode = mode; }

	return {
		buildMetricDatasets, refreshDeltaChart, makeFormatter, chartTitle, chartIsDelta, seriesProvider,
		setSpeedMode,
	};
}
