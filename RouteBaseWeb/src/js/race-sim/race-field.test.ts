// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from '../core/invariant';
import { RaceField, createRichSource, createThinSource, type CourseGrid, type Sample } from './race-field';

const STEP = 10;

/** A straight due-east course of `n` nodes, 10 m apart, flat. */
function course(n: number): CourseGrid {
	const distance = new Float64Array(n);
	const lat = new Float64Array(n);
	const lng = new Float64Array(n);
	const elevation = new Float64Array(n);
	const gradePercent = new Float64Array(n);
	for (let i = 0; i < n; i++) {
		distance[i] = i * STEP;
		lat[i] = 47.9;
		lng[i] = 8.1 + i * 0.0001;
		elevation[i] = 500;
	}
	return { distance, lat, lng, elevation, gradePercent, stepMeters: STEP };
}

/** Constant-pace arrivals over the whole course. */
function evenArrivals(n: number, total: number): Float32Array {
	const a = new Float32Array(n);
	for (let i = 0; i < n; i++) a[i] = (total * i) / (n - 1);
	return a;
}

const sample = (): Sample => ({ lat: 0, lng: 0, chainage: 0, finished: false });

describe('createThinSource (a simulated rider)', () => {
	it('rides the course line, and its chainage tracks the clock', () => {
		const g = course(101);          // 1000 m
		const src = createThinSource(g, evenArrivals(101, 1000));
		const out = sample();

		src.sampleAt(500, out);

		expect(out.chainage).toBeCloseTo(500, 1);
		expect(out.lat).toBeCloseTo(47.9, 9);                 // on the course, by construction
		expect(out.lng).toBeCloseTo(g.lng[50] as number, 6);
		expect(out.finished).toBe(false);
	});

	it('interpolates between nodes rather than snapping to them', () => {
		const g = course(101);
		const src = createThinSource(g, evenArrivals(101, 1000));
		const out = sample();

		src.sampleAt(505, out);

		expect(out.chainage).toBeCloseTo(505, 1);             // NOT 500 or 510
	});

	it('holds at the finish, and reports finished', () => {
		const g = course(101);
		const src = createThinSource(g, evenArrivals(101, 1000));
		const out = sample();

		src.sampleAt(99999, out);

		expect(out.chainage).toBeCloseTo(1000, 1);
		expect(out.finished).toBe(true);
	});

	it('survives a stopped clock (equal arrival times) without dividing by zero', () => {
		// A multi-day stage boundary: the clock does not run overnight, so arrivals repeat.
		const g = course(11);
		const arrivals = Float32Array.from([0, 10, 20, 30, 30, 30, 40, 50, 60, 70, 80]);
		const src = createThinSource(g, arrivals);
		const out = sample();

		src.sampleAt(30, out);

		expect(Number.isFinite(out.lat)).toBe(true);
		expect(Number.isFinite(out.chainage)).toBe(true);
	});
});

describe('createRichSource (a real rider)', () => {
	it('rides HIS OWN line, not the course line — a detour is visible', () => {
		// He wanders 0.01 deg north of the course at the midpoint: a wrong turn.
		const lat = [47.9, 47.91, 47.9];
		const lng = [8.1, 8.105, 8.11];
		const time = [0, 500, 1000];
		const chainage = [0, 500, 1000];
		const src = createRichSource(lat, lng, time, chainage);
		const out = sample();

		src.sampleAt(500, out);

		// THE point of including real riders: his marker leaves the course.
		expect(out.lat).toBeCloseTo(47.91, 6);
		expect(out.lat).not.toBeCloseTo(47.9, 3);
		// ...while still ranking against the field at his true progress.
		expect(out.chainage).toBeCloseTo(500, 1);
	});

	it('needs at least two points', () => {
		expect(() => createRichSource([47.9], [8.1], [0], [0])).toThrow();
	});
});

