import { describe, it, expect, vi, afterEach } from 'vitest';
import { createExportController } from './activities-export-controller';

/**
 * "Export GPX (N)" — two contracts.
 *
 * The LABEL counts the rides the user owns, not the table's match count: the ZIP can only ever contain
 * their own, so a button reading the plain match count would promise rides that cannot be in it.
 *
 * The CLICK only enqueues, and it must send the id set the table last reported — a stale set would build
 * a ZIP that silently disagrees with what the user is looking at. Progress and the download belong to the
 * global op-toast from there, which is what lets the user leave the page.
 */

const BUTTON_ID = 'actExportGpxBtn';

const startExportPolling = vi.fn();
vi.mock('../core/op-polling-bridge', () => ({
	startExportPolling: () => startExportPolling(),
}));

function mountButton(): HTMLButtonElement {
	document.body.innerHTML =
		`<button type="button" id="${BUTTON_ID}"><i class="bi bi-file-earmark-zip"></i> <span class="act-export-label">Export GPX</span></button>`;
	return document.getElementById(BUTTON_ID) as HTMLButtonElement;
}

function labelOf(btn: HTMLButtonElement): string {
	return btn.querySelector('.act-export-label')?.textContent ?? '';
}

function makeFetch(ok = true): { fetchFn: typeof fetch; bodies: unknown[] } {
	const bodies: unknown[] = [];
	const fetchFn = vi.fn((_url: string, init?: RequestInit) => {
		bodies.push(JSON.parse(String(init?.body)));
		return Promise.resolve({
			ok,
			status: ok ? 202 : 409,
			json: () => Promise.resolve(ok
				? { jobId: 1, downloadUrl: '/api/activities/export/download/1', requestedCount: 1 }
				: { code: 'conflict', message: 'An export is already running.' }),
		} as unknown as Response);
	}) as unknown as typeof fetch;
	return { fetchFn, bodies };
}

describe('activities-export-controller', () => {
	afterEach(() => {
		vi.unstubAllGlobals();
		vi.clearAllMocks();
		document.body.innerHTML = '';
	});

	it('labels the button with the OWNED count, not the filtered count', () => {
		const btn = mountButton();
		const ctl = createExportController(BUTTON_ID);

		// 40 rides match the filter but only 22 are the user's — 22 is what an export can contain.
		ctl.setFiltered(Array.from({ length: 40 }, (_, i) => i + 1), 22);
		expect(labelOf(btn)).toBe('Export GPX (22)');
		expect(btn.disabled).toBe(false);
	});

	it('is disabled when the user owns none of the matches', () => {
		const btn = mountButton();
		const ctl = createExportController(BUTTON_ID);

		ctl.setFiltered([7, 8, 9], 0);
		expect(labelOf(btn)).toBe('Export GPX (0)');
		expect(btn.disabled).toBe(true);
	});

	it('enqueues the id set the table last reported and hands off to the op-toast', async () => {
		const btn = mountButton();
		const { fetchFn, bodies } = makeFetch();
		vi.stubGlobal('fetch', fetchFn);

		const ctl = createExportController(BUTTON_ID);
		ctl.setFiltered([1, 2, 3], 3);
		ctl.setFiltered([7], 1); // the user narrowed the filter before clicking
		btn.click();
		await vi.waitFor(() => expect(bodies).toHaveLength(1));

		expect(bodies[0]).toEqual({ trackIds: [7] });
		await vi.waitFor(() => expect(startExportPolling).toHaveBeenCalledOnce());
	});

	/// A 409 means this user already has an export building. Interrupting with a dialog would be noise —
	/// the toast is already showing that run, so the click adopts it.
	it('adopts the running export on 409 instead of reporting a failure', async () => {
		const btn = mountButton();
		const { fetchFn } = makeFetch(false);
		vi.stubGlobal('fetch', fetchFn);
		const alerted: string[] = [];
		vi.stubGlobal('alert', (m: string) => alerted.push(m));

		const ctl = createExportController(BUTTON_ID);
		ctl.setFiltered([7], 1);
		btn.click();
		await vi.waitFor(() => expect(startExportPolling).toHaveBeenCalledOnce());

		expect(alerted).toHaveLength(0);
		expect(btn.disabled).toBe(false);
		expect(labelOf(btn)).toBe('Export GPX (1)');
	});

	it('re-enables after a failed enqueue so the user can retry', async () => {
		const btn = mountButton();
		vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('network down'))) as unknown as typeof fetch);
		const alerted: string[] = [];
		vi.stubGlobal('alert', (m: string) => alerted.push(m));

		const ctl = createExportController(BUTTON_ID);
		ctl.setFiltered([7], 1);
		btn.click();
		await vi.waitFor(() => expect(alerted).toHaveLength(1));

		expect(alerted[0]).toContain('network down');
		expect(startExportPolling).not.toHaveBeenCalled();
		expect(btn.disabled).toBe(false);
		expect(labelOf(btn)).toBe('Export GPX (1)');
	});
});
