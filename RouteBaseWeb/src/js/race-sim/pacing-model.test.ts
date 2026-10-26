// @vitest-environment node
import { describe, expect, it } from 'vitest';
import {
	DEFAULT_FATIGUE_K,
	fitCostCurve,
	costAt,
	simulateArrivals,
	type Anchor,
	type CourseProfile,
	type CostCurve,
} from './pacing-model';

/** A course grid with a caller-supplied grade profile. 10 m nodes, like the C# builder. */
function grid(grades: number[], stepMeters = 10): CourseProfile {
	return {
		distance: grades.map((_, i) => i * stepMeters),
		gradePercent: grades,
		stepMeters,
	};
}

/** Flat course, `n` nodes. */
function flatGrid(n: number): CourseProfile {
	return grid(new Array(n).fill(0));
}

/** A curve resembling the one fitted from the real ride: slow uphill, fast downhill, plateaued. */
function realisticCurve(): CostCurve {
	const samples: Array<{ gradePercent: number; secondsPerMetre: number }> = [];
	// s/m at each grade — from SpikeResults.md's fitted table.
	const table: Array<[number, number]> = [
		[-20, 0.0761], [-15, 0.0875], [-10, 0.0875], [-5, 0.1075],
		[0, 0.1608], [5, 0.3179], [10, 0.5372], [15, 0.6494],
	];
	for (const [g, c] of table) {
		for (let k = 0; k < 5; k++) samples.push({ gradePercent: g, secondsPerMetre: c });
	}
	return fitCostCurve(samples);
}

const startFinish = (n: number, seconds: number): Anchor[] => [
	{ node: 0, elapsedSeconds: 0 },
	{ node: n - 1, elapsedSeconds: seconds },
];

describe('fitCostCurve', () => {
	it('is monotone non-decreasing in grade — cost never falls as the road tilts up', () => {
		// Raw medians that OSCILLATE on the descent: the -15% bin reads slower than the bins 5% either
		// side of it (a real artefact — a technical switchback). Isotonic regression must absorb it.
		const samples: Array<{ gradePercent: number; secondsPerMetre: number }> = [];
		const raw: Array<[number, number]> = [
			[-20, 0.076], [-15, 0.205], [-10, 0.074], [-5, 0.111], [0, 0.161], [5, 0.324],
		];
		for (const [g, c] of raw) {
			for (let k = 0; k < 5; k++) samples.push({ gradePercent: g, secondsPerMetre: c });
		}

		const curve = fitCostCurve(samples);

		for (let i = 1; i < curve.cost.length; i++) {
			expect(curve.cost[i]).toBeGreaterThanOrEqual((curve.cost[i - 1] ?? NaN) - 1e-12);
		}
		// The -15% spike is gone: it cannot be slower than the flat-ground cost.
		expect(costAt(curve, -15)).toBeLessThan(costAt(curve, 0));
	});

	it('plateaus on steep descents instead of extrapolating ever faster (the speed ceiling)', () => {
		const curve = realisticCurve();

		const at20 = 3.6 / costAt(curve, -20);
		const at35 = 3.6 / costAt(curve, -35);   // beyond the fitted range

		expect(at35).toBeCloseTo(at20, 5);       // clamped, not extrapolated
		expect(at35).toBeLessThan(60);           // nobody descends this course at 60 km/h
	});

	it('rejects a sample set with no usable bin', () => {
		expect(() => fitCostCurve([{ gradePercent: 0, secondsPerMetre: 0.1 }])).toThrow();
	});
});

