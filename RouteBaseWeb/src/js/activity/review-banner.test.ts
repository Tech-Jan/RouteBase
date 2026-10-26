import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { setupReviewBanner } from './review-banner';

// The banner is the only way to approve a ride from the details page, and its two restore endpoints are not
// interchangeable — an auto ride restored through /api/trash/restore keeps its session 'abandoned' and the
// background purge reclaims it. Both are pinned here.

function pendingBanner(): void {
	document.body.innerHTML = `
		<div id="reviewBanner" data-track-id="42" data-review-source="auto">
			<select id="reviewTypeSelect"><option value="Ride" selected>Ride</option><option value="Run">Run</option></select>
			<button id="reviewApproveBtn"></button>
			<button id="reviewTrashBtn"></button>
		</div>`;
}

function trashedBanner(source: 'auto' | 'other'): void {
	document.body.innerHTML = `
		<div id="reviewBanner" data-track-id="42" data-review-source="${source}">
			<button id="reviewRestoreBtn"></button>
		</div>`;
}

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
	fetchMock = vi.fn().mockResolvedValue({ ok: true });
	vi.stubGlobal('fetch', fetchMock);
	// jsdom's reload/href assignment is not implemented — the banner navigates on success.
	vi.stubGlobal('location', { reload: vi.fn(), href: '' });
	vi.stubGlobal('confirm', vi.fn(() => true));
});

afterEach(() => {
	vi.unstubAllGlobals();
	document.body.innerHTML = '';
});

/** Let the click handler's awaited fetch settle. */
const settle = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

describe('review banner', () => {
	it('approves with the type currently picked', async () => {
		pendingBanner();
		setupReviewBanner();
		(document.getElementById('reviewTypeSelect') as HTMLSelectElement).value = 'Run';

		document.getElementById('reviewApproveBtn')!.click();
		await settle();

		const approveCall = must(fetchMock.mock.calls[0], 'one fetch call');
		expect(approveCall[0]).toBe('/api/review/approve');
		expect(JSON.parse(approveCall[1].body))
			.toEqual({ trackIds: [42], activityType: 'Run' });
	});

	it('re-enables the button when the call fails, so the ride is not stuck', async () => {
		pendingBanner();
		fetchMock.mockResolvedValue({ ok: false });
		setupReviewBanner();

		const btn = document.getElementById('reviewApproveBtn') as HTMLButtonElement;
		btn.click();
		await settle();

		expect(btn.disabled).toBe(false);
	});

	it('restores an auto ride through the review endpoint (session bookkeeping)', async () => {
		trashedBanner('auto');
		setupReviewBanner();

		document.getElementById('reviewRestoreBtn')!.click();
		await settle();

		expect(must(fetchMock.mock.calls[0], 'one fetch call')[0]).toBe('/api/review/restore');
	});

	it('restores any other ride through the generic trash endpoint', async () => {
		trashedBanner('other');
		setupReviewBanner();

		document.getElementById('reviewRestoreBtn')!.click();
		await settle();

		expect(must(fetchMock.mock.calls[0], 'one fetch call')[0]).toBe('/api/trash/restore');
	});

	it('does nothing on a released ride (no banner in the markup)', () => {
		document.body.innerHTML = '<div id="somethingElse"></div>';
		expect(() => setupReviewBanner()).not.toThrow();
	});
});
