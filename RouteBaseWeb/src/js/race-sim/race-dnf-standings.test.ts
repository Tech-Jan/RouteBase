// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { RaceField, createThinSource, type CourseGrid } from './race-field';
import { computeDnfStandings } from './race-dnf-standings';
import type { RaceFinishStatus } from './race-finish-status';

const STEP = 10;

/** A straight, flat course of `n` nodes 10 m apart — 10·(n-1) metres long. */
function course(n: number): CourseGrid {
	const distance = new Float64Array(n);
	const lat = new Float64Array(n);
	const lng = new Float64Array(n);
	for (let i = 0; i < n; i++) {
		distance[i] = i * STEP;
		lat[i] = 47.9;
		lng[i] = 8.1 + i * 0.0001;
	}
	return { distance, lat, lng, elevation: new Float64Array(n), gradePercent: new Float64Array(n), stepMeters: STEP };
}

/** Constant-pace arrivals: the whole 100 m course covered in `total` seconds. */
function arrivals(total: number): Float32Array {
	const a = new Float32Array(11);
	for (let i = 0; i < 11; i++) a[i] = (total * i) / 10;
	return a;
}

/** `lastNode` is where he stops — that IS the abandon, and arrivals[lastNode] is when. */
function field(riders: ReadonlyArray<{ total: number; lastNode?: number }>): RaceField {
	const g = course(11);
	return new RaceField(riders.map((r) => createThinSource(g, arrivals(r.total), r.lastNode)));
}

describe('computeDnfStandings', () => {
	it('is empty when everybody ranks', () => {
		const statuses: RaceFinishStatus[] = ['finished', 'finished'];

		expect(computeDnfStandings(field([{ total: 100 }, { total: 200 }]), statuses).size).toBe(0);
	});

	/**
	 * The bug this exists for. Rider 1 quits at 50 m while rider 0 is only at 25 m — he was LEADING. Left
	 * to the live ordering his row number climbs for the rest of the replay as the field rides past his
	 * parked marker, and ends up reading like a finishing place he never had.
	 */
	it('freezes the position he held at the moment he stopped, not the one he sinks to', () => {
		// Rider 0: 100 m in 400 s. Rider 1: quits at node 5 (50 m), reached at t=50 of his 100 s pace.
		// At t=50 rider 0 has covered 100·50/400 = 12.5 m, so the quitter is FIRST.
		const f = field([{ total: 400 }, { total: 100, lastNode: 5 }]);

		const standings = computeDnfStandings(f, ['finished', 'dnf']);

		expect(standings.get(1)?.position).toBe(1);
	});

	it('counts riders who are genuinely ahead at that moment', () => {
		// Two riders finish the course quickly; the third quits at 50 m long after they passed him.
		const f = field([{ total: 20 }, { total: 30 }, { total: 800, lastNode: 5 }]);

		const standings = computeDnfStandings(f, ['finished', 'finished', 'dnf']);

		expect(standings.get(2)?.position).toBe(3);
	});

	it('numbers them in the order they dropped out', () => {
		// Quit times are arrivals[lastNode]: 40·5/10 = 20, 100·5/10 = 50, 10·5/10 = 5.
		const f = field([
			{ total: 40, lastNode: 5 },
			{ total: 100, lastNode: 5 },
			{ total: 10, lastNode: 5 },
		]);

		const standings = computeDnfStandings(f, ['dnf', 'dnf', 'dnf']);

		expect(standings.get(2)?.ordinal).toBe(1);   // out first, at t=5
		expect(standings.get(0)?.ordinal).toBe(2);   // t=20
		expect(standings.get(1)?.ordinal).toBe(3);   // t=50
	});

	/** Counted per status: the first DSQ is DSQ 1, not "the 3rd rider to stop". */
	it('counts each status separately', () => {
		const f = field([
			{ total: 10, lastNode: 5 },
			{ total: 20, lastNode: 5 },
			{ total: 30, lastNode: 5 },
		]);

		const standings = computeDnfStandings(f, ['dnf', 'dsq', 'dnf']);

		expect(standings.get(0)?.ordinal).toBe(1);   // DNF 1
		expect(standings.get(1)?.ordinal).toBe(1);   // DSQ 1
		expect(standings.get(2)?.ordinal).toBe(2);   // DNF 2
	});

	it('covers every status that holds no place, not just DNF', () => {
		const f = field([{ total: 10, lastNode: 5 }, { total: 20, lastNode: 5 }, { total: 30 }]);

		const standings = computeDnfStandings(f, ['otl', 'notCompeting', 'finished']);

		expect(standings.has(0)).toBe(true);
		expect(standings.has(1)).toBe(true);
		expect(standings.has(2)).toBe(false);   // he ranks
	});

	/**
	 * Static, not captured from the frames going past — which is the whole reason it is computed this way.
	 * A viewer who seeks straight to the end must get the same numbers as one who watched it happen.
	 */
	it('gives the same answer however the field has already been advanced', () => {
		const statuses: RaceFinishStatus[] = ['finished', 'dnf'];
		const fresh = computeDnfStandings(field([{ total: 400 }, { total: 100, lastNode: 5 }]), statuses);

		const seeked = field([{ total: 400 }, { total: 100, lastNode: 5 }]);
		seeked.advanceTo(100_000);   // jump to the end first, as a seek would
		const afterSeek = computeDnfStandings(seeked, statuses);

		expect(afterSeek.get(1)).toEqual(fresh.get(1));
	});
});
