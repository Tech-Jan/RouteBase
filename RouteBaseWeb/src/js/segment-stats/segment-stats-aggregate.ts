// =============================================================================
// Segment chart aggregate mode — density backdrop + P25–P75 band + median
// -----------------------------------------------------------------------------
// With hundreds of efforts, one line each is unreadable and slow. Aggregate mode
// collapses the visible NON-highlighted efforts into a P25–P75 band + a median
// line (hue-tinted per chart), plus a DenseLines-style density bitmap built from
// the same binned rows (see segment-stats-density.ts; drawn by the density
// plugin in segment-stats-charts.ts). Each HIGHLIGHTED effort (statsbox-checked
// or the pinned selection) is drawn as its own full-res line on top.
// Auto-engages past AGGREGATE_AUTO_THRESHOLD efforts (see segment-stats-effort-defaults.ts)
// unless toggled manually; that view opens with NO efforts highlighted.
//
// Values come from the delta system's seriesProvider so delta / x-axis handling
// is identical to normal mode. Datasets are tagged `_effortIndex` (+ `_aggRole`)
// — the mapping key every index-based path (hover, selection) uses in this mode.
// =============================================================================

import type { SegmentStatisticsContext } from './segment-stats-page-types';
import type { ChartKey, XYPoint } from './segment-stats-delta';
import { robustXMax } from '../core/chart-robust-scale';
import { formatElapsedHms } from './seg-stats-shared';
import { buildDensityImages, type DensityImage, type DensityRamp, type DensityRegionInput } from './segment-stats-density';
import { must } from '../core/invariant';

/**
 * Number of x-bins the band/median/density are sampled onto — deliberately ~6× a plot's
 * pixel width, built ONCE over the robust x-range. Zoom/pan never re-bins (a gesture-end
 * re-bin was tried and hitched): zooming in reveals the extra precomputed resolution.
 * NOTE: Chart.js decimation must stay OFF in aggregate mode (see makeMetricChart) — its
 * LTTB visible-window re-slicing warped the band on zoom-in (read as x-compression).
 */
const BIN_COUNT = 3000;

/** Bin cap for the outlier tail past the robust x-max. The tail keeps the main region's
 *  bin width where feasible — few efforts reach there, so precision hardly matters. */
const TAIL_BIN_COUNT_MAX = 1000;

// Per-chart aggregate palette (dark surface). Each chart keeps one hue — band and
// median are steps of the same ramp the density bitmap uses, so the whole chart
// reads as a single sequential encoding: faint = few efforts, saturated = many.
const AGG_STYLE: Record<ChartKey, { bandFill: string; medianStroke: string; ramp: DensityRamp }> = {
	speed: {
		bandFill: "rgba(57, 135, 229, 0.15)", medianStroke: "#a6c8f0",
		ramp: { low: "#123f7e", high: "#86b6ef", maxAlpha: 0.55 },
	},
	elevation: {
		bandFill: "rgba(201, 133, 0, 0.15)", medianStroke: "#e9c476",
		ramp: { low: "#5d4200", high: "#e2b35a", maxAlpha: 0.55 },
	},
	effort: {
		bandFill: "rgba(230, 103, 103, 0.15)", medianStroke: "#f2adad",
		ramp: { low: "#6e2424", high: "#ef9d9d", maxAlpha: 0.55 },
	},
};

const BAND_STROKE = "rgba(0, 0, 0, 0)"; // band edges invisible; only the fill reads

type AggRole = "band" | "median" | "effort";

/** Chart.js dataset plus the aggregate-mode tags used by hover/selection. */
export interface AggDataset {
	label: string;
	data: XYPoint[];
	borderColor: string;
	backgroundColor?: string | undefined;
	borderWidth: number;
	fill: boolean | string;
	tension: number;
	pointRadius: number;
	pointHoverRadius: number;
	borderDash?: number[] | undefined;
	parsing: false;
	normalized: true;
	_effortIndex: number | null;
	_aggRole: AggRole;
	/** Median only: efforts contributing at an x-value (readout box shows n at the cursor).
	 *  A lookup, not an array parallel to `data` — decimation resamples `data` at render time. */
	_binCountsAt?: ((x: number) => number) | undefined;
}

