/**
 * pending-action-gate.ts — "the user clicked before the page was ready".
 *
 * The answer used to be a blocking dialog: `alert("Map and performance data must be loaded first")`,
 * `alert("No map available for replay")`, `alert("The map is still loading — try again in a moment.")`.
 * On a slow page that is worse than useless — a modal BLOCKS the page's own chunked load until it is
 * dismissed, so the impatient click delays the very thing it was waiting for, and the last of those
 * three asks the user to hand-retry what the code can do itself.
 *
 * So: remember the click. The control paints a waiting state, the action fires itself the moment the
 * precondition holds, and a second click cancels. One state machine, one place to change the timings.
 *
 * Readiness is POLLED rather than pushed on purpose. The preconditions live with different owners
 * (`core/map-state`'s map slot, a page's own "data loaded" flag, a MapLibre adapter that only exists
 * after style load) and none of them publishes an event. A 200 ms timer that exists only while a click
 * is pending is cheaper than inventing a notification seam in each of those owners.
 */
import { pollUntil } from './poll-until';

const POLL_MS = 200;
const MAX_TICKS = 900; // 3 min — past this the page is stuck, not slow

export interface PendingActionGate {
	/** True → ready, act now. False → the action was deferred (or a pending one was cancelled). */
	request: () => boolean;
	cancel: () => void;
	isPending: () => boolean;
}

export interface PendingActionGateOptions {
	isReady: () => boolean;
	/** Called once, later, when a deferred action becomes possible. */
	onReady: () => void;
	/** Paint the waiting state on the control that was clicked (spinner in, spinner out). */
	setPending: (pending: boolean) => void;
	/** The precondition never held — the deferred action is dropped and the control restored first. */
	onTimeout?: () => void;
}

export function createPendingActionGate(opts: PendingActionGateOptions): PendingActionGate {
	let pending = false;

	function clear(): void {
		if (!pending) return;
		pending = false;
		opts.setPending(false);
	}

	function request(): boolean {
		if (opts.isReady()) {
			clear();
			return true;
		}
		// A second click while spinning means "never mind" — the user's only way out.
		if (pending) {
			clear();
			return false;
		}
		pending = true;
		opts.setPending(true);
		void pollUntil(
			() => {
				if (!pending) return true; // cancelled — end the loop, not the action
				if (!opts.isReady()) return false;
				clear();
				opts.onReady();
				return true;
			},
			{
				intervalMs: POLL_MS,
				maxTicks: MAX_TICKS,
				onTimeout: () => {
					if (!pending) return;
					clear();
					opts.onTimeout?.();
				},
			},
		);
		return false;
	}

	return { request, cancel: clear, isPending: () => pending };
}
