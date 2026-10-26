/**
 * A rate cap for work that runs on the frame loop but does not need to.
 *
 * Two callers, one bug to not have twice. The marker flush was already capped at 30 Hz because
 * `setData` — not the position maths — is what costs (race-markers.ts); the leaderboard, which measured
 * at 97% of /Races/Replay's frame cost, had no cap at all.
 */

export interface TickThrottle {
	/** True at most once per interval. `force` always returns true and re-arms the clock. */
	shouldRun(force?: boolean): boolean;
	/** Re-arm without running — for a caller that is about to do the work regardless. */
	arm(): void;
}

export function createTickThrottle(minIntervalMs: number, now: () => number = () => performance.now()): TickThrottle {
	let last = -Infinity;

	return {
		shouldRun(force = false): boolean {
			const t = now();
			// The epsilon is load-bearing. A 60 fps caller against a 30 Hz cap lands EXACTLY on the
			// interval every other frame, and float error there drops that tick — giving an uneven cadence
			// (~23 Hz, stepping irregularly) rather than a steady 30. Uneven is worse than slow.
			if (!force && t - last < minIntervalMs - 1e-6) return false;
			last = t;
			return true;
		},
		arm(): void {
			last = now();
		},
	};
}
