// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { checkpointBoundsFor, roleOf, snapCheckpoint } from './race-clock';
import type { RaceCheckpointDto } from './race-api';

// The formatting cases now live in core/format.test.ts and the axis maths in core/profile-axis.test.ts —
// both moved out of race-sim with their functions. Same assertions, new homes.

const cp = (
	checkpointId: number, seq: number, name: string, chainageMeters: number,
): RaceCheckpointDto => ({
	checkpointId, seq, name, chainageMeters,
	node: Math.round(chainageMeters / 10), legName: null, legColor: null,
});

/** A race that begins 1.2 km into the ride and ends 400 m before it — a roll-out and a cooldown. */
const windowed: RaceCheckpointDto[] = [
	cp(1, 0, 'Start', 1200),
	cp(2, 1, 'Raimartihof', 21000),
	cp(3, 2, 'Finish', 38510),
];

describe('roleOf', () => {
	const flat: RaceCheckpointDto[] = [cp(1, 0, 'Start', 0), cp(2, 1, 'Mid', 21000), cp(3, 2, 'Finish', 38910)];

	it('reads the role off the chainage extremes, not the seq', () => {
		expect(roleOf(cp(3, 2, 'Finish', 38910), flat)).toBe('finish');
		expect(roleOf(cp(1, 0, 'Start', 0), flat)).toBe('start');
		expect(roleOf(cp(2, 1, 'Mid', 21000), flat)).toBe('intermediate');
	});
});

describe('checkpointBoundsFor', () => {
	// The grid is the RIDE; the race is a window on it. Both lines range over the whole ride, which is
	// exactly what a Finish seeded from the wrong metric could never be dragged back from.
	it('lets the finish reach the end of the ride', () => {
		expect(checkpointBoundsFor('finish', [1200, 21000], 38910)).toEqual({ lo: 21050, hi: 38910 });
	});

	it('lets the start sit anywhere before the first checkpoint', () => {
		expect(checkpointBoundsFor('start', [21000, 38510], 38910)).toEqual({ lo: 0, hi: 20950 });
	});

	it('holds an intermediate strictly inside the two lines', () => {
		expect(checkpointBoundsFor('intermediate', [1200, 38510], 38910)).toEqual({ lo: 1250, hi: 38460 });
	});

	it('gives the whole ride when there is nothing else on it', () => {
		expect(checkpointBoundsFor('finish', [], 38910)).toEqual({ lo: 0, hi: 38910 });
	});
});

describe('snapCheckpoint', () => {
	const intermediate = checkpointBoundsFor('intermediate', [1200, 21000, 38510], 38910);

	it('accepts a clear spot', () => {
		expect(snapCheckpoint(10000, windowed, intermediate)).toBeCloseTo(10000, 3);
	});

	it('refuses a spot on top of an existing checkpoint', () => {
		// Two checkpoints at the same chainage give the pacing model a ZERO-LENGTH interval to
		// normalize — a divide-by-zero waiting to happen.
		expect(snapCheckpoint(21010, windowed, intermediate)).toBeNull();
	});

	it('refuses the start and finish lines themselves', () => {
		expect(snapCheckpoint(1210, windowed, intermediate)).toBeNull();
		expect(snapCheckpoint(38500, windowed, intermediate)).toBeNull();
	});

	it('refuses the roll-out and the cooldown — they are ride, not race', () => {
		expect(snapCheckpoint(600, windowed, intermediate)).toBeNull();
		expect(snapCheckpoint(38700, windowed, intermediate)).toBeNull();
	});

	it('refuses a click past the end rather than wrapping it', () => {
		expect(snapCheckpoint(99999, windowed, intermediate)).toBeNull();
	});

	it('lets the finish itself be dragged into the cooldown, where an intermediate may not go', () => {
		const finish = checkpointBoundsFor('finish', [1200, 21000], 38910);
		expect(snapCheckpoint(38700, [cp(1, 0, 'Start', 1200), cp(2, 1, 'Raimartihof', 21000)], finish))
			.toBeCloseTo(38700, 3);
	});
});
