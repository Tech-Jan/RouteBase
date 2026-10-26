import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { waitForAnalysis } from './segment-analysis-wait';

// Queue up the sequence of /api/queue/progress responses; each poll shifts one.
// A `null` entry means the fetch rejects (network error path).
function stubProgress(responses: Array<Record<string, unknown> | null>): void {
	let i = 0;
	const fetchMock = vi.fn(async () => {
		const r = i < responses.length ? responses[i] : responses[responses.length - 1];
		i++;
		if (r === null) throw new Error('network');
		return { ok: true, json: async () => r } as Response;
	});
	vi.stubGlobal('fetch', fetchMock);
}

describe('waitForAnalysis (CB-06 classification pins)', () => {
	beforeEach(() => vi.useFakeTimers());
	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
	});

	it('ignores non-analysis operations and resolves only on the analysis status', async () => {
		stubProgress([
			{ operationType: 'import', isRunning: true, phase: 'Running' },
			{ operationType: 'analysis', isRunning: false, phase: 'Completed' },
		]);
		const p = waitForAnalysis();
		await vi.advanceTimersByTimeAsync(2000);
		await expect(p).resolves.toBe('completed');
	});

	it('returns failed on phase Failed', async () => {
		stubProgress([
			{ operationType: 'analysis', isRunning: true, phase: 'Running' },
			{ operationType: 'analysis', isRunning: false, phase: 'Failed' },
		]);
		const p = waitForAnalysis();
		await vi.advanceTimersByTimeAsync(2000);
		await expect(p).resolves.toBe('failed');
	});

	it('treats not-running-after-seen-running as completed', async () => {
		stubProgress([
			{ operationType: 'analysis', isRunning: true, phase: 'Running' },
			{ operationType: 'analysis', isRunning: false, phase: 'Idle' },
		]);
		const p = waitForAnalysis();
		await vi.advanceTimersByTimeAsync(2000);
		await expect(p).resolves.toBe('completed');
	});

	it('keeps waiting when the analysis has not been picked up yet', async () => {
		// isRunning:false, phase Idle, never seen running -> worker not started, keep polling.
		stubProgress([{ operationType: 'analysis', isRunning: false, phase: 'Idle' }]);
		const p = waitForAnalysis();
		let settled = false;
		void p.then(() => (settled = true));

		await vi.advanceTimersByTimeAsync(5000);
		expect(settled).toBe(false);
		expect(fetch).toHaveBeenCalledTimes(5);

		// Drain to the safety cap so the pending promise resolves before teardown.
		await vi.advanceTimersByTimeAsync(180 * 1000);
		await expect(p).resolves.toBe('completed');
	});

	it('resolves completed at the poll cap when every fetch fails', async () => {
		stubProgress([null]); // always rejects
		const p = waitForAnalysis();
		await vi.advanceTimersByTimeAsync(180 * 1000);
		await expect(p).resolves.toBe('completed');
		expect(fetch).toHaveBeenCalledTimes(180); // MAX_POLLS budget consumed by errors
	});
});