export interface AggregateDeps {
	/** Live page context, RECEIVED instead of reached for via the module singleton. */
	ctx: SegmentStatisticsContext;
	/** Per-effort plotted {x,y} provider for a chart (delta + x-mode already applied). */
	seriesProvider: (key: ChartKey) => (index: number) => XYPoint[];
}

/** Highlighted efforts drawn as individual lines: statsbox-checked ∪ the pinned selection. */
export function highlightedEffortIndices(ctx: SegmentStatisticsContext): Set<number> {
	const set = new Set<number>(ctx.effortStats);
	if (ctx.selectedEffortIndex != null) set.add(ctx.selectedEffortIndex);
	return set;
}

/** Nearest-rank percentile of an ascending array. */
function percentile(sorted: Float64Array, p: number): number {
	const n = sorted.length;
	if (n === 0) return NaN;
	// ?? NaN: indices clamped in [0, n-1] — dead, and NaN is already this function's empty sentinel.
	if (n === 1) return sorted[0] ?? NaN;
	return sorted[Math.min(n - 1, Math.max(0, Math.round(p * (n - 1))))] ?? NaN;
}

/**
 * Interpolate one effort's series onto ascending bin centers in a single merge-walk (O(bins + series)).
 * Bins outside the series' x-range get NaN (the effort doesn't reach there) and are skipped by the
 * percentile step, so a short effort only contributes to the bins it actually spans.
 */
function sampleOntoBins(series: XYPoint[], centers: number[], out: Float64Array): void {
	const n = series.length;
	if (n === 0) { out.fill(NaN); return; }
	const first = must(series[0], 'n >= 1 (guarded above)');
	const last = must(series[n - 1], 'n >= 1 (guarded above)');
	// ?? NaN in the merge-walk: the cursor keeps every index in bounds — dead fallbacks, and NaN
	// already IS this module's "no data here" sentinel, propagating exactly as undefined math did.
	let j = 0; // series cursor: first index with series[j].x >= center
	for (let b = 0; b < centers.length; b++) {
		const x = centers[b] ?? NaN;
		if (x < first.x || x > last.x) { out[b] = NaN; continue; }
		while (j < n && (series[j]?.x ?? NaN) < x) j++;
		if (j === 0) { out[b] = first.y; continue; }
		if (j >= n) { out[b] = last.y; continue; }
		const pj = must(series[j], 'j in (0, n) (guarded above)');
		const pj1 = must(series[j - 1], 'j-1 >= 0 (guarded above)');
		const span = pj.x - pj1.x;
		out[b] = span <= 0 ? pj.y
			: pj1.y + ((x - pj1.x) / span) * (pj.y - pj1.y);
	}
}

