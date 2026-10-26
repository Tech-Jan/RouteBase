import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { pollUntil } from './poll-until';

// CR6-02 pins: these four behaviors are what the migrated poll sites
// (import.ts ×2, keys-and-data.ts ×2, readiness polls) rely on.

describe('pollUntil', () => {
	beforeEach(() => {
		vi.useFakeTimers();
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it('done stops scheduling: a truthy tick resolves and no further ticks run', async () => {
		const tick = vi.fn(() => true);
		const onTimeout = vi.fn();
		const p = pollUntil(tick, { intervalMs: 1000, maxTicks: 5, onTimeout });

		await vi.advanceTimersByTimeAsync(1000);
		expect(await p).toBe('done');

		await vi.advanceTimersByTimeAsync(10_000);
		expect(tick).toHaveBeenCalledTimes(1);
		expect(onTimeout).not.toHaveBeenCalled();
	});

	it('timeout fires onTimeout exactly once after maxTicks', async () => {
		const tick = vi.fn().mockResolvedValue(false);
		const onTimeout = vi.fn();
		const p = pollUntil(tick, { intervalMs: 1000, maxTicks: 3, onTimeout });

		await vi.advanceTimersByTimeAsync(3000);
		expect(await p).toBe('timeout');
		expect(tick).toHaveBeenCalledTimes(3);
		expect(onTimeout).toHaveBeenCalledTimes(1);

		await vi.advanceTimersByTimeAsync(10_000);
		expect(onTimeout).toHaveBeenCalledTimes(1);
	});

	it('a rejecting tick does not stop the loop', async () => {
		const tick = vi.fn()
			.mockRejectedValueOnce(new Error('network hiccup'))
			.mockResolvedValueOnce(true);
		const p = pollUntil(tick, { intervalMs: 1000, maxTicks: 5 });

		await vi.advanceTimersByTimeAsync(2000);
		expect(await p).toBe('done');
		expect(tick).toHaveBeenCalledTimes(2);
	});

	it('ticks never overlap: a pending async tick blocks further scheduling', async () => {
		let release!: () => void;
		const gate = new Promise<boolean>((r) => { release = () => r(false); });
		const tick = vi.fn(() => gate);
		const p = pollUntil(tick, { intervalMs: 1000, maxTicks: 3 });

		// First tick starts at t=1000 and stays pending; with setInterval(async …)
		// four more ticks would have fired by t=5000.
		await vi.advanceTimersByTimeAsync(5000);
		expect(tick).toHaveBeenCalledTimes(1);

		release();
		await vi.advanceTimersByTimeAsync(0); // flush the resumed loop's microtasks
		await vi.advanceTimersByTimeAsync(2000);
		expect(await p).toBe('timeout');
		expect(tick).toHaveBeenCalledTimes(3);
	});
});
