// @vitest-environment node
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createPendingActionGate } from './pending-action-gate';

// The gate polls on window.setTimeout (via core/poll-until) — node env, so provide the one global it uses.
beforeEach(() => {
	vi.useFakeTimers();
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(globalThis as any).window = globalThis;
});

afterEach(() => {
	vi.useRealTimers();
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	delete (globalThis as any).window;
});

function makeGate(ready: { value: boolean }) {
	const onReady = vi.fn();
	const setPending = vi.fn();
	const onTimeout = vi.fn();
	const gate = createPendingActionGate({
		isReady: () => ready.value,
		onReady,
		setPending,
		onTimeout,
	});
	return { gate, onReady, setPending, onTimeout };
}

describe('pending-action-gate', () => {
	it('starts immediately when the data is already loaded — no pending state', () => {
		const ready = { value: true };
		const { gate, onReady, setPending } = makeGate(ready);

		expect(gate.request()).toBe(true);
		expect(gate.isPending()).toBe(false);
		expect(setPending).not.toHaveBeenCalled();
		expect(onReady).not.toHaveBeenCalled(); // the caller starts it, not the callback
	});

	it('defers the start while loading and fires it once the data lands', async () => {
		const ready = { value: false };
		const { gate, onReady, setPending } = makeGate(ready);

		expect(gate.request()).toBe(false);
		expect(gate.isPending()).toBe(true);
		expect(setPending).toHaveBeenCalledWith(true);

		await vi.advanceTimersByTimeAsync(1000);
		expect(onReady).not.toHaveBeenCalled(); // still loading — nothing started, nothing alerted

		ready.value = true;
		await vi.advanceTimersByTimeAsync(300);

		expect(onReady).toHaveBeenCalledTimes(1);
		expect(gate.isPending()).toBe(false);
		expect(setPending).toHaveBeenLastCalledWith(false);
	});

	it('a second click while waiting cancels — the deferred start never fires', async () => {
		const ready = { value: false };
		const { gate, onReady, setPending } = makeGate(ready);

		gate.request();
		expect(gate.request()).toBe(false); // second click
		expect(gate.isPending()).toBe(false);
		expect(setPending).toHaveBeenLastCalledWith(false);

		ready.value = true;
		await vi.advanceTimersByTimeAsync(2000);
		expect(onReady).not.toHaveBeenCalled();
	});

	it('cancel() drops a pending start (replay exited / page moved on)', async () => {
		const ready = { value: false };
		const { gate, onReady } = makeGate(ready);

		gate.request();
		gate.cancel();
		ready.value = true;
		await vi.advanceTimersByTimeAsync(2000);

		expect(onReady).not.toHaveBeenCalled();
	});

	it('gives up after the poll cap and restores the button', async () => {
		const ready = { value: false };
		const { gate, onReady, setPending, onTimeout } = makeGate(ready);

		gate.request();
		await vi.advanceTimersByTimeAsync(200 * 900 + 200);

		expect(onTimeout).toHaveBeenCalledTimes(1);
		expect(onReady).not.toHaveBeenCalled();
		expect(gate.isPending()).toBe(false);
		expect(setPending).toHaveBeenLastCalledWith(false);
	});
});
