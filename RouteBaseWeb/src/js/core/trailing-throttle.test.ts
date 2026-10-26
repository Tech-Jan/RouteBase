// @vitest-environment node
import { describe, it, expect, vi } from 'vitest';
import { createTrailingThrottle, type TrailingThrottleTimers } from './trailing-throttle';

// PERF-2's contract. The cap alone is easy; the part worth pinning is that capping never SWALLOWS the
// last update — a seek while paused delivers one progress callback, and a leading-edge-only gate would
// drop it with no later frame to correct the stale labels.

/** Controllable clock + timer queue: no DOM, no waiting. */
function fakeTimers(): TrailingThrottleTimers & { tick(ms: number): void; pending(): number } {
	let now = 0;
	let nextId = 1;
	const queue = new Map<number, { at: number; fn: () => void }>();

	return {
		now: () => now,
		setTimeout: (fn, ms) => { const id = nextId++; queue.set(id, { at: now + ms, fn }); return id; },
		clearTimeout: (id) => { queue.delete(id); },
		pending: () => queue.size,
		tick(ms: number): void {
			now += ms;
			for (const [id, t] of [...queue.entries()]) {
				if (t.at > now) continue;
				queue.delete(id);
				t.fn();
			}
		},
	};
}

describe('createTrailingThrottle', () => {
	it('runs the first call at once and caps the burst that follows', () => {
		const timers = fakeTimers();
		const run = vi.fn();
		const throttle = createTrailingThrottle(200, run, timers);

		throttle.request();
		expect(run).toHaveBeenCalledTimes(1);

		// A 60 fps caller inside one interval: 11 more requests, none of them a second run.
		for (let i = 0; i < 11; i++) { timers.tick(16); throttle.request(); }
		expect(run).toHaveBeenCalledTimes(1);
	});

	it('the last dropped call still lands — even if nothing calls again', () => {
		const timers = fakeTimers();
		const run = vi.fn();
		const throttle = createTrailingThrottle(200, run, timers);

		throttle.request();          // leading run
		timers.tick(10);
		throttle.request();          // dropped… but it is the last thing that will ever happen
		expect(run).toHaveBeenCalledTimes(1);

		timers.tick(200);
		expect(run).toHaveBeenCalledTimes(2); // the trailing run corrected the stale state
	});

	it('a burst queues ONE trailing run, not one per dropped call', () => {
		const timers = fakeTimers();
		const run = vi.fn();
		const throttle = createTrailingThrottle(200, run, timers);

		throttle.request();
		for (let i = 0; i < 5; i++) throttle.request();
		expect(timers.pending()).toBe(1);

		timers.tick(200);
		expect(run).toHaveBeenCalledTimes(2);
		expect(timers.pending()).toBe(0);
	});

	it('force runs immediately and drops the queued trailing run', () => {
		const timers = fakeTimers();
		const run = vi.fn();
		const throttle = createTrailingThrottle(200, run, timers);

		throttle.request();
		throttle.request();            // queues the trailing run
		expect(timers.pending()).toBe(1);

		throttle.request(true);        // a seek/mode switch: now, not in 200 ms
		expect(run).toHaveBeenCalledTimes(2);
		expect(timers.pending()).toBe(0); // …and no redundant run one interval later

		timers.tick(500);
		expect(run).toHaveBeenCalledTimes(2);
	});

	it('cancel() drops a pending trailing run (teardown must not fire into a dead DOM)', () => {
		const timers = fakeTimers();
		const run = vi.fn();
		const throttle = createTrailingThrottle(200, run, timers);

		throttle.request();
		throttle.request();
		throttle.cancel();

		timers.tick(500);
		expect(run).toHaveBeenCalledTimes(1);
	});

	it('a steady caller settles into one run per interval, not two', () => {
		const timers = fakeTimers();
		const run = vi.fn();
		const throttle = createTrailingThrottle(200, run, timers);

		// 60 fps for a second: 5 Hz means ~5 runs, and the trailing edge must not double them up.
		for (let i = 0; i < 60; i++) { throttle.request(); timers.tick(16); }
		expect(run.mock.calls.length).toBeLessThanOrEqual(6);
		expect(run.mock.calls.length).toBeGreaterThanOrEqual(5);
	});
});
