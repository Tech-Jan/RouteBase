import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { must } from '../core/invariant';

// PU-01/02/05: photos.ts is a fully side-effecting page module (wires listeners + calls load()
// at import). Tests build the Photos.cshtml fixture first, stub fetch, then dynamic-import fresh
// per test (vi.resetModules) since page/total/loadGen are module singletons.

// Keep the lightbox minimap path (dynamic import of maplibre-gl) inert.
vi.mock('maplibre-gl', () => ({
	default: {
		Map: class { on() {} remove() {} },
		Marker: class { setLngLat() { return this; } addTo() { return this; } },
		LngLatBounds: class { extend() { return this; } },
	},
}));

const FIXTURE = `
	<div id="photoGrid"></div>
	<div id="photoGridEmpty"></div>
	<div id="photoPager"><span id="pageLabel"></span>
		<button id="btnPrevPage"></button><button id="btnNextPage"></button></div>
	<span id="photosCount"></span>
	<input id="filterFrom" /><input id="filterTo" />
	<input type="checkbox" id="groupByActivity" />
	<div id="photoSelectBar"><span id="photoSelectCount"></span>
		<button id="btnDeleteSelected"></button><button id="btnClearSelected"></button></div>
	<button id="btnApplyFilter"></button>
	<div id="lightbox" style="display:none">
		<img id="lightboxImg" /><div id="lightboxMeta"></div><a id="lightboxActivity"></a>
		<div id="lightboxMap"></div>
	</div>`;

function photo(id: number, extra: Partial<Record<string, unknown>> = {}): Record<string, unknown> {
	return {
		photoId: id, trackId: 10, trackName: `T${id}`, activityStartUtc: null,
		thumbHashBase64: null, width: 4, height: 3, markerLat: null, markerLng: null,
		trackPositionM: null, hasExifGps: false, locationSource: null, canRestore: false,
		capturedUtc: null, tzOffsetHours: null, cameraModel: null, origFilename: `f${id}.jpg`,
		description: null, v: 'z', ...extra,
	};
}

function resp(body: unknown): Response {
	return { ok: true, json: () => Promise.resolve(body) } as unknown as Response;
}

const flush = () => new Promise((r) => setTimeout(r, 0));

