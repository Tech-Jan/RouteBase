// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { createTickThrottle } from './tick-throttle';

/**
 * PR-01's pin. The leaderboard was rebuilt on every rAF tick — 0.479 ms of a 0.494 ms frame (97%) and
 * 17.7 KB of garbage per frame. The cap is what removes that; these are its rules.
 */
describe('createTickThrottle', () => {
	/** A 60 fps caller for `seconds`, against a `hz` cap. */
	function run(hz: number, seconds: number): number {
		let clock = 0;
		const throttle = createTickThrottle(1000 / hz, () => clock);
		let ran = 0;

		for (let frame = 0; frame < 60 * seconds; frame++) {
			if (throttle.shouldRun()) ran++;
			clock += 1000 / 60;
		}
		return ran;
	}

	it('throttles to the configured rate', () => {
		// 60 calls across a simulated second at 5 Hz.
		expect(run(5, 1)).toBe(5);
	});

	it('force always invokes, and re-arms the clock', () => {
		let clock = 0;
		const throttle = createTickThrottle(200, () => clock);

		expect(throttle.shouldRun()).toBe(true);      // first call always runs
		clock += 10;
		expect(throttle.shouldRun()).toBe(false);     // inside the interval
		expect(throttle.shouldRun(true)).toBe(true);  // ...unless forced

		clock += 10;
		// The forced call re-armed: we are 10 ms past it, not 20 ms past the last throttled run.
		expect(throttle.shouldRun()).toBe(false);
		clock += 190;
		expect(throttle.shouldRun()).toBe(true);
	});

	it('the epsilon keeps the cadence EVEN, not merely slow', () => {
		// The bug race-markers.ts documents: a 60 fps caller against a 30 Hz cap lands EXACTLY on the
		// interval every other frame. Without the `- 1e-6` the float compare drops that tick and the
		// cadence degrades to a ragged ~23 Hz. A steady 30 is the requirement — uneven is worse than slow.
		expect(run(30, 1)).toBe(30);

		// And it is genuinely every-other-frame, not bunched.
		let clock = 0;
		const throttle = createTickThrottle(1000 / 30, () => clock);
		const pattern: boolean[] = [];
		for (let frame = 0; frame < 8; frame++) {
			pattern.push(throttle.shouldRun());
			clock += 1000 / 60;
		}
		expect(pattern).toEqual([true, false, true, false, true, false, true, false]);
	});

	it('arm() re-arms without running — the caller is doing the work anyway', () => {
		let clock = 0;
		const throttle = createTickThrottle(200, () => clock);

		throttle.arm();
		clock += 100;
		expect(throttle.shouldRun()).toBe(false);
		clock += 100;
		expect(throttle.shouldRun()).toBe(true);
	});
});
