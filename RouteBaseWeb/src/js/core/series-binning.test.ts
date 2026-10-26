// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { binnedMean } from './series-binning';
import { must } from './invariant';

describe('binnedMean', () => {
	it('interpolates across a bin no sample landed in — the empty-bin glitch', () => {
		// Equal-DISTANCE bins over a stretch covered fast hold no sample at all. Substituting a
		// neighbour's value flat put a kink and then a jump in the trace; the line should run straight
		// between the bins that do have values.
		// Samples cluster at the start, then one long jump in distance leaves middle bins empty.
		const xs = [0, 1, 2, 3, 4, 40, 41, 42, 43, 44];
		const values = [10, 10, 10, 10, 10, 50, 50, 50, 50, 50];
		const out = binnedMean(values, xs, 0, xs.length - 1, 9);

		for (const v of out) expect(Number.isFinite(v)).toBe(true);
		// Monotonic from the low level to the high one, with no excursion outside them.
		for (let i = 1; i < out.length; i++) {
			expect(must(out[i], 'value')).toBeGreaterThanOrEqual(must(out[i - 1], 'prev') - 1e-9);
		}
		expect(Math.min(...out)).toBeGreaterThanOrEqual(10 - 1e-9);
		expect(Math.max(...out)).toBeLessThanOrEqual(50 + 1e-9);
	});

	it('reproduces a straight ramp — bin means of a ramp lie ON the ramp, and lerping keeps them there', () => {
		// The cleanest exactness check available: if the per-bin means are right AND the interpolation
		// between centres is right, a linear input has to come back linear.
		const xs = Array.from({ length: 100 }, (_, i) => i);
		const out = binnedMean(xs.slice(), xs, 0, 99, 10);
		for (let i = 10; i < 90; i++) {
			expect(Math.abs(must(out[i], 'ramp value') - i)).toBeLessThan(1);
		}
		expect(out).toHaveLength(100);
	});

	it('spreads a step across a bin width rather than stepping at the edge', () => {
		const xs = Array.from({ length: 100 }, (_, i) => i);
		const out = binnedMean(xs.map((x) => (x < 50 ? 10 : 40)), xs, 0, 99, 10);
		let maxJump = 0;
		for (let i = 1; i < out.length; i++) {
			const a = out[i - 1] ?? Number.NaN, b = out[i] ?? Number.NaN;
			if (Number.isFinite(a) && Number.isFinite(b)) maxJump = Math.max(maxJump, Math.abs(b - a));
		}
		expect(maxJump).toBeLessThan(10); // a held level would jump the full 30 at one sample
	});

	it('skips holes when averaging rather than reading them as zero', () => {
		const xs = [0, 1, 2, 3];
		const out = binnedMean([10, Number.NaN, 30, Number.NaN], xs, 0, 3, 1);
		expect(must(out[0], 'bin mean')).toBeCloseTo(20, 10); // (10 + 30) / 2, not / 4
	});

	it('preserves length and gaps outside the range', () => {
		const xs = Array.from({ length: 20 }, (_, i) => i);
		const out = binnedMean(xs.map(() => 5), xs, 5, 10, 3);
		expect(out).toHaveLength(20);
		expect(out[0]).toBeNaN();
		expect(out[19]).toBeNaN();
	});
});
