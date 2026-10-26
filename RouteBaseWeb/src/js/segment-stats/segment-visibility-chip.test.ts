import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setupSegmentVisibilityChip } from './segment-visibility-chip';

function render({ withSelect = true }: { withSelect?: boolean } = {}): void {
	document.body.innerHTML = `
		<span class="det-badge det-badge--info" id="segVisibilityBadge" role="button" tabindex="0">
			<i class="bi bi-people"></i> Friends
		</span>
		${
			withSelect
				? `<select id="segVisibilitySelect" class="det-select det-select--vis"
				           data-segment-id="7" style="display:none !important;">
					<option value="private">private</option>
					<option value="friends" selected>friends</option>
					<option value="public">public</option>
				   </select>`
				: ''
		}`;
}

const badge = (): HTMLElement => document.getElementById('segVisibilityBadge')!;
const select = (): HTMLSelectElement => document.getElementById('segVisibilitySelect') as HTMLSelectElement;

/** Waits out the fetch promise chain the change handler kicks off. */
const settle = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

describe('segment-visibility-chip', () => {
	beforeEach(() => {
		render();
		vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 204 }));
	});

	afterEach(() => {
		vi.unstubAllGlobals();
		document.body.innerHTML = '';
	});

	it('swaps the badge for the select on click, and back after a successful change', async () => {
		setupSegmentVisibilityChip();

		badge().click();
		expect(badge().style.display).toBe('none');
		expect(select().style.display).toBe('inline-block');

		select().value = 'public';
		select().dispatchEvent(new Event('change'));
		await settle();

		expect(select().style.display).toBe('none');
		expect(badge().style.display).toBe('');
	});

	it('repaints the badge to the newly saved value', async () => {
		setupSegmentVisibilityChip();
		badge().click();

		select().value = 'private';
		select().dispatchEvent(new Event('change'));
		await settle();

		expect(badge().className).toBe('det-badge det-badge--danger');
		expect(badge().textContent?.trim()).toBe('Private');
	});

	it('PATCHes the chosen value to the segment endpoint', async () => {
		setupSegmentVisibilityChip();
		badge().click();

		select().value = 'public';
		select().dispatchEvent(new Event('change'));
		await settle();

		expect(fetch).toHaveBeenCalledWith(
			'/api/SegmentsApi/7/visibility',
			expect.objectContaining({ method: 'PATCH', body: JSON.stringify({ visibility: 'public' }) }),
		);
	});

	it('reverts the select and leaves the badge alone when the server refuses', async () => {
		vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 403 }));
		setupSegmentVisibilityChip();
		badge().click();

		select().value = 'public';
		select().dispatchEvent(new Event('change'));
		await settle();

		// The UI must never claim a change that did not happen.
		expect(select().value).toBe('friends');
		expect(badge().textContent?.trim()).toBe('Friends');
		expect(badge().className).toBe('det-badge det-badge--info');
	});

	it('reverts on a network error too', async () => {
		vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('offline')));
		setupSegmentVisibilityChip();
		badge().click();

		select().value = 'private';
		select().dispatchEvent(new Event('change'));
		await settle();

		expect(select().value).toBe('friends');
		expect(badge().textContent?.trim()).toBe('Friends');
	});

	it('dismisses back to the badge on blur without saving anything', () => {
		setupSegmentVisibilityChip();
		badge().click();

		select().dispatchEvent(new Event('blur'));

		expect(badge().style.display).toBe('');
		expect(fetch).not.toHaveBeenCalled();
	});

	it('opens on Enter and Space, so the chip is reachable without a mouse', () => {
		setupSegmentVisibilityChip();

		badge().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
		expect(select().style.display).toBe('inline-block');

		select().dispatchEvent(new Event('blur'));
		badge().dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true }));
		expect(select().style.display).toBe('inline-block');
	});

	it('is a no-op for a viewer who has a badge but no select', () => {
		render({ withSelect: false });

		expect(() => setupSegmentVisibilityChip()).not.toThrow();

		badge().click();
		expect(badge().style.display).toBe(''); // nothing to swap to, so nothing hides
	});
});
