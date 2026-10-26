import { describe, it, expect, vi, afterEach } from 'vitest';
import { deleteActivity } from './activity-delete';

// CR6-06 pins: characterize the extended seam (opts.onSuccess / opts.guardBtn) before
// activity/activity-actions.ts adopts it in place of its hand-rolled duplicate.

/** A fetch mock whose single call stays pending until resolveFetch() is invoked. */
function makePendingFetch(): { fetchFn: typeof fetch; resolveFetch: () => void } {
	let resolve: (() => void) | null = null;
	const fetchFn = vi.fn(
		() =>
			new Promise<Response>((res) => {
				resolve = () => res({ ok: true, status: 204 } as Response);
			}),
	) as unknown as typeof fetch;
	return { fetchFn, resolveFetch: () => resolve?.() };
}

describe('deleteActivity', () => {
	afterEach(() => {
		vi.unstubAllGlobals();
	});

	it('declined confirm makes no fetch call', async () => {
		vi.stubGlobal('confirm', vi.fn(() => false));
		const fetchFn = vi.fn();
		vi.stubGlobal('fetch', fetchFn);

		await deleteActivity(1, 'Morning Run');

		expect(fetchFn).not.toHaveBeenCalled();
	});

	it('204 response calls onSuccess', async () => {
		vi.stubGlobal('confirm', vi.fn(() => true));
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, status: 204 } as Response)));
		const onSuccess = vi.fn();

		await deleteActivity(1, 'Morning Run', { onSuccess });

		expect(onSuccess).toHaveBeenCalledTimes(1);
	});

	it('error response alerts and re-enables the guard button', async () => {
		vi.stubGlobal('confirm', vi.fn(() => true));
		vi.stubGlobal(
			'fetch',
			vi.fn(() =>
				Promise.resolve({
					ok: false,
					status: 500,
					statusText: 'Server Error',
					json: () => Promise.resolve({}),
				} as unknown as Response),
			),
		);
		const alertFn = vi.fn();
		vi.stubGlobal('alert', alertFn);
		const btn = document.createElement('button');

		await deleteActivity(1, 'Morning Run', { guardBtn: btn });

		expect(alertFn).toHaveBeenCalledTimes(1);
		expect(btn.disabled).toBe(false);
	});

	it('guarded double-call fires only one fetch', async () => {
		vi.stubGlobal('confirm', vi.fn(() => true));
		const { fetchFn, resolveFetch } = makePendingFetch();
		vi.stubGlobal('fetch', fetchFn);
		const btn = document.createElement('button');
		const onSuccess = vi.fn();

		const first = deleteActivity(1, 'Morning Run', { guardBtn: btn, onSuccess });
		expect(btn.disabled).toBe(true); // disabled synchronously before the fetch resolves
		const second = deleteActivity(1, 'Morning Run', { guardBtn: btn, onSuccess }); // re-entry, guarded

		resolveFetch();
		await Promise.all([first, second]);

		expect(fetchFn).toHaveBeenCalledTimes(1);
	});
});
