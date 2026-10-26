/**
 * replay-history-session.ts — shared replay history session (CR1-05)
 *
 * Single source for the dummy-history-entry dance both replay UIs previously
 * hand-rolled identically: pushState on start, popstate-clears-flag-then-exits,
 * guarded history.back() on exit, and the document-level Escape exit.
 *
 * `enter()` is idempotent per session — a second call before `leave()` is a
 * no-op, so a regressed re-entry guard can no longer create the RP-01/RS2-05
 * duplicate-history-entry damage (structural backstop for matrix #8).
 */

export interface ReplayHistorySession {
	/** Push the dummy entry. Idempotent per session — a second call before leave() is a no-op. */
	enter(): void;
	/** Exit-side half: clears the entered flag; history.back() only while history.state[stateKey] is ours. */
	leave(): void;
	/**
	 * Remove the global popstate + keydown listeners this session installed. A session recreated per
	 * overlay (race-replay-overlay.ts) MUST call this from its own destroy() — otherwise every recreate
	 * stacks another live Escape/Back handler on the document/window (the leak race-replay-overlay's
	 * destroy() closes for its eleven OTHER listeners but not, until now, these two).
	 */
	destroy(): void;
}

export function createReplayHistorySession(opts: {
	/** Grep-able history-state flag ("replayActive" | "segReplayActive") — literals stay at the call sites. */
	stateKey: string;
	/** Is a replay session live right now? Gates the popstate/Escape exits. */
	isActive(): boolean;
	/** The chain's full exit path; MUST end with session.leave(). */
	exit(): void;
}): ReplayHistorySession {
	let _entered = false;

	// Back button: exit the replay instead of navigating away. The browser already
	// moved back, so clear the flag first — leave()'s guarded history.back() then skips.
	// Named (not anonymous) so destroy() can remove them.
	const onPopState = (_e: PopStateEvent): void => {
		if (opts.isActive()) {
			if (history.state) history.state[opts.stateKey] = false;
			opts.exit();
		}
	};
	window.addEventListener("popstate", onPopState);

	// Esc closes the replay (mirrors the exit button).
	const onKeyDown = (e: KeyboardEvent): void => {
		if (e.key === "Escape" && opts.isActive()) opts.exit();
	};
	document.addEventListener("keydown", onKeyDown);

	return {
		enter: (): void => {
			if (_entered) return;
			_entered = true;
			history.pushState({ [opts.stateKey]: true }, "");
		},
		leave: (): void => {
			_entered = false;
			// Remove the dummy entry only when the state is still ours — after a
			// back-button exit we are already back, and history.back() would navigate away.
			if (history.state && history.state[opts.stateKey]) {
				history.back();
			}
		},
		destroy: (): void => {
			window.removeEventListener("popstate", onPopState);
			document.removeEventListener("keydown", onKeyDown);
		},
	};
}
