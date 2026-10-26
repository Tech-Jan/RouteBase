// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { centredMeanKmh, windowSecondsFor, type DistanceSeries } from './speed-window';

/** A series from parallel (seconds, km) arrays — the shape both replay adapters build. */
function series(seconds: readonly number[], km: readonly number[]): DistanceSeries {
	return {
		length: seconds.length,
		secondsAt: (i) => seconds[i] ?? Number.NaN,
		kmAt: (i) => km[i] ?? Number.NaN,
	};
}

/** 21 samples at 1 Hz holding a constant km/h. */
function constantSpeed(kmh: number): DistanceSeries {
	const t: number[] = [], d: number[] = [];
	for (let i = 0; i <= 20; i++) { t.push(i); d.push((i * kmh) / 3600); }
	return series(t, d);
}

describe('windowSecondsFor', () => {
	it('never averages less than the server velocity_smooth window', () => {
		// Below 2.5x the wall-time rule would ask for less than 5 s; the floor keeps the label and the
		// effort chart's speed channel describing the same quantity at 1x.
		expect(windowSecondsFor(1)).toBe(5);
		expect(windowSecondsFor(0.25)).toBe(5);
	});

	it('widens with the playback rate — the reading is worth ~2 wall-seconds either way', () => {
		expect(windowSecondsFor(5)).toBe(10);
		expect(windowSecondsFor(10)).toBe(20);
	});

	it('caps at a minute, past which it is a lap average rather than a speed', () => {
		expect(windowSecondsFor(30)).toBe(60);
		expect(windowSecondsFor(750)).toBe(60);
	});

	it('holds the reading at ~2 wall-seconds of screen time between the floor and the cap', () => {
		// This is the whole point: ride-seconds are not the readability unit, wall-seconds are.
		for (const rate of [2.5, 4, 7.5, 13, 30]) {
			expect(windowSecondsFor(rate) / rate).toBeCloseTo(2, 10);
		}
	});

	it('falls back to the floor on a rate that is missing or nonsense', () => {
		expect(windowSecondsFor(0)).toBe(5);
		expect(windowSecondsFor(-3)).toBe(5);
		expect(windowSecondsFor(Number.NaN)).toBe(5);
	});
});

