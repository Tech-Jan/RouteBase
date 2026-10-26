// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from '../core/invariant';
import { RaceField, createThinSource, type CourseGrid } from './race-field';
import { buildSidebarRows } from './race-sidebar-rows';
import type { RaceParticipantDto } from './race-api';

const STEP = 10;

/** A straight, flat course of `n` nodes 10 m apart — 10·(n-1) metres long. */
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

/** Constant-pace arrivals: the whole course covered in `total` seconds. */
function evenArrivals(n: number, total: number): Float32Array {
	const a = new Float32Array(n);
	for (let i = 0; i < n; i++) a[i] = (total * i) / (n - 1);
	return a;
}

function participant(over: Partial<RaceParticipantDto> = {}): RaceParticipantDto {
	return {
		participantId: 1,
		kind: 'simulated',
		name: null,
		bib: null,
		category: null,
		gender: null,
		club: null,
		seed: 0,
		color: null,
		emphasised: false, calloutOff: false,
		officialFinishSeconds: null,
		finishStatus: null,
		finishStatusRaw: null,
		startOffsetSeconds: null,
		wave: null,
		discipline: null,
		splits: [],
		track: null,
		...over,
	};
}

describe('buildSidebarRows', () => {
	it('ranks the field by chainage with the leader at position 1 and gap 0', () => {
		// 100 m course; rider 0 finishes in 100 s, rider 1 in 200 s. At t=50: 50 m vs 25 m.
		const g = course(11);
		const field = new RaceField([
			createThinSource(g, evenArrivals(11, 100)),
			createThinSource(g, evenArrivals(11, 200)),
		]);
		field.advanceTo(50);

		const rows = buildSidebarRows(field, [
			participant({ name: 'Fast' }),
			participant({ name: 'Slow' }),
		], ['finished', 'finished']);

		expect(rows.map((r) => r.name)).toEqual(['Fast', 'Slow']);
		expect(rows.map((r) => r.position)).toEqual([1, 2]);
		expect(rows.map((r) => r.index)).toEqual([0, 1]);
		expect(must(rows[0], 'row 0').gapMetres).toBe(0);
		expect(must(rows[1], 'row 1').gapMetres).toBeCloseTo(25, 3);
	});

	it('falls back name → bib → placeholder, and flags real riders', () => {
		const g = course(11);
		const field = new RaceField([
			createThinSource(g, evenArrivals(11, 100)),
			createThinSource(g, evenArrivals(11, 150)),
			createThinSource(g, evenArrivals(11, 200)),
		]);
		field.advanceTo(10);

		const rows = buildSidebarRows(field, [
			participant({ name: 'Named', kind: 'real' }),
			participant({ name: null, bib: '42' }),
			participant({ name: null, bib: null }),
		], ['finished', 'finished', 'finished']);

		expect(rows.map((r) => r.name)).toEqual(['Named', '42', '—']);
		expect(rows.map((r) => r.isReal)).toEqual([true, false, false]);
	});

	it('marks stopped riders and never emits a negative gap', () => {
		const g = course(11);
		const field = new RaceField([
			createThinSource(g, evenArrivals(11, 100)),
			createThinSource(g, evenArrivals(11, 200)),
		]);
		field.advanceTo(150); // rider 0 stopped (100 m), rider 1 at 75 m

		const rows = buildSidebarRows(field, [participant(), participant()], ['finished', 'finished']);

		expect(must(rows[0], 'row 0').stopped).toBe(true);
		expect(must(rows[1], 'row 1').stopped).toBe(false);
		for (const r of rows) expect(r.gapMetres).toBeGreaterThanOrEqual(0);
	});

	// `stopped` and `status` are orthogonal on purpose. `stopped` is time-aware — has the playhead passed
	// his last time? `status` is static — what did the results file say about his race? A DNF is the
	// combination the sidebar used to have no way to express, and so labelled "fin".
	it('separates STOPPED from the RESULT — a DNF is stopped without finishing', () => {
		const g = course(11);
		// Rider 1's source ends at node 5 (half the course): he abandoned there.
		const field = new RaceField([
			createThinSource(g, evenArrivals(11, 100)),
			createThinSource(g, evenArrivals(11, 200), 5),
		]);
		field.advanceTo(150);

		const rows = buildSidebarRows(field, [participant(), participant()], ['finished', 'dnf']);
		const dnf = must(rows.find((r) => r.index === 1), 'the abandoned rider');

		expect(dnf.stopped).toBe(true);
		expect(dnf.status).toBe('dnf');
		expect(must(rows.find((r) => r.index === 0), 'the finisher').status).toBe('finished');
	});

	it('a rider still riding is neither stopped nor yet at the line, however he ends up', () => {
		const g = course(11);
		const field = new RaceField([createThinSource(g, evenArrivals(11, 100))]);
		field.advanceTo(50);

		const rows = buildSidebarRows(field, [participant()], ['finished']);

		expect(must(rows[0], 'row 0').stopped).toBe(false);
		expect(must(rows[0], 'row 0').status).toBe('finished');
	});

	it('returns an empty board for an empty field', () => {
		expect(buildSidebarRows(new RaceField([]), [], [])).toEqual([]);
	});

	/**
	 * The ordering rule that a real rider's projected chainage broke. His DTW chainage tops out tens of
	 * metres short of the line (RaceRealParticipantService admits ±40 m) while every simulated finisher is
	 * parked on the exact finish node — so ordering a finished field by chainage buried the one rider whose
	 * data is real underneath 800 modelled ones. A finisher is placed by his TIME.
	 */
	describe('once riders have crossed the line', () => {
		it('places finishers by finish time, not by the metre their chainage stopped at', () => {
			const g = course(11);
			// Rider 1 finishes FIRST (t=90) but his source stops one node short of the line, at 90 m — a
			// real rider's projection, in miniature. Rider 0 takes until t=150 and parks on the finish node.
			const field = new RaceField([
				createThinSource(g, evenArrivals(11, 150)),
				createThinSource(g, evenArrivals(11, 100), 9),
			]);
			field.advanceTo(500);

			const rows = buildSidebarRows(field, [
				participant({ name: 'Simulated' }),
				participant({ name: 'Real', kind: 'real' }),
			], ['finished', 'finished']);

			expect(rows.map((r) => r.name)).toEqual(['Real', 'Simulated']);
			expect(rows.map((r) => r.position)).toEqual([1, 2]);
		});

		it('puts a finisher ahead of anyone still out on the course', () => {
			const g = course(11);
			const field = new RaceField([
				createThinSource(g, evenArrivals(11, 400)),   // still riding at t=120
				createThinSource(g, evenArrivals(11, 100)),   // done at t=100
			]);
			field.advanceTo(120);

			const rows = buildSidebarRows(field, [
				participant({ name: 'Riding' }),
				participant({ name: 'Done' }),
			], ['finished', 'finished']);

			expect(rows.map((r) => r.name)).toEqual(['Done', 'Riding']);
		});

		/** `finished` means "past his last time" — an abandoned rider is that too, and must not be placed. */
		it('does not promote an abandoned rider just because he has stopped', () => {
			const g = course(11);
			const field = new RaceField([
				createThinSource(g, evenArrivals(11, 200)),      // still riding, 60 m in
				createThinSource(g, evenArrivals(11, 50), 3),    // quit at 30 m, and long ago
			]);
			field.advanceTo(120);

			const rows = buildSidebarRows(field, [
				participant({ name: 'Riding' }),
				participant({ name: 'Abandoned' }),
			], ['finished', 'dnf']);

			expect(rows.map((r) => r.name)).toEqual(['Riding', 'Abandoned']);
		});

		/**
		 * An abandoned rider's marker is parked where he quit, so ordering him by chainage wedged him among
		 * riders still racing — ahead of everyone behind that point, drifting down as they passed. He is
		 * out of the race; he belongs at the bottom.
		 */
		it('drops an abandoned rider BELOW everyone still racing, however far he got', () => {
			const g = course(11);
			const field = new RaceField([
				createThinSource(g, evenArrivals(11, 100), 9),   // quit at 90 m — nearly the whole course
				createThinSource(g, evenArrivals(11, 800)),      // still riding, barely started
				createThinSource(g, evenArrivals(11, 400)),      // still riding, further along
			]);
			field.advanceTo(120);

			const rows = buildSidebarRows(field, [
				participant({ name: 'Quit at 90m' }),
				participant({ name: 'Slow' }),
				participant({ name: 'Faster' }),
			], ['dnf', 'finished', 'finished']);

			expect(rows.map((r) => r.name)).toEqual(['Faster', 'Slow', 'Quit at 90m']);
		});

		it('orders the bottom block by when they dropped out, so it reads DNF 1, 2, 3 downwards', () => {
			const g = course(11);
			const field = new RaceField([
				createThinSource(g, evenArrivals(11, 300), 5),   // quit at t=150
				createThinSource(g, evenArrivals(11, 100), 5),   // quit at t=50  — first out
				createThinSource(g, evenArrivals(11, 200), 5),   // quit at t=100
			]);
			field.advanceTo(500);

			const rows = buildSidebarRows(field, [
				participant({ name: 'Third out' }),
				participant({ name: 'First out' }),
				participant({ name: 'Second out' }),
			], ['dnf', 'dnf', 'dnf']);

			expect(rows.map((r) => r.name)).toEqual(['First out', 'Second out', 'Third out']);
		});

		/** The block fills one rider at a time: until his abandon moment he is just another rider. */
		it('leaves a would-be DNF in the field until he actually stops', () => {
			const g = course(11);
			const field = new RaceField([
				createThinSource(g, evenArrivals(11, 400)),      // slow, still riding
				createThinSource(g, evenArrivals(11, 100), 9),   // quits at t=90 — but not yet
			]);
			field.advanceTo(50);   // he is at 50 m, ahead of the slow rider's 12.5 m

			const rows = buildSidebarRows(field, [
				participant({ name: 'Slow' }),
				participant({ name: 'Will quit' }),
			], ['finished', 'dnf']);

			expect(rows.map((r) => r.name)).toEqual(['Will quit', 'Slow']);
		});

		it('lets two riders share a place when their times are equal', () => {
			const g = course(11);
			const field = new RaceField([
				createThinSource(g, evenArrivals(11, 100)),
				createThinSource(g, evenArrivals(11, 100)),
			]);
			field.advanceTo(500);

			const rows = buildSidebarRows(field, [participant(), participant()], ['finished', 'finished']);

			// Same end time: neither is ordered ahead on merit, and both are placed among the finishers
			// rather than one of them sinking below the field.
			expect(rows.map((r) => r.position)).toEqual([1, 2]);
			expect(rows.every((r) => r.stopped)).toBe(true);
		});
	});

	describe('an abandoned rider keeps the number he had when he stopped', () => {
		it('uses the frozen position and flags it, instead of his sinking live rank', () => {
			const g = course(11);
			const field = new RaceField([
				createThinSource(g, evenArrivals(11, 100)),
				createThinSource(g, evenArrivals(11, 200), 3),   // quit at 30 m
			]);
			field.advanceTo(500);   // long after: the live rank would have him last

			const rows = buildSidebarRows(field, [participant(), participant()], ['finished', 'dnf'],
				new Map([[1, { position: 1, ordinal: 4 }]]));
			const quit = must(rows.find((r) => r.index === 1), 'the abandoned rider');

			expect(quit.position).toBe(1);          // what he held when he stopped, not rank 2
			expect(quit.positionFrozen).toBe(true);
			expect(quit.statusOrdinal).toBe(4);
		});

		/** Until he actually stops he is riding, and his live position is the true one. */
		it('leaves a rider who has not stopped yet on his live rank', () => {
			const g = course(11);
			const field = new RaceField([
				createThinSource(g, evenArrivals(11, 100)),
				createThinSource(g, evenArrivals(11, 200)),
			]);
			field.advanceTo(50);

			const rows = buildSidebarRows(field, [participant(), participant()], ['finished', 'dnf'],
				new Map([[1, { position: 1, ordinal: 1 }]]));
			const riding = must(rows.find((r) => r.index === 1), 'the rider still going');

			expect(riding.position).toBe(2);
			expect(riding.positionFrozen).toBe(false);
			expect(riding.statusOrdinal).toBeNull();
		});

		it('leaves finishers alone — they hold a real place', () => {
			const g = course(11);
			const field = new RaceField([createThinSource(g, evenArrivals(11, 100))]);
			field.advanceTo(500);

			const rows = buildSidebarRows(field, [participant()], ['finished'], new Map());

			expect(must(rows[0], 'the finisher').positionFrozen).toBe(false);
			expect(must(rows[0], 'the finisher').statusOrdinal).toBeNull();
		});
	});

	it('carries the emphasis flag through — the sidebar pins those rows above the field', () => {
		const g = course(11);
		const field = new RaceField([
			createThinSource(g, evenArrivals(11, 100)),
			createThinSource(g, evenArrivals(11, 200)),
		]);
		field.advanceTo(50);

		const rows = buildSidebarRows(field, [
			participant({ emphasised: true }),
			participant(),
		], ['finished', 'finished']);

		expect(rows.map((r) => r.emphasised)).toEqual([true, false]);
	});
});
