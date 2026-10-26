/**
 * trailing-throttle.ts — run at most once per interval, but never swallow the LAST call.
 *
 * `createTickThrottle` alone is a leading-edge gate: it answers "may I run now?" and drops everything
 * else. That is right for a loop that keeps ticking (the next frame corrects the display), and wrong for
 * a stream that can stop at any moment — a seek while paused delivers exactly one update, and if the gate
 * drops it, nothing later comes to fix the stale UI.
 *
 * This wraps the gate with a trailing timer: a dropped call is re-run once the interval is up. Pure —
 * clock and timers are injected, so the behaviour is testable without a DOM or a real wait.
 */

import { createTickThrottle } from './tick-throttle';

export interface TrailingThrottle {
	/** Run now if the interval has elapsed (or `force`); otherwise schedule the trailing run. */
	request(force?: boolean): void;
	/** Drop a pending trailing run — call from teardown. */
	cancel(): void;
}

export interface TrailingThrottleTimers {
	now: () => number;
	setTimeout: (fn: () => void, ms: number) => number;
	clearTimeout: (id: number) => void;
}

const browserTimers: TrailingThrottleTimers = {
	now: () => performance.now(),
	setTimeout: (fn, ms) => window.setTimeout(fn, ms),
	clearTimeout: (id) => window.clearTimeout(id),
};

export function createTrailingThrottle(
	intervalMs: number,
	run: () => void,
	timers: TrailingThrottleTimers = browserTimers,
): TrailingThrottle {
	const gate = createTickThrottle(intervalMs, timers.now);
	let trailingId: number | null = null;

	function clearTrailing(): void {
		if (trailingId === null) return;
		timers.clearTimeout(trailingId);
		trailingId = null;
	}

	return {
		request(force = false): void {
			if (gate.shouldRun(force)) {
				// This run supersedes anything queued — otherwise a burst leaves a redundant trailing run
				// that fires one interval later against unchanged state.
				clearTrailing();
				run();
				return;
			}
			if (trailingId !== null) return; // one trailing run is enough; it reads the latest state
			trailingId = timers.setTimeout(() => {
				trailingId = null;
				gate.arm(); // the trailing run counts as this interval's run
				run();
			}, intervalMs);
		},

		cancel: clearTrailing,
	};
}
