// =============================================================================
// Robust axis scaling — outlier-tolerant bounds (generic chart math, core/)
// -----------------------------------------------------------------------------
// Lives in core/ (moved from segment-stats/ in the chart-kernel pile) so any chart
// — segment comparison, Statistics, Details — can reach it without a cross-feature
// import. Pure functions, no DOM, no Chart.js dependency.
//
// Heatmap segments mix normal efforts with rare outliers: one 60 h ride vs mostly
// ~10 h, or a GPS speed spike of 500 km/h vs 0–30. Chart.js autoscale stretches
// the axis to the outlier and squashes every real line into a sliver. These
// helpers derive percentile-based bounds so the plot fits the bulk of the data;
// outlier points simply clip off the plot area (Y) or sit past the initial view
// but are still reachable by zooming out (X — the wheel/pan clamp uses the raw
// data extent, not this capped max).
// =============================================================================
import { must } from './invariant';

/**
 * Plotted {x,y} arrays of the visible efforts, for robustYBounds. Works on any dataset array indexed
 * by effortIndex (build-time datasets or a live chart.data.datasets) — visibility is the same index
 * set in both. Reads the actual plotted points, so delta-mode values get measured too.
 */
export function visibleYSeries(
	datasets: Array<{ data?: Array<{ y?: number | null }> }>,
	visibleIndices: number[],
): Array<Array<{ y?: number | null }>> {
	const out: Array<Array<{ y?: number | null }>> = [];
	for (const i of visibleIndices) {
		const d = datasets[i]?.data;
		if (d && d.length) out.push(d);
	}
	return out;
}

/** Nearest-rank percentile of an already-ascending array. */
function percentile(sorted: number[], p: number): number {
	const n = sorted.length;
	if (n === 0) return NaN;
	// ?? NaN: both indices are clamped inside [0, n-1] — unreachable, and NaN is already this
	// function's empty-input sentinel.
	if (n === 1) return sorted[0] ?? NaN;
	const idx = Math.min(n - 1, Math.max(0, Math.round(p * (n - 1))));
	return sorted[idx] ?? NaN;
}

/** Cap total sampled y-values so bounds stay O(1)-ish regardless of effort count. */
const MAX_Y_SAMPLES = 50_000;

/**
 * P1/P99 y-bounds over the visible efforts' plotted values, padded 5%. Sampling
 * strides across all series so hundreds of efforts still cost ~50 k reads.
 * `beginAtZero` pins the floor to 0 (speed chart, total mode). Returns null when
 * there is nothing to measure (all placeholders / empty) — caller keeps autoscale.
 */
export function robustYBounds(
	series: Array<Array<{ y?: number | null }>>,
	opts: { beginAtZero: boolean },
): { min: number; max: number } | null {
	let total = 0;
	for (const s of series) total += s.length;
	if (total === 0) return null;

	const stride = Math.max(1, Math.ceil(total / MAX_Y_SAMPLES));
	const samples: number[] = [];
	for (const s of series) {
		for (let i = 0; i < s.length; i += stride) {
			const y = s[i]?.y;
			if (y != null && Number.isFinite(y)) samples.push(y);
		}
	}
	if (samples.length === 0) return null;

	samples.sort((a, b) => a - b);
	const p1 = percentile(samples, 0.01);
	const p99 = percentile(samples, 0.99);
	const pad = (p99 - p1) * 0.05 || Math.abs(p99) * 0.05 || 1;
	const min = opts.beginAtZero ? 0 : p1 - pad;
	const max = p99 + pad;

	// Snap the pinned bounds OUTWARD to a nice grid (~1/10 of the span's magnitude).
	// Chart.js renders the exact min/max as the edge ticks, so raw percentile+pad values
	// would show up as "71.19500000000001" / "5.305" next to the nice 10/20/… ticks.
	const span = max - min;
	if (!(span > 0)) return { min, max };
	const pow = Math.pow(10, Math.floor(Math.log10(span)) - 1);
	const dec = Math.max(0, -Math.floor(Math.log10(pow)));
	return {
		min: Number((Math.floor(min / pow) * pow).toFixed(dec)),
		max: Number((Math.ceil(max / pow) * pow).toFixed(dec)),
	};
}

/**
 * Initial/reset X max (time mode only). When the longest effort is a wild outlier
 * (> P90 × 1.3) the view opens at P90 × 1.05 so the bulk fills the plot; zoom-out
 * still reaches the outlier (clamp uses the raw extent). Otherwise the true max.
 */
export function robustXMax(perEffortLastX: number[]): number | undefined {
	const xs = perEffortLastX.filter((x) => Number.isFinite(x) && x > 0).sort((a, b) => a - b);
	if (xs.length === 0) return undefined;
	const absMax = must(xs.at(-1), 'non-empty (guarded above)');
	if (xs.length < 3) return absMax; // too few to judge an outlier
	const p90 = percentile(xs, 0.9);
	return absMax > p90 * 1.3 ? p90 * 1.05 : absMax;
}
