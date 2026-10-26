import { describe, it, expect, vi, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { createDeleteController } from './activities-delete-controller';

// GP-01: toggleRemoval must ignore marks while a delete commit (apply()) is in flight. Otherwise a
// click after the commit's id snapshot could still slip into the deletion, with no confirm() ever
// mentioning it (analysis-grids-page.md GP-01). apply() now sends ONE batch POST /api/activities/bulk/delete
// with the ids marked at commit time (not N single DELETEs).

function mount(): { btn: HTMLButtonElement; ctl: ReturnType<typeof createDeleteController> } {
	const toolbar = document.createElement('div');
	const ctl = createDeleteController();
	ctl.mountButton(toolbar);
	const btn = toolbar.querySelector('.act-delete-btn') as HTMLButtonElement;
	return { btn, ctl };
}

// First fetch() call stays pending until resolveFirst() is called (simulates apply() suspended
// mid-loop on its first await); every later call resolves immediately.
function makeFetch(): {
	fetchFn: typeof fetch;
	calls: string[];
	inits: (RequestInit | undefined)[];
	resolveFirst: () => void;
} {
	const calls: string[] = [];
	const inits: (RequestInit | undefined)[] = [];
	let firstResolve: (() => void) | null = null;
	let firstPending = true;
	const fetchFn = vi.fn((url: string, init?: RequestInit) => {
		calls.push(url);
		inits.push(init);
		if (firstPending) {
			firstPending = false;
			return new Promise<Response>((resolve) => {
				firstResolve = () => resolve({ ok: true, status: 200 } as Response);
			});
		}
		return Promise.resolve({ ok: true, status: 200 } as Response);
	}) as unknown as typeof fetch;
	return { fetchFn, calls, inits, resolveFirst: () => firstResolve?.() };
}

describe('activities-delete-controller', () => {
	afterEach(() => {
		vi.unstubAllGlobals();
	});

	it('toggleRemoval marks and unmarks before any commit starts', () => {
		const { ctl } = mount();
		expect(ctl.toggleRemoval(1)).toBe(true);
		expect(ctl.isMarked(1)).toBe(true);
		expect(ctl.toggleRemoval(1)).toBe(false);
		expect(ctl.isMarked(1)).toBe(false);
	});

	it('toggleRemoval is a no-op while a delete commit is in flight', async () => {
		const { btn, ctl } = mount();
		ctl.toggleRemoval(1);
		ctl.toggleRemoval(2);
		vi.stubGlobal('confirm', vi.fn(() => true));
		vi.stubGlobal('location', { reload: vi.fn() } as unknown as Location);
		const { fetchFn } = makeFetch();
		vi.stubGlobal('fetch', fetchFn);

		btn.click(); // fires apply(); the first fetch() never resolves, so it stays suspended mid-loop
		await vi.waitFor(() => expect(fetchFn).toHaveBeenCalled());

		expect(ctl.toggleRemoval(3)).toBe(false);
		expect(ctl.isMarked(3)).toBe(false);
	});

	it('a failing batch after committed batches still reloads and surfaces the server error', async () => {
		// Batches before the failure are already trashed server-side; skipping the reload left the grid
		// showing rows that no longer exist (re-markable into a second delete), and the alert never said why.
		const { btn, ctl } = mount();
		for (let i = 1; i <= 10001; i++) ctl.toggleRemoval(i); // 2 batches at MAX_BULK_IDS=10000
		vi.stubGlobal('confirm', vi.fn(() => true));
		vi.stubGlobal('location', { reload: vi.fn() } as unknown as Location);
		const alertFn = vi.fn();
		vi.stubGlobal('alert', alertFn);
		const fetchFn = vi
			.fn()
			.mockResolvedValueOnce({ ok: true, status: 200 } as Response)
			.mockResolvedValueOnce({
				ok: false, status: 500, json: () => Promise.resolve({ message: 'db locked' }),
			} as unknown as Response);
		vi.stubGlobal('fetch', fetchFn);

		btn.click();
		await vi.waitFor(() => expect(alertFn).toHaveBeenCalled());

		expect(fetchFn).toHaveBeenCalledTimes(2);
		const alertMsg = must(alertFn.mock.calls[0], 'one alert call')[0];
		expect(alertMsg).toContain('10000');     // says how many are already gone
		expect(alertMsg).toContain('db locked'); // and why the rest failed
		expect(location.reload).toHaveBeenCalled();              // grid must not keep showing trashed rows
	});

	it('apply() sends one batch of the ids marked at commit time, not ids marked afterward', async () => {
		const { btn, ctl } = mount();
		ctl.toggleRemoval(1);
		ctl.toggleRemoval(2);
		vi.stubGlobal('confirm', vi.fn(() => true));
		vi.stubGlobal('location', { reload: vi.fn() } as unknown as Location);
		const { fetchFn, calls, inits, resolveFirst } = makeFetch();
		vi.stubGlobal('fetch', fetchFn);

		btn.click();
		await vi.waitFor(() => expect(fetchFn).toHaveBeenCalled());
		ctl.toggleRemoval(3); // ignored — commit already in flight

		resolveFirst();
		await vi.waitFor(() => expect(location.reload).toHaveBeenCalled());

		// One batch request (not N single DELETEs); its body carries exactly the ids marked at commit time.
		expect(fetchFn).toHaveBeenCalledTimes(1);
		expect(calls).toEqual(['/api/activities/bulk/delete']);
		const body = JSON.parse(inits[0]!.body as string) as { trackIds: number[] };
		expect(body.trackIds.slice().sort()).toEqual([1, 2]);
		expect(body.trackIds).not.toContain(3);
	});
});