describe('photos gallery (PU-01 / PU-02 / PU-05)', () => {
	beforeEach(() => {
		document.body.innerHTML = FIXTURE;
		(window as unknown as { MAPTILER_API_KEY?: string }).MAPTILER_API_KEY = '';
		vi.resetModules();
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		document.body.innerHTML = '';
	});

	it('PU-05: group header title is HTML-escaped', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(resp({
			total: 1, page: 1, pageSize: 60, items: [photo(1, { trackName: '<script>x</script>' })],
		}))));
		(document.getElementById('groupByActivity') as HTMLInputElement).checked = true;
		await import('./photos');
		await flush();
		expect(document.querySelector('#photoGrid script')).toBeNull();
		expect(document.querySelector('.photos-group-title')!.textContent).toContain('<script>x</script>');
	});

	it('PU-01: a slow older-page response does not overwrite a newer page', async () => {
		// Deferred fetch queue: each /api/photos call gets its own controllable promise.
		const deferreds: Array<(r: Response) => void> = [];
		const fetchMock = vi.fn((url: string) => {
			if (typeof url === 'string' && url.startsWith('/api/photos?')) {
				return new Promise<Response>((res) => deferreds.push(res));
			}
			return Promise.resolve(resp({}));
		});
		vi.stubGlobal('fetch', fetchMock);

		await import('./photos');
		// Resolve the initial import-time load() first (page 1).
		must(deferreds[0], 'initial load deferred')(resp({ total: 180, page: 1, pageSize: 60, items: [photo(1)] }));
		await flush();

		// Two rapid Next clicks → page 2 (call idx 1), page 3 (call idx 2).
		document.getElementById('btnNextPage')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		document.getElementById('btnNextPage')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));

		// Resolve the newer (page 3) response first, then the stale page-2 response.
		must(deferreds[2], 'page-3 deferred')(resp({ total: 180, page: 3, pageSize: 60, items: [photo(3)] }));
		await flush();
		must(deferreds[1], 'page-2 deferred')(resp({ total: 180, page: 2, pageSize: 60, items: [photo(2)] }));
		await flush();

		// Grid + pager both reflect page 3; the stale page-2 items never touched the DOM.
		expect(document.getElementById('pageLabel')!.textContent).toBe('Page 3 of 3');
		expect(document.querySelectorAll('#photoGrid .photos-cell')).toHaveLength(1);
		expect(document.querySelector('#photoGrid img')!.getAttribute('alt')).toBe('f3.jpg');
	});

	it('item I: the lightbox renders the photo caption when present, escaped', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(resp({
			total: 1, page: 1, pageSize: 60, items: [photo(1, { description: 'Summit <b>selfie</b>' })],
		}))));
		await import('./photos');
		await flush();

		document.querySelector<HTMLElement>('#photoGrid .photos-cell')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));

		const caption = document.querySelector('.photos-meta-caption')!;
		expect(caption.textContent).toBe('Summit <b>selfie</b>');
		expect(caption.querySelector('b')).toBeNull(); // escaped, not rendered as markup
	});

	it('item I: no caption row when the photo has no description', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(resp({
			total: 1, page: 1, pageSize: 60, items: [photo(1)],
		}))));
		await import('./photos');
		await flush();

		document.querySelector<HTMLElement>('#photoGrid .photos-cell')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));

		expect(document.querySelector('.photos-meta-caption')).toBeNull();
	});

	it('pages the lightbox through the grid page with ‹ ›, wrapping at the end', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(resp({
			total: 3, page: 1, pageSize: 60, items: [photo(1), photo(2), photo(3)],
		}))));
		await import('./photos');
		await flush();

		const cells = document.querySelectorAll<HTMLElement>('#photoGrid .photos-cell');
		must(cells[1], 'grid cell 1').dispatchEvent(new MouseEvent('click', { bubbles: true }));
		const img = document.getElementById('lightboxImg') as HTMLImageElement;
		expect(img.getAttribute('alt')).toBe('f2.jpg');
		expect(document.querySelector('.photo-viewer-counter')!.textContent).toBe('2 / 3');

		document.querySelector<HTMLElement>('.photo-viewer-next')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect(img.getAttribute('alt')).toBe('f3.jpg');

		// Wraps back to the first photo — and the side panel follows, not just the image.
		document.querySelector<HTMLElement>('.photo-viewer-next')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect(img.getAttribute('alt')).toBe('f1.jpg');
		expect(document.querySelector('.photos-meta-file')!.textContent).toBe('f1.jpg');
	});

	it('closes on Escape only while the viewer is open, and leaves other keys alone', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(resp({
			total: 1, page: 1, pageSize: 60, items: [photo(1)],
		}))));
		await import('./photos');
		await flush();
		const box = document.getElementById('lightbox') as HTMLElement;

		// Closed: the page's own Escape handling must not be hijacked by a viewer that isn't up.
		const pageEsc = vi.fn();
		document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !e.defaultPrevented) pageEsc(); });
		document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
		expect(pageEsc).toHaveBeenCalledTimes(1);

		document.querySelector<HTMLElement>('#photoGrid .photos-cell')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', cancelable: true }));
		expect(box.style.display).toBe('none');
	});

	it('PU-04: Tab is trapped inside the open lightbox (aria-modal), wrapping at both ends', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(resp({
			total: 3, page: 1, pageSize: 60, items: [photo(1), photo(2), photo(3)],
		}))));
		await import('./photos');
		await flush();

		document.querySelector<HTMLElement>('#photoGrid .photos-cell')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));

		// DOM order of focusables inside #lightbox: the meta Delete button, the activity link, then the
		// chrome close/‹/› buttons appended after the body. First = Delete, last = the next arrow.
		const first = document.getElementById('lightboxDelete') as HTMLElement;
		const last = document.querySelector('.photo-viewer-next') as HTMLElement;
		expect(first).not.toBeNull();
		expect(last).not.toBeNull();

		// Tab off the last control wraps to the first — it must not escape into the grid behind the viewer.
		last.focus();
		const tab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
		document.dispatchEvent(tab);
		expect(document.activeElement).toBe(first);
		expect(tab.defaultPrevented).toBe(true);

		// Shift+Tab off the first wraps back to the last.
		first.focus();
		document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
		expect(document.activeElement).toBe(last);
	});

	it('PU-04: Tab is not trapped once the lightbox is closed', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(resp({
			total: 1, page: 1, pageSize: 60, items: [photo(1)],
		}))));
		await import('./photos');
		await flush();

		// No viewer open → the page's own Tab handling is untouched.
		const tab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
		document.dispatchEvent(tab);
		expect(tab.defaultPrevented).toBe(false);
	});

	it('PU-02: deleteSelected closes an open lightbox', async () => {
		vi.stubGlobal('confirm', vi.fn(() => true));
		vi.stubGlobal('fetch', vi.fn((url: string) => {
			if (typeof url === 'string' && url.startsWith('/api/photos?')) {
				return Promise.resolve(resp({ total: 1, page: 1, pageSize: 60, items: [photo(1)] }));
			}
			return Promise.resolve(resp({})); // delete + gps
		}));

		await import('./photos');
		await flush();

		// Open the lightbox for the photo, mark it for deletion, then bulk-delete.
		document.querySelector<HTMLElement>('#photoGrid .photos-cell')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect((document.getElementById('lightbox') as HTMLElement).style.display).toBe('');
		document.querySelector<HTMLElement>('.photos-cell-del')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		document.getElementById('btnDeleteSelected')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		await flush();

		expect((document.getElementById('lightbox') as HTMLElement).style.display).toBe('none');
	});

	it('item 1: the per-cell delete control is a real, keyboard-focusable <button>', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(resp({
			total: 1, page: 1, pageSize: 60, items: [photo(1)],
		}))));
		await import('./photos');
		await flush();

		const del = document.querySelector('.photos-cell-del') as HTMLElement;
		expect(del).not.toBeNull();
		// A real <button> is in the tab order and AT-labelled for free; the old <span role=button> with no
		// tabindex was not focusable — so this is the regression that made it mouse-only.
		expect(del.tagName).toBe('BUTTON');
		expect(del.getAttribute('aria-label')).toBe('Mark for deletion');
		del.focus();
		expect(document.activeElement).toBe(del); // a tabindex-less span could never become activeElement
	});

	it('item 1: activating the delete toggle marks the photo, sets aria-pressed, and reveals the bulk bar', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(resp({
			total: 2, page: 1, pageSize: 60, items: [photo(1), photo(2)],
		}))));
		await import('./photos');
		await flush();

		const del = document.querySelector('.photos-cell-del') as HTMLButtonElement;
		const wrap = del.closest('.photos-cell-wrap') as HTMLElement;
		const bar = document.getElementById('photoSelectBar') as HTMLElement;
		expect(del.getAttribute('aria-pressed')).toBe('false');
		expect(bar.style.display).toBe('none');

		del.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect(del.getAttribute('aria-pressed')).toBe('true');
		expect(wrap.classList.contains('is-marked')).toBe(true);
		expect(bar.style.display).toBe(''); // the bulk-delete bar is now reachable

		// Toggling off clears the mark, the pressed state and the bar.
		del.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect(del.getAttribute('aria-pressed')).toBe('false');
		expect(wrap.classList.contains('is-marked')).toBe(false);
		expect(bar.style.display).toBe('none');
	});

	it('item 1: Clear resets every marked cell, including the delete buttons’ aria-pressed', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(resp({
			total: 2, page: 1, pageSize: 60, items: [photo(1), photo(2)],
		}))));
		await import('./photos');
		await flush();

		const dels = document.querySelectorAll<HTMLButtonElement>('.photos-cell-del');
		must(dels[0], 'delete button 0').dispatchEvent(new MouseEvent('click', { bubbles: true }));
		must(dels[1], 'delete button 1').dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect([...dels].every((d) => d.getAttribute('aria-pressed') === 'true')).toBe(true);

		document.getElementById('btnClearSelected')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect([...dels].every((d) => d.getAttribute('aria-pressed') === 'false')).toBe(true);
		expect(document.querySelectorAll('.photos-cell-wrap.is-marked')).toHaveLength(0);
		expect((document.getElementById('photoSelectBar') as HTMLElement).style.display).toBe('none');
	});

	it('date filter sends plain yyyy-MM-dd for from and to — the end-of-day hack is the server\'s job', async () => {
		const fetchMock = vi.fn((_input: RequestInfo | URL) =>
			Promise.resolve(resp({ total: 0, page: 1, pageSize: 60, items: [] })));
		vi.stubGlobal('fetch', fetchMock);
		await import('./photos');
		await flush();

		(document.getElementById('filterFrom') as HTMLInputElement).value = '2026-06-01';
		(document.getElementById('filterTo') as HTMLInputElement).value = '2026-06-15';
		(document.getElementById('btnApplyFilter') as HTMLButtonElement).click();
		await flush();

		const url = String(fetchMock.mock.calls.at(-1)?.[0]);
		expect(url).toContain('from=2026-06-01');
		expect(url).toContain('to=2026-06-15');
		expect(url).not.toContain('T23%3A59%3A59');
	});

	it('FUX-06: a failed load shows a message instead of a blank grid', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, status: 500 } as Response)));
		await import('./photos');
		await flush();

		const msg = document.querySelector('#photoGrid .photos-empty');
		expect(msg).not.toBeNull();
		expect(msg!.textContent).toContain("Couldn't load photos");
	});

	it('FUX-06: a failed delete alerts instead of silently doing nothing', async () => {
		const alertSpy = vi.fn();
		vi.stubGlobal('alert', alertSpy);
		vi.stubGlobal('confirm', vi.fn(() => true));
		vi.stubGlobal('fetch', vi.fn((url: string) => {
			if (typeof url === 'string' && url.startsWith('/api/photos?')) {
				return Promise.resolve(resp({ total: 1, page: 1, pageSize: 60, items: [photo(1)] }));
			}
			if (url === '/api/photos/delete') return Promise.resolve({ ok: false, status: 500 } as unknown as Response);
			return Promise.resolve(resp({})); // gps etc.
		}));

		await import('./photos');
		await flush();

		document.querySelector<HTMLElement>('#photoGrid .photos-cell')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		document.querySelector<HTMLElement>('.photos-cell-del')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		document.getElementById('btnDeleteSelected')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		await flush();

		expect(alertSpy).toHaveBeenCalledWith("Couldn't delete the selected photos. Please try again.");
	});
});
