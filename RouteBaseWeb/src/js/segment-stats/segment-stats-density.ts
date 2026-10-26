// =============================================================================
// Segment chart density backdrop — DenseLines-style effort-density field
// -----------------------------------------------------------------------------
// Aggregate mode's band+median hides how the efforts are *distributed*. This
// module rasterizes the binned effort rows (from segment-stats-aggregate) into a
// W×H count grid — each effort deposits ~1 unit of mass per column regardless of
// how steep its line is (segment mass is split 1/spanCells over the cells it
// crosses) — tone-maps the counts with asinh so sparse regions stay visible, and
// renders one offscreen RGBA bitmap per chart. The chart plugin only drawImage()s
// that bitmap, so pan/zoom/hover cost is a single blit — never a re-bin.
//
// accumulateDensity/toneMapDensity/robustValueRange are pure (no DOM) on purpose.
// =============================================================================
import { must } from '../core/invariant';

/** Offscreen density bitmap + the data-space rect it covers (for scale mapping). */
export interface DensityImage {
	canvas: HTMLCanvasElement;
	xMin: number;
	xMax: number;
	yMin: number;
	yMax: number;
}

/** One-hue colormap ramp: sparse → `low` (recedes into the dark surface), dense → `high`. */
export interface DensityRamp {
	low: string;   // hex, darkest/deepest step
	high: string;  // hex, brightest step
	maxAlpha: number; // alpha at full density (0 density is always fully transparent)
}

/** Vertical resolution of the density grid (horizontal = the caller's bin count). */
export const DENSITY_ROWS = 240;

/**
 * Robust value range over the binned rows (NaN = no coverage): P1/P99 of a strided
 * sample, padded 5% — mirrors robustYBounds so one GPS spike can't stretch the
 * grid and crush all real density into a few cells. Null when nothing is finite.
 */
export function robustValueRange(rows: Float64Array[]): { min: number; max: number } | null {
	const MAX_SAMPLES = 50_000;
	let total = 0;
	for (const r of rows) total += r.length;
	if (total === 0) return null;
	const stride = Math.max(1, Math.ceil(total / MAX_SAMPLES));
	const samples: number[] = [];
	for (const r of rows) {
		for (let i = 0; i < r.length; i += stride) {
			const v = r[i] ?? NaN; // dead (i < length); isFinite(NaN) skips it exactly as undefined did
			if (Number.isFinite(v)) samples.push(v);
		}
	}
	if (samples.length === 0) return null;
	samples.sort((a, b) => a - b);
	const at = (p: number): number => samples[Math.min(samples.length - 1, Math.max(0, Math.round(p * (samples.length - 1))))] ?? NaN; // dead: clamped in-bounds
	const p1 = at(0.01), p99 = at(0.99);
	const pad = (p99 - p1) * 0.05 || Math.abs(p99) * 0.05 || 1;
	return { min: p1 - pad, max: p99 + pad };
}

/**
 * Rasterize the effort rows into a column-major-x count grid (index = cellY * w + x,
 * cellY 0 = yMin). Per column each effort contributes total mass ~1, spread over the
 * vertical span between this bin's value and the next (so steep sections don't read
 * denser than flat ones). Cells outside [yMin,yMax] clip — their mass is dropped,
 * matching how outliers clip off the plot.
 */
export function accumulateDensity(
	rows: Float64Array[], w: number, h: number, yMin: number, yMax: number,
): Float32Array {
	const counts = new Float32Array(w * h);
	const span = yMax - yMin;
	if (!(span > 0)) return counts;
	const scale = h / span;
	for (const row of rows) {
		for (let b = 0; b < w; b++) {
			// ?? NaN: b(+1) < w = row.length — dead, and NaN already takes the existing skip paths.
			const v0 = row[b] ?? NaN;
			if (Number.isNaN(v0)) continue;
			const next = b + 1 < w ? row[b + 1] ?? NaN : NaN;
			const v1 = Number.isNaN(next) ? v0 : next;
			let c0 = Math.floor((v0 - yMin) * scale);
			let c1 = Math.floor((v1 - yMin) * scale);
			if (c1 < c0) { const t = c0; c0 = c1; c1 = t; }
			const weight = 1 / (c1 - c0 + 1); // unit mass per column, before clipping
			const lo = Math.max(0, c0), hi = Math.min(h - 1, c1);
			for (let c = lo; c <= hi; c++) counts[c * w + b] = (counts[c * w + b] ?? NaN) + weight; // dead: clipped in-bounds
		}
	}
	return counts;
}

/**
 * Normalize counts to 0..1 in place via asinh(c)/asinh(max) — compressive like log
 * but defined at 0, so a heavily-travelled median corridor doesn't black out every
 * sparser path. `maxCount` overrides the array's own max (multi-region grids must
 * share one denominator or a sparse region renders artificially hot). Returns the
 * same array.
 */
