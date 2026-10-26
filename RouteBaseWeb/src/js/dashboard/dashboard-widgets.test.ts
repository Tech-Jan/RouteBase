import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { fetchSegments, fetchFriendsRecent } from './dashboard-widgets';

// The right-rail widgets render an animated skeleton until their fetch resolves. A 500/network failure
// used to only console.warn — leaving the skeleton shimmering "loading" forever. These pin that a
// failure now swaps the skeleton for a quiet failure message, and that the empty path still renders.

function widget(id: string): string {
	return `<div id="${id}"><div class="widget-header"></div><div class="widget-skeleton"></div></div>`;
}

function okJson(body: unknown): Response {
	return { ok: true, status: 200, json: () => Promise.resolve(body) } as unknown as Response;
}

beforeEach(() => {
	document.body.innerHTML = widget('segments-widget') + widget('friends-widget');
});

afterEach(() => {
	vi.unstubAllGlobals();
	document.body.innerHTML = '';
});

describe('dashboard segments widget', () => {
	it('shows a failure message instead of a permanent skeleton on a network error', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('offline'))));
		await fetchSegments();

		const el = document.getElementById('segments-widget')!;
		expect(el.querySelector('.widget-skeleton')).toBeNull();
		expect(el.querySelector('.widget-empty')!.textContent).toContain("Couldn't load segments");
	});

	it('shows a failure message on a non-ok response too', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, status: 500 } as Response)));
		await fetchSegments();

		expect(document.querySelector('#segments-widget .widget-skeleton')).toBeNull();
		expect(document.querySelector('#segments-widget .widget-empty')!.textContent).toContain("Couldn't load segments");
	});

	it('still renders the empty state when there are no segments', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(okJson([]))));
		await fetchSegments();

		const empty = document.querySelector('#segments-widget .widget-empty')!;
		expect(empty.textContent).toContain('No segments yet');
	});
});

describe('dashboard friends widget', () => {
	it('shows a failure message instead of a permanent skeleton on a network error', async () => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('offline'))));
		await fetchFriendsRecent();

		const el = document.getElementById('friends-widget')!;
		expect(el.querySelector('.widget-skeleton')).toBeNull();
		expect(el.querySelector('.widget-empty')!.textContent).toContain("Couldn't load friends");
	});
});
