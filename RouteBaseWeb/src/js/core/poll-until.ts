/**
 * poll-until.ts — shared "poll until terminal" loop (CR6-02).
 *
 * Generalizes segment-analysis-wait's sequential delay-loop: `await delay(intervalMs)`
 * then `await tick()`. Because the next delay is only scheduled after the current tick
 * settles, ticks can never overlap — unlike `setInterval(async …)`, where a slow
 * response lets a second tick start while the first is still in flight.
 *
 * Semantics:
 * - `tick` returning truthy ends the loop → 'done'.
 * - A throwing/rejecting tick does NOT stop the loop (network-hiccup semantics live
 *   here in the seam, not at every call site).
 * - After `maxTicks` ticks without a truthy result, `onTimeout` is called once and
 *   the loop resolves 'timeout'. Caps count ticks, not wall time: a hung fetch
 *   stalls ticking rather than piling up requests.
 * - No visibility gating — callers wanting hidden-tab behavior keep it inside `tick`.
 */

const delay = (ms: number) => new Promise<void>((r) => window.setTimeout(r, ms));

export interface PollUntilOptions {
	intervalMs: number;
	maxTicks: number;
	onTimeout?: () => void;
}

export async function pollUntil(
	tick: () => boolean | Promise<boolean>,
	opts: PollUntilOptions,
): Promise<'done' | 'timeout'> {
	for (let i = 0; i < opts.maxTicks; i++) {
		await delay(opts.intervalMs);
		try {
			if (await tick()) return 'done';
		} catch {
			// network hiccup — keep polling
		}
	}
	opts.onTimeout?.();
	return 'timeout';
}