export function toneMapDensity(counts: Float32Array, maxCount?: number): Float32Array {
	let max = maxCount ?? 0;
	if (maxCount == null) {
		for (let i = 0; i < counts.length; i++) { const c = counts[i] ?? NaN; if (c > max) max = c; }
	}
	if (max <= 0) return counts;
	const denom = Math.asinh(max);
	for (let i = 0; i < counts.length; i++) counts[i] = Math.asinh(counts[i] ?? NaN) / denom;
	return counts;
}

function hexToRgb(hex: string): [number, number, number] {
	const n = parseInt(hex.slice(1), 16);
	return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

/**
 * Paint the tone-mapped density onto an offscreen canvas. Color lerps low→high with
 * density; alpha ramps 0→maxAlpha (slightly convex, ^0.8, so faint regions stay
 * translucent as requested). Grid cellY 0 = yMin = bitmap bottom row.
 */
export function renderDensityBitmap(
	density: Float32Array, w: number, h: number, ramp: DensityRamp,
): HTMLCanvasElement {
	const [lr, lg, lb] = hexToRgb(ramp.low);
	const [hr, hg, hb] = hexToRgb(ramp.high);
	// 256-entry RGBA LUT — avoids per-pixel lerp math on the w*h fill below.
	const lut = new Uint8ClampedArray(256 * 4);
	for (let i = 0; i < 256; i++) {
		const t = i / 255;
		lut[i * 4] = lr + (hr - lr) * t;
		lut[i * 4 + 1] = lg + (hg - lg) * t;
		lut[i * 4 + 2] = lb + (hb - lb) * t;
		lut[i * 4 + 3] = 255 * ramp.maxAlpha * Math.pow(t, 0.8);
	}
	const canvas = document.createElement("canvas");
	canvas.width = w;
	canvas.height = h;
	const c2d = must(canvas.getContext("2d"), 'fresh offscreen canvas has a 2d context');
	const img = c2d.createImageData(w, h);
	const px = img.data;
	for (let y = 0; y < h; y++) {
		const srcRow = (h - 1 - y) * w; // flip: bitmap row 0 is the top (yMax)
		const dstRow = y * w * 4;
		for (let x = 0; x < w; x++) {
			// ?? 0 / ?? NaN: indices are in-bounds of the dense LUT/grid — dead, and a
			// Uint8ClampedArray write already coerced undefined to 0, so this is bit-identical.
			const li = Math.min(255, Math.round((density[srcRow + x] ?? NaN) * 255)) * 4;
			const o = dstRow + x * 4;
			px[o] = lut[li] ?? 0;
			px[o + 1] = lut[li + 1] ?? 0;
			px[o + 2] = lut[li + 2] ?? 0;
			px[o + 3] = lut[li + 3] ?? 0;
		}
	}
	c2d.putImageData(img, 0, 0);
	return canvas;
}

/** One contiguous x-region of binned rows (bin width may differ between regions). */
export interface DensityRegionInput {
	rows: Float64Array[]; // one row per effort reaching this region; length = the region's bin count
	xMin: number;
	xMax: number;
}

/**
 * Full pipeline over one or more x-regions: rows → count grids → shared tone map →
 * one bitmap per region. The y-range AND the tone-map denominator are computed over
 * ALL regions together, so a sparse outlier tail keeps the same color meaning as the
 * dense main window. Empty when no region has a usable range.
 */
export function buildDensityImages(regions: DensityRegionInput[], ramp: DensityRamp): DensityImage[] {
	const usable = regions.filter((r) => r.rows.length > 0 && r.xMax > r.xMin);
	if (usable.length === 0) return [];
	const allRows: Float64Array[] = [];
	for (const r of usable) allRows.push(...r.rows);
	const range = robustValueRange(allRows);
	if (!range || !(range.max > range.min)) return [];

	const grids = usable.map((r) => {
		const w = must(r.rows[0], 'rows.length > 0 (filtered above)').length;
		return {
			region: r,
			w,
			counts: accumulateDensity(r.rows, w, DENSITY_ROWS, range.min, range.max),
		};
	});
	let max = 0;
	for (const g of grids) for (let i = 0; i < g.counts.length; i++) { const c = g.counts[i] ?? NaN; if (c > max) max = c; }

	return grids.map((g) => ({
		canvas: renderDensityBitmap(toneMapDensity(g.counts, max), g.w, DENSITY_ROWS, ramp),
		xMin: g.region.xMin,
		xMax: g.region.xMax,
		yMin: range.min,
		yMax: range.max,
	}));
}
