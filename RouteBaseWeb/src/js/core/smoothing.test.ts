import { describe, it, expect } from 'vitest';
import { must } from './invariant';
import { gaussianSmooth, gaussianSmoothTime } from './smoothing';

// A constant-array test can't discriminate a broken kernel — every weight multiplies against the same
// value regardless of its shape or sign, so a flipped exponent or an off-by-one window would still pass.
// These pin actual blurred shapes (values computed from the algorithm itself, not hand-derived — a golden
// fixture, same spirit as this repo's other exact-value pins).
describe('gaussianSmooth', () => {
	it('blurs an isolated spike into a symmetric bell, strictly decaying with distance', () => {
		const out = gaussianSmooth([0, 0, 0, 0, 10, 0, 0, 0, 0], 5);

		expect(out[4]).toBeCloseTo(3.3240562523794583, 10); // the spike's own position
		expect(out[3]).toBeCloseTo(2.413760246844125, 10);  // 1 away
		expect(out[2]).toBeCloseTo(0.924211626966146, 10);  // 2 away
		expect(out[3]).toBe(out[5]);                        // symmetric around the spike
		expect(out[2]).toBe(out[6]);
		// Strictly decaying outward — an inverted exponent (weights GROWING with distance) would fail this.
		expect(out[4]).toBeGreaterThan(must(out[3], 'fixture index 3'));
		expect(out[3]).toBeGreaterThan(must(out[2], 'fixture index 2'));
		expect(out[2]).toBeGreaterThan(must(out[1], 'fixture index 1')); // out[1] = 0 (out of window)
	});

	it('blurs a step into a monotonic ramp through the transition, flat on both plateaus', () => {
		const out = gaussianSmooth([0, 0, 0, 0, 0, 10, 10, 10, 10, 10], 5);

		expect(out[0]).toBeCloseTo(0, 10);
		expect(out[9]).toBeCloseTo(10, 10);
		// Monotonically non-decreasing across the whole transition — a broken kernel (e.g. wrong window
		// bounds) would produce a dip, overshoot, or an early/late transition point.
		for (let i = 1; i < out.length; i++) expect(out[i]).toBeGreaterThanOrEqual(must(out[i - 1], 'i-1 >= 0 (loop-bounded)'));
		expect(out[3]).toBeCloseTo(0.924211626966146, 10); // last pre-transition point already feels the step
		expect(out[7]).toBeCloseTo(10, 10);                // fully flat again 2 points past the edge
	});

	it('a changed sigma (windowSize) changes the blur width — the formula is not a fixed constant', () => {
		const narrow = gaussianSmooth([0, 0, 0, 0, 10, 0, 0, 0, 0], 3);
		const wide = gaussianSmooth([0, 0, 0, 0, 10, 0, 0, 0, 0], 7);

		// A wider window spreads the same spike's energy further — its own-position value drops...
		expect(wide[4]).toBeLessThan(must(narrow[4], 'spike position'));
		// ...and reaches further out (2-away is 0 under a window of 3, nonzero under a window of 7).
		expect(narrow[2]).toBe(0);
		expect(wide[2]).toBeGreaterThan(0);
	});
});

/** Seconds 0..n-1 — a 1 Hz recording, the case a sample window happens to get right. */
function oneHz(n: number): number[] {
	return Array.from({ length: n }, (_, i) => i);
}

describe('gaussianSmoothTime', () => {
	it('is zero-phase — a symmetric input smooths to a symmetric output', () => {
		const v = [0, 0, 0, 0, 10, 0, 0, 0, 0];
		const out = gaussianSmoothTime(v, oneHz(9), 8);
		expect(out[3]).toBeCloseTo(must(out[5], 'mirror of 3'), 12);
		expect(out[2]).toBeCloseTo(must(out[6], 'mirror of 2'), 12);
		// The peak stays put; a trailing filter would have moved it right.
		for (const i of [0, 1, 2, 3, 5, 6, 7, 8]) expect(out[4]).toBeGreaterThan(must(out[i], `index ${i}`));
	});

	it('smooths the same ride the same way at 1 Hz and at a 4 s sample rate', () => {
		// THE defect the sample-indexed kernel has. Both arrays carry one underlying ride, sampled
		// differently. A window of 16 counted in POINTS spans 16 s on the dense grid and 64 s on the
		// sparse one; counted in seconds it is 16 s on both.
		const dense = oneHz(41);                            // 0,1,2,…40
		const sparse = dense.filter((t) => t % 4 === 0);    // 0,4,8,…40
		const signal = (t: number): number => 20 + 10 * Math.sin(t / 8);

		const timeDense = gaussianSmoothTime(dense.map(signal), dense, 16);
		const timeSparse = gaussianSmoothTime(sparse.map(signal), sparse, 16);
		const sampleSparse = gaussianSmooth(sparse.map(signal), 16);

		let worstTime = 0, worstSample = 0;
		for (let k = 2; k < sparse.length - 2; k++) {
			const t = must(sparse[k], 'sparse index');
			const ref = must(timeDense[t], 'dense out');
			worstTime = Math.max(worstTime, Math.abs(must(timeSparse[k], 'time out') - ref));
			worstSample = Math.max(worstSample, Math.abs(must(sampleSparse[k], 'sample out') - ref));
		}

		// Not bit-identical — a Gaussian sampled on a 4x coarser grid cannot be — but within a quarter
		// of a km/h across a 10 km/h swing.
		expect(worstTime).toBeLessThan(0.25);
		// The sample-indexed kernel, given the same nominal window, is out by whole km/h: its window
		// silently became 64 s and flattened most of a 50 s oscillation away.
		expect(worstSample).toBeGreaterThan(3);
	});

	it('leaves a hole as a hole instead of dragging the mean to zero', () => {
		// `gaussianSmooth`'s `data[idx] || 0` reads a dropout as a standstill and smears a dip across
		// the kernel. Here the dropout stays a gap and its neighbours are unaffected by it.
		const v = [20, 20, Number.NaN, 20, 20];
		const out = gaussianSmoothTime(v, oneHz(5), 4);
		expect(out[2]).toBeNaN();
		for (const i of [0, 1, 3, 4]) expect(out[i]).toBeCloseTo(20, 10);
	});

	it('keeps a legitimate zero — a standstill is data, a dropout is not', () => {
		const out = gaussianSmoothTime([0, 0, 0, 0, 0], oneHz(5), 4);
		out.forEach((x) => expect(x).toBe(0));
	});

	it('preserves a constant and the array length', () => {
		const out = gaussianSmoothTime([5, 5, 5, 5, 5], oneHz(5), 10);
		expect(out).toHaveLength(5);
		out.forEach((x) => expect(x).toBeCloseTo(5, 10));
	});

	it('a non-positive window is "no smoothing", not a crash', () => {
		expect(gaussianSmoothTime([1, 9, 1], oneHz(3), 0)).toEqual([1, 9, 1]);
		expect(gaussianSmoothTime([1, 9, 1], oneHz(3), -5)).toEqual([1, 9, 1]);
		expect(gaussianSmoothTime([], [], 10)).toEqual([]);
	});

	it('a wider window in seconds blurs harder', () => {
		const v = [0, 0, 0, 0, 10, 0, 0, 0, 0];
		const narrow = gaussianSmoothTime(v, oneHz(9), 4);
		const wide = gaussianSmoothTime(v, oneHz(9), 12);
		expect(wide[4]).toBeLessThan(must(narrow[4], 'spike position'));
	});
});