describe('simulateArrivals', () => {
	it('hits the finish time EXACTLY — the whole guarantee of the model', () => {
		const g = grid([...new Array(200).fill(8), ...new Array(200).fill(-8)]);
		const curve = realisticCurve();

		const arrivals = simulateArrivals(g, curve, startFinish(g.distance.length, 8939));

		expect(arrivals[arrivals.length - 1]).toBeCloseTo(8939, 2);
	});

	it('hits EVERY checkpoint exactly, not just the finish', () => {
		const g = grid([...new Array(150).fill(10), ...new Array(150).fill(-10), ...new Array(100).fill(0)]);
		const curve = realisticCurve();
		const anchors: Anchor[] = [
			{ node: 0, elapsedSeconds: 0 },
			{ node: 120, elapsedSeconds: 900 },
			{ node: 280, elapsedSeconds: 1500 },
			{ node: 399, elapsedSeconds: 2400 },
		];

		const arrivals = simulateArrivals(g, curve, anchors);

		for (const a of anchors) expect(arrivals[a.node]).toBeCloseTo(a.elapsedSeconds, 2);
	});

	it('is slower uphill and faster downhill — the point of the whole exercise', () => {
		// Same distance, opposite gradient. Equal-length halves, so a constant-pace model would take
		// exactly as long over each.
		const g = grid([...new Array(200).fill(10), ...new Array(200).fill(-10)]);
		const curve = realisticCurve();

		const arrivals = simulateArrivals(g, curve, startFinish(g.distance.length, 3600));

		const halfway = arrivals[200];
		expect(halfway).toBeGreaterThan(3600 * 0.75);   // the climb eats most of the clock
	});

	it('never goes backwards', () => {
		const g = grid([...new Array(100).fill(15), ...new Array(100).fill(-30), ...new Array(100).fill(0)]);
		const curve = realisticCurve();

		const arrivals = simulateArrivals(g, curve, startFinish(g.distance.length, 5000), {
			noise: 0.3,
			seed: 42,
		});

		for (let i = 1; i < arrivals.length; i++) {
			expect(arrivals[i]).toBeGreaterThanOrEqual(arrivals[i - 1] ?? NaN);
		}
	});

	it('applies fatigue to a finish-only rider (he fades)', () => {
		const g = flatGrid(401);
		const curve = realisticCurve();

		const withFade = simulateArrivals(g, curve, startFinish(401, 4000), { fatigueK: DEFAULT_FATIGUE_K });
		const noFade = simulateArrivals(g, curve, startFinish(401, 4000), { fatigueK: 0 });

		// On flat ground, no-fade is exactly constant pace. Fading means being AHEAD at halfway and
		// paying for it later — and still finishing on the same second.
		expect(withFade[200]).toBeLessThan(noFade[200] ?? NaN);
		expect(withFade[400]).toBeCloseTo(noFade[400] ?? NaN, 2);
	});

	it('does NOT apply fatigue once real splits exist — checkpoints measure what fatigue guesses', () => {
		// The spike's key finding: fatigue and checkpoints are substitutes. Stacking them made the fit
		// worse (median drift 142 m -> 173 m), so the model must ignore fatigueK when anchors are present.
		const g = flatGrid(401);
		const curve = realisticCurve();
		const anchors: Anchor[] = [
			{ node: 0, elapsedSeconds: 0 },
			{ node: 200, elapsedSeconds: 1800 },
			{ node: 400, elapsedSeconds: 4000 },
		];

		const withK = simulateArrivals(g, curve, anchors, { fatigueK: 0.9 });
		const withoutK = simulateArrivals(g, curve, anchors, { fatigueK: 0 });

		for (let i = 0; i < withK.length; i++) expect(withK[i]).toBeCloseTo(withoutK[i] ?? NaN, 3);
	});

	it('ability alpha bends hill response without touching the finish time', () => {
		const g = grid([...new Array(200).fill(10), ...new Array(200).fill(-10)]);
		const last = g.distance.length - 1;
		const curve = realisticCurve();

		const strong = simulateArrivals(g, curve, startFinish(g.distance.length, 3600), { abilityAlpha: 0.6 });
		const weak = simulateArrivals(g, curve, startFinish(g.distance.length, 3600), { abilityAlpha: 1.4 });

		// The weaker rider loses MORE of his race to the climb...
		expect(weak[200]).toBeGreaterThan(strong[200] ?? NaN);
		// ...but both still cross the line on their recorded time.
		expect(strong[last]).toBeCloseTo(3600, 2);
		expect(weak[last]).toBeCloseTo(3600, 2);
	});

	it('is deterministic for a given seed — a replay must look identical every time', () => {
		const g = grid([...new Array(200).fill(5), ...new Array(200).fill(-5)]);
		const curve = realisticCurve();
		const opts = { noise: 0.25, seed: 1234 };

		const a = simulateArrivals(g, curve, startFinish(g.distance.length, 3000), opts);
		const b = simulateArrivals(g, curve, startFinish(g.distance.length, 3000), opts);

		expect(Array.from(a)).toEqual(Array.from(b));
	});

	it('noise reshapes the ride but never the finish time', () => {
		const g = grid([...new Array(200).fill(5), ...new Array(200).fill(-5)]);
		const last = g.distance.length - 1;
		const curve = realisticCurve();

		const noisy = simulateArrivals(g, curve, startFinish(g.distance.length, 3000), { noise: 0.3, seed: 7 });
		const clean = simulateArrivals(g, curve, startFinish(g.distance.length, 3000), { noise: 0 });

		expect(noisy[200]).not.toBeCloseTo(clean[200] ?? NaN, 1);
		expect(noisy[last]).toBeCloseTo(3000, 2);
	});

	it('rejects an anchor past the end of the course instead of quietly producing NaN', () => {
		// Caught for real while writing these tests: an off-the-end anchor walked off the grade array and
		// filled the whole ride with NaN, which would have surfaced as markers that simply never appear.
		const g = flatGrid(100);
		const curve = realisticCurve();

		expect(() => simulateArrivals(g, curve, startFinish(101, 3000))).toThrow(/outside the course/);
	});

	it('rejects anchors that go backwards', () => {
		const g = flatGrid(101);
		const curve = realisticCurve();

		expect(() =>
			simulateArrivals(g, curve, [
				{ node: 0, elapsedSeconds: 0 },
				{ node: 50, elapsedSeconds: 600 },
				{ node: 40, elapsedSeconds: 900 },
			]),
		).toThrow(/increasing/);
	});

	it('holds a DNF rider at his last checkpoint rather than teleporting him to the finish', () => {
		const g = flatGrid(401);
		const curve = realisticCurve();

		const arrivals = simulateArrivals(g, curve, [
			{ node: 0, elapsedSeconds: 0 },
			{ node: 200, elapsedSeconds: 1800 },
		]);

		expect(arrivals[200]).toBeCloseTo(1800, 2);
		expect(arrivals[400]).toBeCloseTo(1800, 2);
	});

	/**
	 * The model validates its anchors — node bounds, strictly increasing nodes, non-decreasing times —
	 * and the comment above that check says why: an unvalidated anchor "would quietly fill the whole ride
	 * with NaN, which surfaces much later as markers that simply never appear".
	 *
	 * The time check cannot do its job. `elapsedSeconds < previous` is FALSE when elapsedSeconds is NaN,
	 * so a NaN anchor walks straight through the guard and does precisely what the comment warns of:
	 * `span = t1 - t0` is NaN, and every arrival after it is NaN.
	 *
	 * Not reachable from `buildRace` today — `anchorsFor` happens to filter NaN out with its
	 * `elapsedSeconds > 0` test. But that is an accident of one caller, not a property of the model, and
	 * this is an exported pure function whose stated contract is that it validates. Same defeated-bounds
	 * check as the cost-curve and CSV bugs.
	 */
	it('rejects a non-finite anchor time instead of filling the ride with NaN', () => {
		const g = flatGrid(101);
		const curve = realisticCurve();

		expect(() =>
			simulateArrivals(g, curve, [
				{ node: 0, elapsedSeconds: 0 },
				{ node: 50, elapsedSeconds: Number.NaN },
				{ node: 100, elapsedSeconds: 3600 },
			]),
		).toThrow(/finite/i);
	});
});