export function createAggregateSystem(deps: AggregateDeps) {
	const { ctx } = deps;
	// Density bitmaps per chart (one per bin region), rebuilt with the datasets; the backdrop
	// plugin blits them each draw (drawImage only — never a re-bin), skipped outside aggregate mode.
	const densityImages = new Map<ChartKey, DensityImage[]>();

	function getDensityImages(key: ChartKey): DensityImage[] {
		return densityImages.get(key) ?? [];
	}

	// SC2-07: drop all retained density bitmaps (~11 MB across 3 charts) when leaving aggregate mode.
	// They rebuild from scratch on the next aggregate toggle.
	function clearDensityImages(): void {
		densityImages.clear();
	}

	// Build the aggregate datasets for one chart: [bandLow, bandHigh, median, ...highlighted efforts].
	function buildAggregateDatasets(key: ChartKey, borderWidth: number, tension: number): AggDataset[] {
		const data = ctx.performanceData;
		if (!data) return [];
		const seriesOf = deps.seriesProvider(key);
		const highlighted = highlightedEffortIndices(ctx);
		const visible = ctx.getVisibleEffortIndices();
		const style = AGG_STYLE[key];

		// Band is computed over visible, non-highlighted efforts; highlighted ones are drawn individually.
		const bandSeries: XYPoint[][] = [];
		const lastXs: number[] = [];
		let xMin = Infinity, xMax = -Infinity;
		for (const i of visible) {
			const s = seriesOf(i);
			if (!s.length) continue;
			const sFirst = must(s[0], 'non-empty (guarded above)');
			const sLast = must(s[s.length - 1], 'non-empty (guarded above)');
			if (sFirst.x < xMin) xMin = sFirst.x;
			if (sLast.x > xMax) xMax = sLast.x;
			lastXs.push(sLast.x);
			if (!highlighted.has(i)) bandSeries.push(s);
		}
		const datasets: AggDataset[] = [];
		densityImages.delete(key);

		if (bandSeries.length > 0 && isFinite(xMin) && isFinite(xMax) && xMax > xMin) {
			// Two bin regions. The full BIN_COUNT resolution goes to the robust x-range the
			// initial view opens at (P90-capped when the longest effort is a wild outlier —
			// binning the raw extent uniformly would waste most bins on the tail and turn the
			// main window coarse the moment you zoom in). The tail past the cap is still
			// binned — zooming out shows band/median/density over the entire extent — at the
			// same bin width, capped at TAIL_BIN_COUNT_MAX. Percent mode: every last x ≈ 100,
			// so no tail region exists. Centers span first..last INCLUSIVE per region, so the
			// band/median reach the plot edges.
			// Main region: centers span xMin..mainMax INCLUSIVE (band/median reach the plot edge).
			// Tail region: CENTERED bins tiling (mainMax, xMax] — its first bin's coverage starts
			// exactly at the cap. Starting the tail's centers a whole (wide, capped) tail-bin after
			// the cap left a strip of x that no bin covered: an empty seam in the density with only
			// the straight median segment crossing it. imgX0/imgX1 = each bitmap's coverage; the
			// seam sits at exactly mainMax for both, so the images meet without gap or overlap.
			const robustMax = robustXMax(lastXs);
			const mainMax = robustMax != null && robustMax < xMax ? robustMax : xMax;
			const stepMain = (mainMax - xMin) / (BIN_COUNT - 1);
			const regions: Array<{ first: number; step: number; bins: number; imgX0: number; imgX1: number }> = [
				{ first: xMin, step: stepMain, bins: BIN_COUNT, imgX0: xMin - stepMain / 2, imgX1: mainMax },
			];
			if (stepMain > 0 && xMax > mainMax + stepMain) {
				const bins = Math.min(TAIL_BIN_COUNT_MAX, Math.max(16, Math.round((xMax - mainMax) / stepMain)));
				const step = (xMax - mainMax) / bins;
				regions.push({ first: mainMax + step / 2, step, bins, imgX0: mainMax, imgX1: xMax });
			}

			const low: XYPoint[] = [], high: XYPoint[] = [], mid: XYPoint[] = [];
			const densityRegions: DensityRegionInput[] = [];
			const countRegions: Array<{ first: number; step: number; counts: Uint16Array }> = [];

			for (const rg of regions) {
				const centers: number[] = new Array(rg.bins);
				for (let b = 0; b < rg.bins; b++) centers[b] = rg.first + b * rg.step;
				const regionEnd = centers[rg.bins - 1] ?? NaN; // dead: centers has rg.bins entries

				// One binning pass feeds both the percentiles and the density bitmap; only
				// efforts actually reaching this region get a row (the rest is all-NaN weight).
				const rows: Float64Array[] = [];
				for (const s of bandSeries) {
					// ?? NaN: NaN comparisons are false → the effort is not skipped, as with undefined.
					if ((s[s.length - 1]?.x ?? NaN) < rg.first || (s[0]?.x ?? NaN) > regionEnd) continue;
					const row = new Float64Array(rg.bins);
					sampleOntoBins(s, centers, row);
					rows.push(row);
				}
				densityRegions.push({ rows, xMin: rg.imgX0, xMax: rg.imgX1 });

				// Column-wise percentile pass over a reusable scratch: gather each bin's finite
				// values, native-sort the subarray (a number[][] with comparator sorts was the
				// hot spot at this bin count), then nearest-rank P25/P50/P75.
				const scratch = new Float64Array(rows.length);
				const counts = new Uint16Array(rg.bins);
				for (let b = 0; b < rg.bins; b++) {
					let m = 0;
					for (const row of rows) {
						const v = row[b] ?? NaN; // dead: b < rg.bins = row.length
						if (!Number.isNaN(v)) scratch[m++] = v;
					}
					if (m === 0) continue;
					const col = scratch.subarray(0, m);
					col.sort();
					counts[b] = m;
					const x = centers[b] ?? NaN; // dead: b < rg.bins = centers.length
					low.push({ x, y: percentile(col, 0.25) });
					high.push({ x, y: percentile(col, 0.75) });
					mid.push({ x, y: percentile(col, 0.5) });
				}
				countRegions.push({ first: rg.first, step: rg.step, counts });
			}

			const imgs = buildDensityImages(densityRegions, style.ramp);
			if (imgs.length) densityImages.set(key, imgs);

			const countAt = (x: number): number => {
				let rg = must(countRegions[0], 'at least one region (built above)');
				for (let i = countRegions.length - 1; i > 0; i--) {
					const cand = must(countRegions[i], 'i < length (loop-bounded)');
					if (x >= cand.first - cand.step / 2) { rg = cand; break; }
				}
				// ?? NaN: the clamp keeps the index inside counts — dead.
				return rg.counts[Math.max(0, Math.min(rg.counts.length - 1, Math.round((x - rg.first) / rg.step)))] ?? NaN;
			};

			const bandBase = {
				borderWidth: 0, tension, pointRadius: 0, pointHoverRadius: 0,
				parsing: false as const, normalized: true as const,
				_effortIndex: null, _aggRole: "band" as const,
			};
			datasets.push({ ...bandBase, label: "P25", data: low, borderColor: BAND_STROKE, fill: false });
			// fill:"-1" fills between this (P75) and the previous dataset (P25) → the shaded band.
			datasets.push({ ...bandBase, label: "P75", data: high, borderColor: BAND_STROKE, backgroundColor: style.bandFill, fill: "-1" });
			datasets.push({
				label: "Median", data: mid, borderColor: style.medianStroke, borderWidth: 2, tension,
				pointRadius: 0, pointHoverRadius: 0, fill: false,
				parsing: false as const, normalized: true as const,
				_effortIndex: null, _aggRole: "median" as const,
				_binCountsAt: countAt,
			});
		}

		// Highlighted efforts as full-res individual lines on top; the pinned selection gets a fill + a
		// thicker stroke (mirrors setLineSelection, which we can't use here — dataset index ≠ effortIndex).
		for (const i of visible) {
			if (!highlighted.has(i)) continue;
			const effort = data[i];
			if (!effort) continue;
			const color = must(ctx.effortColors[i % ctx.effortColors.length], 'in-bounds modulo of non-empty palette');
			const selected = i === ctx.selectedEffortIndex;
			const timeStr = formatElapsedHms(effort.elapsedTime);
			datasets.push({
				label: effort.activityName + " (" + timeStr + ")",
				data: seriesOf(i),
				borderColor: color,
				backgroundColor: color + "33",
				borderWidth: selected ? borderWidth + 1.5 : borderWidth,
				borderDash: key === "effort" && effort.costEstimated ? [6, 4] : undefined,
				fill: selected ? "origin" : false,
				tension, pointRadius: 0, pointHoverRadius: 0,
				parsing: false as const, normalized: true as const,
				_effortIndex: i, _aggRole: "effort",
			});
		}

		return datasets;
	}

	return { buildAggregateDatasets, getDensityImages, clearDensityImages };
}

export type AggregateSystem = ReturnType<typeof createAggregateSystem>;
