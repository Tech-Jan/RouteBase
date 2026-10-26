// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { computeEffortSpeedProfile, type EffortSpeedSource } from './segment-effort-speed';
import type { EffortDataPoint } from './segment-stats-effort-types';

/**
 * The aggregate the replay strip traces. What is worth pinning is the sampling rule, because getting it
 * wrong is silent: effort points arrive at a fixed CADENCE, so a slow effort files more of them through a
 * stretch, and pooling every point would drag the median toward whoever was slowest there — an aggregate
 * that reads slower than any effort actually rode, with nothing to show for it.
 */

function pt(speed: number): EffortDataPoint {
	return { distance: 0, time: 0, speed, altitude: null, lat: null, lng: null };
}

/** One effort as `speed @ progress%` pairs. */
function effort(samples: Array<[number, number]>): EffortSpeedSource {
	return { points: samples.map(([s]) => pt(s)), progress: samples.map(([, p]) => p) };
}

describe('computeEffortSpeedProfile', () => {
	it('bins by completion, with the bin centre as the x the strip plots against', () => {
		const profile = computeEffortSpeedProfile([effort([[20, 5], [30, 55]])], { bins: 2 });

		expect(profile.bins.map((b) => b.progressPercent)).toEqual([25, 75]);
		expect(profile.bins.map((b) => b.median)).toEqual([20, 30]);
	});

	it('gives each effort ONE value per bin — a dawdler cannot outvote a fast effort by filing more points', () => {
		// Both efforts cross the same stretch. The slow one leaves four points there, the fast one two;
		// pooled, the median would be 10. Per-effort means make it the midpoint of the two efforts.
		const slow = effort([[10, 10], [10, 12], [10, 14], [10, 16]]);
		const fast = effort([[30, 11], [30, 15]]);

		const profile = computeEffortSpeedProfile([slow, fast], { bins: 1 });
		const bin = profile.bins[0];

		expect(bin?.effortCount).toBe(2);
		expect(bin?.median).toBe(20);
	});

	it('quantiles run across EFFORTS — p10/p90 are the slow and fast ends of the set', () => {
		const efforts = [10, 20, 30, 40, 50].map((s) => effort([[s, 50]]));

		const bin = computeEffortSpeedProfile(efforts, { bins: 1 }).bins[0];

		expect(bin?.median).toBe(30);
		expect(bin?.p10).toBeCloseTo(14, 6);   // linear-interpolated over 5 sorted values
		expect(bin?.p90).toBeCloseTo(46, 6);
		expect(bin?.effortCount).toBe(5);
	});

	it('files no bin for a stretch nobody rode — the trace skips it rather than plotting a zero', () => {
		const profile = computeEffortSpeedProfile([effort([[20, 5], [25, 95]])], { bins: 10 });

		expect(profile.bins.map((b) => b.progressPercent)).toEqual([5, 95]);
	});

	it('ignores non-positive and non-finite speeds instead of averaging them in', () => {
		const profile = computeEffortSpeedProfile(
			[effort([[0, 50], [Number.NaN, 50], [30, 50]])], { bins: 1 });

		expect(profile.bins[0]?.median).toBe(30);
	});

	it('reports the tallest p90 as the y-axis top a renderer scales to', () => {
		const efforts = [10, 50].map((s) => effort([[s, 20], [s, 80]]));

		expect(computeEffortSpeedProfile(efforts, { bins: 2 }).maxSpeedKmh).toBeCloseTo(46, 6);
	});

	it('an effort with no usable points contributes nothing rather than a zero bin', () => {
		expect(computeEffortSpeedProfile([], { bins: 4 }).bins).toEqual([]);
		expect(computeEffortSpeedProfile([effort([])], { bins: 4 }).bins).toEqual([]);
	});

	it('a single effort is still a profile — its own speed, with the spread collapsed onto it', () => {
		const bin = computeEffortSpeedProfile([effort([[24, 50]])], { bins: 1 }).bins[0];

		expect(bin?.effortCount).toBe(1);
		expect([bin?.p10, bin?.median, bin?.p90]).toEqual([24, 24, 24]);
	});
});
