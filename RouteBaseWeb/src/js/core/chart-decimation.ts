/**
 * chart-decimation.ts — LTTB downsampling + sorted-array nearest-index lookup.
 *
 * Pure helpers lifted out of the chart factory: no Chart.js, no DOM. chart-factory
 * uses them to reduce an {x,y}[] series to a drawable point count and to map a
 * cursor x-value back to a data index.
 */

import { must } from './invariant';


/**
 * LTTB (Largest Triangle Three Buckets) decimation.
 * Reduces an {x,y}[] array to `target` points while preserving visual shape.
 * Always keeps the first and last point.
 */
export function lttbDecimate(
	data: { x: number; y: number }[],
	target: number,
): { x: number; y: number }[] {
	const len = data.length;
	if (target >= len || target < 3) return data;

	// len > target >= 3 past the guard, so first/last exist and every bucket index below
	// stays inside [1, len-1] by construction.
	const first = must(data[0], 'len >= 3 (guarded above)');
	const last = must(data[len - 1], 'len >= 3 (guarded above)');

	const out: { x: number; y: number }[] = new Array(target);
	out[0] = first; // always keep first

	const bucketSize = (len - 2) / (target - 2);
	let a = 0; // index of previously selected point

	for (let i = 1; i < target - 1; i++) {
		// Determine bucket range
		const bucketStart = Math.floor((i - 1) * bucketSize) + 1;
		const bucketEnd = Math.min(Math.floor(i * bucketSize) + 1, len - 1);

		// Average of next bucket (for triangle area calculation)
		const nextStart = Math.floor(i * bucketSize) + 1;
		const nextEnd = Math.min(Math.floor((i + 1) * bucketSize) + 1, len - 1);
		let avgX: number, avgY: number;
		const nextCount = nextEnd - nextStart;
		if (nextCount > 0) {
			avgX = 0; avgY = 0;
			for (let j = nextStart; j < nextEnd; j++) {
				const p = must(data[j], 'j < nextEnd <= len-1 (loop-bounded)');
				avgX += p.x;
				avgY += p.y;
			}
			avgX /= nextCount;
			avgY /= nextCount;
		} else {
			// Last interior bucket: its "next bucket" is just the final point (standard LTTB uses it as
			// the stand-in). 0/0 = NaN here made every area comparison false, so the bucket degraded to
			// its FIRST point and a late spike (summit, max speed) vanished from the decimated series.
			avgX = last.x;
			avgY = last.y;
		}

		// Find point in current bucket with largest triangle area
		const pa = must(data[a], 'a tracks an already-selected in-bounds index');
		const ax = pa.x, ay = pa.y;
		let maxArea = -1;
		let bestIdx = bucketStart;
		for (let j = bucketStart; j < bucketEnd; j++) {
			const pj = must(data[j], 'j < bucketEnd <= len-1 (loop-bounded)');
			const area = Math.abs(
				(ax - avgX) * (pj.y - ay) -
				(ax - pj.x) * (avgY - ay),
			);
			if (area > maxArea) {
				maxArea = area;
				bestIdx = j;
			}
		}

		out[i] = must(data[bestIdx], 'bestIdx ∈ [bucketStart, bucketEnd) (in-bounds)');
		a = bestIdx;
	}

	out[target - 1] = last; // always keep last
	return out;
}

/**
 * Binary search: nearest index in a sorted-ascending array for a given x-value.
 * O(log n) vs O(n) for Chart.js getElementsAtEventForMode.
 */
export function nearestIndexForX(sortedX: number[], xVal: number): number {
	// ?? NaN: lo/hi/mid stay inside the array by the search invariant — unreachable, and a NaN
	// comparison is false exactly as the undefined one was.
	let lo = 0;
	let hi = sortedX.length - 1;
	while (lo < hi) {
		const mid = (lo + hi) >>> 1;
		if ((sortedX[mid] ?? NaN) < xVal) lo = mid + 1;
		else hi = mid;
	}
	// lo is the insertion point; check neighbours for the closest match
	if (lo > 0 && Math.abs((sortedX[lo - 1] ?? NaN) - xVal) < Math.abs((sortedX[lo] ?? NaN) - xVal)) {
		return lo - 1;
	}
	return lo;
}