describe('centredMeanKmh', () => {
	it('reads a constant speed exactly, at every playhead and every width', () => {
		const s = constantSpeed(36);
		for (const at of [0, 3.5, 10, 17, 20]) {
			for (const w of [5, 20, 60]) {
				expect(centredMeanKmh(s, at, w)).toBeCloseTo(36, 10);
			}
		}
	});

	it('is a time-weighted integral, not an average of samples', () => {
		// 10 s at 36 km/h, then 10 s at 18.
		const t: number[] = [], d: number[] = [];
		for (let i = 0; i <= 20; i++) {
			t.push(i);
			d.push(i <= 10 ? (i * 36) / 3600 : (10 * 36) / 3600 + ((i - 10) * 18) / 3600);
		}
		// Centred on the join: 5 s of each leg → 27.
		expect(centredMeanKmh(series(t, d), 10, 10)).toBeCloseTo(27, 10);
		// Centred at 12 the window is [7, 17] — 3 s at 36 and 7 s at 18, deliberately unequal so the
		// answer differs from an unweighted mean of the two leg speeds: (3*36 + 7*18) / 10 = 23.4.
		expect(centredMeanKmh(series(t, d), 12, 10)).toBeCloseTo(23.4, 10);
	});

	it('is exact under constant acceleration, at any width — what centring buys', () => {
		// d = t^2 means v = 2t m/s. Centred differencing cancels the quadratic term exactly, so the
		// answer is the true instantaneous speed regardless of window width. A TRAILING window over
		// the same data would read (d(t) - d(t-W))/W — at t=10, W=10 that is 10 m/s (36 km/h) against
		// a true 20 m/s (72 km/h). That factor-of-two error is the lag this module exists to remove.
		const t: number[] = [], d: number[] = [];
		for (let i = 0; i <= 40; i++) { t.push(i); d.push((i * i) / 1000); }
		const s = series(t, d);
		for (const w of [5, 10, 20]) {
			expect(centredMeanKmh(s, 10, w)).toBeCloseTo(72, 6);
			expect(centredMeanKmh(s, 25, w)).toBeCloseTo(180, 6);
		}
	});

	it('is CENTRED, not trailing — a step change is half-crossed at the step itself', () => {
		// Trailing would still read the full 36 at t=10 (it has seen nothing of the slow leg yet).
		const t: number[] = [], d: number[] = [];
		for (let i = 0; i <= 20; i++) {
			t.push(i);
			d.push(i <= 10 ? (i * 36) / 3600 : (10 * 36) / 3600 + ((i - 10) * 18) / 3600);
		}
		const at10 = centredMeanKmh(series(t, d), 10, 10);
		expect(at10).toBeLessThan(36);
		expect(at10).toBeGreaterThan(18);
	});

	it('truncates at the ends and divides by the span that exists', () => {
		// A full-window divisor would read half speed at t=0. The mean over what happened is 36.
		const s = constantSpeed(36);
		expect(centredMeanKmh(s, 0, 10)).toBeCloseTo(36, 10);
		expect(centredMeanKmh(s, 20, 10)).toBeCloseTo(36, 10);
	});

	it('reads the nearest end when the playhead is outside the series', () => {
		const s = constantSpeed(36);
		expect(centredMeanKmh(s, -50, 10)).toBeCloseTo(36, 10);
		expect(centredMeanKmh(s, 500, 10)).toBeCloseTo(36, 10);
	});

	it('is a pure function of the playhead — no history, warm or cold', () => {
		// The property the old frame-sample window could not hold: what the label reads at t must not
		// depend on which playheads were evaluated before it.
		const s = constantSpeed(24);
		const cold = centredMeanKmh(s, 13.25, 8);
		for (let t = 0; t <= 20; t += 0.1) centredMeanKmh(s, t, 8);
		expect(centredMeanKmh(s, 13.25, 8)).toBe(cold);
	});

	it('does not care how often it is asked — the old window collapsed to one sample at high rates', () => {
		// The estimator takes no samples, so evaluating it 60x per ride-second and once per two
		// ride-seconds must agree exactly at the playheads they share.
		const s = constantSpeed(31.5);
		const shared = [0, 2, 4, 6, 8, 10];
		const coarse = shared.map((t) => centredMeanKmh(s, t, 12));
		for (let step = 0; step <= 600; step++) centredMeanKmh(s, step / 60, 12); // a "60 fps" pass
		expect(shared.map((t) => centredMeanKmh(s, t, 12))).toEqual(coarse);
	});

	it('reads the same scrubbing backwards as playing forwards', () => {
		const s = constantSpeed(19);
		const forwards: (number | null)[] = [];
		for (let t = 0; t <= 20; t += 1) forwards.push(centredMeanKmh(s, t, 7));
		const backwards: (number | null)[] = [];
		for (let t = 20; t >= 0; t -= 1) backwards.push(centredMeanKmh(s, t, 7));
		expect(backwards.reverse()).toEqual(forwards);
	});

	it('reads a standstill rather than a negative speed when distance steps backwards', () => {
		// Effort distance mixes a DB cumulative channel with a haversine fallback, which can step back.
		const s = series([0, 1, 2, 3], [0, 0.01, 0.004, 0.02]);
		const v = centredMeanKmh(s, 1.5, 2);
		expect(v).not.toBeNull();
		expect(v).toBeGreaterThanOrEqual(0);
	});

	it('returns null rather than NaN when the series cannot answer', () => {
		expect(centredMeanKmh(series([5], [1]), 5, 10)).toBeNull();          // one point
		expect(centredMeanKmh(series([], []), 0, 10)).toBeNull();            // empty
		expect(centredMeanKmh(series([4, 4, 4], [0, 1, 2]), 4, 10)).toBeNull(); // no time span
		expect(centredMeanKmh(constantSpeed(30), Number.NaN, 10)).toBeNull();
		expect(centredMeanKmh(constantSpeed(30), 5, 0)).toBeNull();
		expect(centredMeanKmh(series([0, 1], [0, Number.NaN]), 0.5, 10)).toBeNull();
	});
});
