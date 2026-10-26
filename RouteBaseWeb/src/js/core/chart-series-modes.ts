/**
 * chart-series-modes.ts — the ways a GPS channel can be drawn, precomputed.
 *
 *   recorded   what the server stored, untouched by the client
 *   smoothBin  a zero-phase Gaussian over a window of seconds, then quantised into equal distance
 *              bins and drawn through the bin centres
 *
 * The Gaussian alone reads as a continuous ripple, because a linear low-pass rounds off every edge it
 * meets. Binning its output gives the trace a readable shape — a climb reads "about 5", a descent
 * "about 45" — while joining bin centres rather than holding each bin flat keeps the bin grid out of
 * the picture. (Judged against Δd/Δt bins, total-variation levels and a rolling median; this is the
 * one that was kept.)
 *
 * "Recorded" is deliberately not called "raw": speed is already a 5 s distance/time average applied
 * at ingest (Services/GeoUtils.SmoothSpeeds), so there is no rawer series to offer and a button
 * promising one would be lying.
 *
 * Every view is built once, up front, so switching is a dataset swap rather than a rebuild — which is
 * what lets the chart keep its zoom across a mode change.
 *
 * ## Every mode shares ONE x-grid, and that is load-bearing
 *
 * The chart's zoom re-decimation slices series by INDEX and slices the elevation backdrop by the same
 * window; the crosshair and the readout assume the same alignment. So the binned view is computed at
 * SOURCE resolution rather than collapsing to one point per bin — see core/series-binning.
 *
 * Pure: plain arrays in, plain arrays out. No Chart.js, no DOM, no settings.
 */

import { gaussianSmooth, gaussianSmoothTime } from './smoothing';
import { binnedMean } from './series-binning';
import { lttbDecimate } from './chart-decimation';

export type SeriesMode = 'recorded' | 'smoothBin';

/** The modes in click order — least processed to most. Exported so a pref read can validate. */
export const SERIES_MODES: readonly SeriesMode[] = ['recorded', 'smoothBin'];

/** Modes whose values depend on the visible range and must be recomputed when it changes. */
const RANGE_DEPENDENT: ReadonlySet<SeriesMode> = new Set<SeriesMode>(['smoothBin']);

export interface XYPoint { x: number; y: number }

/** One drawable series at both resolutions: full-res for lookups, decimated for painting. */
export interface SeriesView {
	full: XYPoint[];
	dec: XYPoint[];
}

export interface SeriesViews {
	views: Record<SeriesMode, SeriesView>;
	/**
	 * Re-derive one mode's full-resolution points over a visible index range, or null when the mode
	 * is range-independent and the caller should just slice `full`.
	 *
	 * This is what keeps ~BIN_COUNT bins on screen at every zoom: whole ride → coarse levels, zoomed
	 * into a climb → fine ones, the same way the chart already re-decimates per viewport.
	 */
	rebuildRange(mode: SeriesMode, from: number, to: number): XYPoint[] | null;
}

export interface SeriesViewInput {
	/** The recorded channel, lockstep with `xs`. Non-finite entries are gaps, not zeroes. */
	readonly values: readonly number[];
	/** The x axis the chart plots against — cumulative km, monotonic. */
	readonly xs: readonly number[];
	/** Elapsed seconds per point, or null when the track has no usable time axis. */
	readonly seconds: readonly number[] | null;
	/** Smoothing width in seconds; used when `seconds` is present. 0 disables smoothing. */
	readonly windowSeconds: number;
	/** Sample-indexed kernel width, used only when there is no time axis to measure against. */
	readonly sampleWindow: number;
	/** Bins across the visible range — the chart's detail setting. */
	readonly bins: number;
	/** Decimation target (points actually drawn). */
	readonly target: number;
}

function toXY(xs: readonly number[], ys: readonly number[]): XYPoint[] {
	const n = Math.min(xs.length, ys.length);
	const out: XYPoint[] = new Array(n);
	for (let i = 0; i < n; i++) out[i] = { x: xs[i] ?? Number.NaN, y: ys[i] ?? Number.NaN };
	return out;
}

function view(xs: readonly number[], ys: readonly number[], target: number): SeriesView {
	const full = toXY(xs, ys);
	return { full, dec: full.length > target ? lttbDecimate(full, target) : full };
}

/** Build every view of one channel, plus the seam that re-derives the range-dependent ones. */
export function buildSeriesViews(input: SeriesViewInput): SeriesViews {
	const { values, xs, seconds, windowSeconds, sampleWindow, target, bins } = input;
	const n = Math.min(values.length, xs.length);
	const last = Math.max(0, n - 1);

	const smoothedY = seconds
		? gaussianSmoothTime(values, seconds, windowSeconds)
		// No time axis: fall back to the sample-indexed kernel. It cannot describe a physical window,
		// but it still beats drawing an unsmoothed line and calling it smoothed.
		: gaussianSmooth(values.slice(), sampleWindow);

	const smoothBinY = (from: number, to: number): number[] => binnedMean(smoothedY, xs, from, to, bins);

	const views: Record<SeriesMode, SeriesView> = {
		recorded: view(xs, values, target),
		smoothBin: view(xs, smoothBinY(0, last), target),
	};

	function rebuildRange(mode: SeriesMode, from: number, to: number): XYPoint[] | null {
		if (!RANGE_DEPENDENT.has(mode)) return null;
		const ys = smoothBinY(from, to);
		const out: XYPoint[] = [];
		for (let i = Math.max(0, from); i <= Math.min(n - 1, to); i++) {
			out.push({ x: xs[i] ?? Number.NaN, y: ys[i] ?? Number.NaN });
		}
		return out;
	}

	return { views, rebuildRange };
}

/** The series a mode draws. */
export function lineForMode(views: SeriesViews, mode: SeriesMode): SeriesView {
	return views.views[mode];
}

/**
 * Decimal places the readout should show for a mode.
 *
 * Only the recorded channel is a measurement to a tenth; everything else is an average over seconds
 * or a fitted level, whose tenths are arithmetic rather than data.
 */
export function readoutDecimalsForMode(mode: SeriesMode): number {
	return mode === 'recorded' ? 1 : 0;
}
