// live-elapsed.ts — pure "active riding time" accumulator shared by both live-recording views (the
// public /s/{token} share and the authenticated LiveView panel), so the two never disagree (FE-08/FE-12).
//
// The elapsed clock reflects ACTIVE riding time: it advances while recording, FREEZES while paused, and
// holds a final value once the ride ends. Callers feed the latest observed (status, wall-clock ms) on
// every render; it returns whole active seconds to display. No DOM, timers or network — pure state, so it
// is unit-testable and identical for both views.
//
// Limitation: a viewer who opens the link AFTER the ride started cannot know about pauses from before they
// tuned in — that pre-observation span counts as active. Excluding it would need a server-supplied
// active-duration field (see REPORT). Pauses observed while watching are excluded exactly.

import { ENDED_STATUSES } from './live-recording-shared';

export interface ActiveElapsed {
	/**
	 * Feed the latest observed status + wall-clock ms; returns whole active SECONDS so far. Unchanged
	 * (frozen) while paused, and permanently frozen once an ended status is seen. Pass the real stop time
	 * as `nowMs` on the terminal sample to freeze at the true final duration.
	 */
	sample(status: string, nowMs: number): number;
}

export function createActiveElapsed(startedAtMs: number): ActiveElapsed {
	let accumMs = 0;                             // active ms from segments already closed
	let segStartMs: number | null = startedAtMs; // wall start of the open active segment; null while frozen
	let ended = false;

	return {
		sample(status: string, nowMs: number): number {
			if (!ended) {
				if (status === 'recording') {
					segStartMs ??= nowMs; // a resume opens a fresh segment at nowMs — the paused gap is excluded
				} else {
					// paused or ended: close the open segment, then hold
					if (segStartMs !== null) { accumMs += nowMs - segStartMs; segStartMs = null; }
					if (ENDED_STATUSES.includes(status)) ended = true;
				}
			}
			const liveMs = segStartMs !== null ? nowMs - segStartMs : 0;
			return Math.max(0, Math.floor((accumMs + liveMs) / 1000));
		},
	};
}
