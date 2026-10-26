/**
 * core/latest-run.ts — "newest run wins" cancellation token.
 *
 * A thin wrapper around AbortController for the recurring pattern where each new
 * run (a zoom-triggered quality upgrade, a filter change, …) must supersede the
 * previous one: starting a fresh run aborts whatever was still in flight.
 *
 * Usage:
 *   const run = new LatestRun();
 *   const signal = run.begin();          // aborts the prior run, returns a fresh signal
 *   await loadInChunks({ …, signal });   // bail in onChunk via `if (signal.aborted) return`
 *   run.cancel();                        // abort + forget (e.g. on reset/teardown)
 *
 * Extracted so the Activities overview and Segment maps share one implementation
 * instead of each inlining the abort-previous / new-controller dance.
 */
export class LatestRun {
	private ctrl: AbortController | null = null;

	/** Abort the previous run (if any) and hand out a fresh signal for the new one. */
	begin(): AbortSignal {
		this.ctrl?.abort();
		this.ctrl = new AbortController();
		return this.ctrl.signal;
	}

	/** Abort the current run and forget it — no signal handed out. */
	cancel(): void {
		this.ctrl?.abort();
		this.ctrl = null;
	}
}
