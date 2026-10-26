/**
 * series-binning.ts — turning a per-sample channel into concrete levels.
 *
 * A Gaussian is a linear low-pass, so it rounds off every edge it meets and the result reads as a
 * continuous ripple. Binning is the opposite: the axis is cut into equal stretches and each stretch
 * gets ONE value, so a climb reads "about 5" and a descent reads "about 45" with a visible step
 * between them. This is exactly what the race strip does (200 bins over course distance, nothing
 * applied after the bin statistic) and it is the binning — not the drawing — that makes it readable:
 * the same polyline renderer draws the activity strip wavy from unbinned data.
 *
 * ## A note on the bin statistic
 *
 * The input here is an already-SMOOTHED series, so a per-bin arithmetic mean is the right reduction:
 * it is averaging a value per sample, not re-deriving a rate. (A Δdistance/Δtime binner was built and
 * measured alongside this — it is immune to uneven sampling, where a sample mean is not — but the
 * smoothed-then-binned view is the one that was kept, and it has no rate to re-derive.)
 *
 * ## Why the output keeps the input's length
 *
 * Every sample carries a value rather than the series collapsing to one point per bin. The chart's
 * zoom re-decimation slices series by INDEX and slices the elevation backdrop by the same window, and
 * the crosshair and readout assume that alignment too — a shorter array on its own x-grid would break
 * all of them.
 *
 * ## Why the bins are joined, not held flat
 *
 * Holding each bin's value across its samples draws a literal staircase: a flat run, then a vertical
 * riser at every bin edge. That is not what the race strip looks like, because the race strip keeps
 * ONE point per bin at the bin's centre and joins them with straight lines — the bins are there in
 * the numbers but invisible as edges. These functions reproduce that shape at full resolution: each
 * sample is linearly interpolated between the two bin centres that bracket it. Sampled densely, a
 * polyline through the interpolated samples is the same curve as a polyline through the bin centres.
 *
 * The bin edges are therefore an artefact of the estimator, not of the picture — which is the point:
 * a grid the data did not choose should not be visible in the drawing of it.
 */

/**
 * Spread per-bin values across the samples by interpolating between bin CENTRES.
 *
 * Bin `b` is centred at `x0 + (b + 0.5) * binWidth`. Before the first centre and after the last there
 * is nothing to interpolate toward, so those samples take the end bin's value flat — the alternative
 * would be extrapolating past the data.
 *
 * ## Empty bins are skipped, not substituted
 *
 * Equal-DISTANCE bins are not equally populated: samples arrive on a time cadence, so a stretch the
 * rider covered fast can hold no sample at all and its bin has no mean to report. Treating that as a
 * value — taking a neighbour's flat — put a kink and then a jump into the trace, which is what showed
 * up as a line shooting too far. An empty bin is a gap in the SAMPLING, not in the ride, so the
 * honest line is the one drawn between the bins that do have values.
 */
function spreadBetweenBinCentres(
	binValues: Float64Array,
	xs: readonly number[],
	lo: number,
	hi: number,
	x0: number,
	binWidth: number,
	out: number[],
): void {
	// The bins that actually reported a value, with the x of their centres.
	const centreX: number[] = [];
	const centreV: number[] = [];
	for (let b = 0; b < binValues.length; b++) {
		const v = binValues[b] ?? Number.NaN;
		if (Number.isFinite(v)) { centreX.push(x0 + (b + 0.5) * binWidth); centreV.push(v); }
	}

	if (centreV.length === 0) {
		for (let i = lo; i <= hi; i++) out[i] = Number.NaN;
		return;
	}
	if (centreV.length === 1) {
		const only = centreV[0] ?? Number.NaN;
		for (let i = lo; i <= hi; i++) out[i] = only;
		return;
	}

	let k = 0; // bracket [k, k+1]; xs ascends, so this only ever advances
	for (let i = lo; i <= hi; i++) {
		const x = xs[i] ?? Number.NaN;
		if (!Number.isFinite(x)) { out[i] = Number.NaN; continue; }

		while (k < centreX.length - 2 && (centreX[k + 1] ?? Number.POSITIVE_INFINITY) < x) k++;

		const xa = centreX[k] ?? Number.NaN, xb = centreX[k + 1] ?? Number.NaN;
		const va = centreV[k] ?? Number.NaN, vb = centreV[k + 1] ?? Number.NaN;
		if (x <= xa) { out[i] = va; continue; }
		if (x >= xb) { out[i] = vb; continue; }
		out[i] = va + (vb - va) * ((x - xa) / (xb - xa));
	}
}

/**
 * Mean of an already-derived series per equal-width distance bin, interpolated between bin centres.
 *
 * Used for the "smoothed + binned" view: the input is the smoothed line, so the bin is quantising an
 * already-conditioned signal rather than raw samples. A sample mean is the right reduction HERE
 * (unlike above) because the input is a value per sample, not a rate to be re-derived.
 */
export function binnedMean(
	values: readonly number[],
	xs: readonly number[],
	from: number,
	to: number,
	bins: number,
): number[] {
	const n = Math.min(values.length, xs.length);
	const out = new Array<number>(n).fill(Number.NaN);
	const lo = Math.max(0, from);
	const hi = Math.min(n - 1, to);
	if (n === 0 || hi <= lo || bins < 1) return out;

	const x0 = xs[lo] ?? Number.NaN;
	const x1 = xs[hi] ?? Number.NaN;
	if (!Number.isFinite(x0) || !Number.isFinite(x1) || x1 <= x0) return out;

	const binCount = Math.max(1, Math.min(Math.floor(bins), hi - lo));
	const binWidth = (x1 - x0) / binCount;
	if (!(binWidth > 0)) return out;

	const binValues = new Float64Array(binCount).fill(Number.NaN);
	let cursor = lo;
	for (let b = 0; b < binCount; b++) {
		const stopX = b === binCount - 1 ? x1 : x0 + (b + 1) * binWidth;
		let sum = 0, count = 0;
		while (cursor <= hi && (xs[cursor] ?? Number.POSITIVE_INFINITY) <= stopX) {
			const v = values[cursor] ?? Number.NaN;
			if (Number.isFinite(v)) { sum += v; count++; }
			cursor++;
		}
		if (count > 0) binValues[b] = sum / count;
	}

	spreadBetweenBinCentres(binValues, xs, lo, hi, x0, binWidth, out);
	return out;
}
