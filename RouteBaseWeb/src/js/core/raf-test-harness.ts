/**
 * raf-test-harness.ts — a requestAnimationFrame the tests drive by hand.
 *
 * Test-only (nothing in the app imports it). It exists because the usual jsdom stub,
 * `vi.stubGlobal('requestAnimationFrame', () => 1)`, silently SWALLOWS every frame callback: any code
 * that coalesces work into a frame then does nothing at all, and a suite asserting synchronously still
 * passes while testing nothing. That is exactly the trap PERF-1's scrub coalescing would have fallen
 * into.
 *
 * With this, a test queues real work and decides when frames happen — so it can assert the thing that
 * matters about coalescing: N events, one flush, ONE render.
 */

type FrameCallback = (time: number) => void;

export interface FakeRaf {
	/** Run the callbacks queued so far. Callbacks queued DURING a flush belong to the next frame (that is
	 *  what stops a self-rescheduling playback loop from spinning forever here). */
	flushFrames(count?: number): void;
	/** How many callbacks are waiting — `0` after a flush unless something re-queued. */
	pending(): number;
	restore(): void;
}

/** Swap in the fake. Call `restore()` in afterEach (or use `vi.unstubAllGlobals` only for OTHER stubs —
 *  this one restores the real functions itself, so it does not depend on vitest). */
export function installFakeRaf(startTimeMs = 0, frameMs = 16): FakeRaf {
	const realRequest = globalThis.requestAnimationFrame;
	const realCancel = globalThis.cancelAnimationFrame;

	let nextId = 1;
	let now = startTimeMs;
	const queue = new Map<number, FrameCallback>();

	globalThis.requestAnimationFrame = ((cb: FrameCallback): number => {
		const id = nextId++;
		queue.set(id, cb);
		return id;
	}) as typeof requestAnimationFrame;

	globalThis.cancelAnimationFrame = ((id: number): void => {
		queue.delete(id);
	}) as typeof cancelAnimationFrame;

	return {
		pending: () => queue.size,

		flushFrames(count = 1): void {
			for (let i = 0; i < count; i++) {
				const due = [...queue.values()];
				queue.clear();
				now += frameMs;
				for (const cb of due) cb(now);
			}
		},

		restore(): void {
			globalThis.requestAnimationFrame = realRequest;
			globalThis.cancelAnimationFrame = realCancel;
		},
	};
}