describe('RaceField', () => {
	it('mixes real and simulated riders in one field', () => {
		const g = course(101);
		const field = new RaceField([
			createThinSource(g, evenArrivals(101, 1000)),
			createRichSource([47.95, 47.95], [8.1, 8.2], [0, 1000], [0, 1000]),
		]);

		field.advanceTo(500);

		expect(field.size).toBe(2);
		expect(field.lats[0]).toBeCloseTo(47.9, 6);    // simulated: on the course
		expect(field.lats[1]).toBeCloseTo(47.95, 6);   // real: on his own line
	});

	it('ranks the field by progress — furthest along first', () => {
		const g = course(101);
		const field = new RaceField([
			createThinSource(g, evenArrivals(101, 2000)),   // slow
			createThinSource(g, evenArrivals(101, 1000)),   // fast
			createThinSource(g, evenArrivals(101, 1500)),   // middling
		]);

		field.advanceTo(500);

		expect(field.order()).toEqual([1, 2, 0]);
	});

	it('leadChainage() equals the leader\'s chainage — the O(P) playhead read the render loop uses', () => {
		const g = course(101);
		const field = new RaceField([
			createThinSource(g, evenArrivals(101, 2000)),   // slow
			createThinSource(g, evenArrivals(101, 1000)),   // fast — the leader
			createThinSource(g, evenArrivals(101, 1500)),   // middling
		]);

		field.advanceTo(500);

		// Exactly chainages[order()[0]] — the value the per-frame charts playhead needs, without the sort.
		const order = field.order();
		expect(field.leadChainage()).toBe(field.chainages[must(order[0], 'non-empty order')]);
	});

	it('leadChainage() is null on an empty field, matching order()\'s empty result', () => {
		expect(new RaceField([]).leadChainage()).toBeNull();
	});

	it('seeking backwards gives the same answer as arriving there forwards', () => {
		// The cursor is an optimisation; it must not become a source of truth.
		const g = course(501);
		const make = () => new RaceField([createThinSource(g, evenArrivals(501, 5000))]);

		const forward = make();
		forward.advanceTo(1200);

		const rewound = make();
		rewound.advanceTo(4800);   // run to near the end...
		rewound.advanceTo(1200);   // ...then seek back

		expect(rewound.lats[0]).toBeCloseTo(forward.lats[0] ?? NaN, 9);
		expect(rewound.lngs[0]).toBeCloseTo(forward.lngs[0] ?? NaN, 9);
		expect(rewound.chainages[0]).toBeCloseTo(forward.chainages[0] ?? NaN, 3);
	});

	it('endSeconds is the last rider home', () => {
		const g = course(101);
		const field = new RaceField([
			createThinSource(g, evenArrivals(101, 1000)),
			createThinSource(g, evenArrivals(101, 3600)),
		]);

		expect(field.endSeconds).toBeCloseTo(3600, 1);
	});

	it('costs well under a frame budget for the real field: 424 riders on a 39 km course', () => {
		// THE scaling question. 424 finishers, 3,892 nodes at 10 m — the actual Black Forest SHORT TRACK.
		// A 60 fps frame is 16.6 ms; this is only the position maths, so it must be a rounding error
		// against MapLibre's own upload cost.
		const g = course(3892);
		const sources = Array.from({ length: 424 }, (_, i) =>
			createThinSource(g, evenArrivals(3892, 4600 + i * 15)),
		);
		const field = new RaceField(sources);

		const frames = 600;                       // 10 s of playback at 60 fps
		const end = field.endSeconds;
		const t0 = performance.now();
		for (let f = 0; f < frames; f++) field.advanceTo((end * f) / frames);
		const perFrame = (performance.now() - t0) / frames;

		// Generous bound — CI machines are slow and this is a guard against an O(n log n) regression
		// (a binary search per rider per frame), not a benchmark.
		expect(perFrame).toBeLessThan(2.0);
		console.info(`RaceField: 424 riders x 3892 nodes -> ${perFrame.toFixed(3)} ms/frame`);
	});
});

// RS-12: the two factories are one cursor sampler now (they were the same 30 lines twice). The tests
// above pin each on its own; this pins that they still AGREE, which is the property the extraction could
// have broken silently.
describe('thin and rich sources agree on the same ride', () => {
	it('a real rider riding the course exactly matches the simulated rider who rides it at the same pace', () => {
		const n = 101;
		const g = course(n);
		const total = 1000;

		const thin = createThinSource(g, evenArrivals(n, total));
		const rich = createRichSource(
			g.lat as Float64Array,
			g.lng as Float64Array,
			// His own GPS: the same geometry, the same timing, and a chainage channel that follows the
			// course exactly — so there is nothing for the two to disagree about.
			Float32Array.from({ length: n }, (_, i) => (total * i) / (n - 1)),
			Float32Array.from({ length: n }, (_, i) => i * STEP),
		);

		const a = sample();
		const b = sample();
		for (let k = 0; k <= 20; k++) {
			const t = (total * k) / 20;
			thin.sampleAt(t, a);
			rich.sampleAt(t, b);

			expect(b.lat, `lat @${t}`).toBeCloseTo(a.lat, 9);
			expect(b.lng, `lng @${t}`).toBeCloseTo(a.lng, 9);
			expect(b.chainage, `chainage @${t}`).toBeCloseTo(a.chainage, 9);
			expect(b.finished, `finished @${t}`).toBe(a.finished);
		}
	});

	it('both clamp before the start and after the finish, and both hold a stopped clock', () => {
		const n = 5;
		const g = course(n);
		// A stationary stretch: nodes 1..3 share an arrival time (a stopped clock at a checkpoint).
		const arrivals = Float32Array.from([0, 100, 100, 100, 200]);

		const thin = createThinSource(g, arrivals);
		const rich = createRichSource(
			g.lat as Float64Array, g.lng as Float64Array,
			arrivals,
			Float32Array.from({ length: n }, (_, i) => i * STEP),
		);

		const a = sample();
		const b = sample();
		for (const t of [-50, 0, 50, 100, 150, 200, 500]) {
			thin.sampleAt(t, a);
			rich.sampleAt(t, b);
			expect(b.chainage, `chainage @${t}`).toBeCloseTo(a.chainage, 9);
			expect(b.finished, `finished @${t}`).toBe(a.finished);
			expect(Number.isNaN(a.chainage), `thin NaN @${t}`).toBe(false);
			expect(Number.isNaN(b.chainage), `rich NaN @${t}`).toBe(false);
		}
	});
});
