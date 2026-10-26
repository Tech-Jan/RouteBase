// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { buildSpeedLadder, roundSpeed } from './speed-ladder';

describe('roundSpeed', () => {
	it.each([
		[0.3, 0.25],    // quarter steps below 1x
		[3.4, 3],       // whole numbers below 10x
		[74, 75],       // fives below 100x
		[117, 120],     // tens above — nobody wants 117x
	])('roundSpeed(%f) = %f', (input, expected) => {
		expect(roundSpeed(input)).toBe(expected);
	});
});

describe('buildSpeedLadder', () => {
	it('scales to the replay: the default watches the whole thing in about the target time', () => {
		// A fixed ladder cannot serve both a 2-minute segment effort and a 2.5-hour race.
		const race = buildSpeedLadder(8939);       // the Black Forest ride
		const effort = buildSpeedLadder(120);      // a short segment effort

		expect(8939 / race.defaultSpeed).toBeGreaterThan(60);
		expect(8939 / race.defaultSpeed).toBeLessThan(240);
		expect(race.defaultSpeed).toBeGreaterThan(effort.defaultSpeed);
	});

	it('always offers real time and a gentle 4x, however long the replay is', () => {
		// However fast the ladder is scaled, you must still be able to watch it as it happened.
		const ladder = buildSpeedLadder(40000);

		expect(ladder.speeds).toContain(1);
		expect(ladder.speeds).toContain(4);
	});

	it('is ascending and free of duplicates', () => {
		const ladder = buildSpeedLadder(8939);

		expect(ladder.speeds).toEqual([...ladder.speeds].sort((a, b) => a - b));
		expect(new Set(ladder.speeds).size).toBe(ladder.speeds.length);
	});

	it('the default is ON the ladder', () => {
		// It is rounded, so an unrounded base would select an option that does not exist and leave the
		// stepper's label out of step with the speed actually running.
		for (const seconds of [60, 600, 8939, 43200]) {
			const ladder = buildSpeedLadder(seconds);
			expect(ladder.speeds).toContain(ladder.defaultSpeed);
		}
	});

	it('never offers a zero or negative speed', () => {
		const ladder = buildSpeedLadder(30);   // very short: base clamps to 1
		expect(ladder.speeds.every((s) => s > 0)).toBe(true);
	});

	// Migrated from map-utils-shared.test.ts when the activity-only ladder (roundReplaySpeed /
	// buildReplaySpeedOptions) was retired: the ladder that now feeds ALL three replays must still round
	// every step to a human-nice number, so the speed label can never read a value the ladder would not
	// offer (the divergence that let the activity clock read "2.5x").
	it('every step is already rounded — no raw multiplier survives onto the ladder', () => {
		for (const seconds of [120, 3600, 8939, 43200]) {
			const ladder = buildSpeedLadder(seconds);
			for (const s of ladder.speeds) expect(roundSpeed(s)).toBe(s);
		}
	});
});
