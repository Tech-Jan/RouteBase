import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

// friends.ts wires a single delegated document click listener + module-level state at import.
// Import it exactly once (static) so only one listener is attached; each test builds fresh DOM
// with unique ids and drives the handlers via real click events.
import './friends';

function pendingSection(ids: number[]): void {
	const rows = ids
		.map(
			(id) =>
				`<tr id="pending-row-${id}"><td>User ${id}</td>` +
				`<td class="col-actions">` +
				`<button data-action="accept" data-id="${id}">Accept</button>` +
				`<button data-action="decline" data-id="${id}">Decline</button>` +
				`</td></tr>`,
		)
		.join('');
	document.body.innerHTML =
		`<section class="friends-section">` +
		`<div class="friends-section-header"><span>Pending Requests</span>` +
		`<span class="friends-badge" id="pendingCount">${ids.length}</span></div>` +
		`<table><tbody id="pendingRequestsBody">${rows}</tbody></table>` +
		`</section>`;
}

function clickAccept(id: number): void {
	document.querySelector<HTMLButtonElement>(`[data-action="accept"][data-id="${id}"]`)!
		.dispatchEvent(new MouseEvent('click', { bubbles: true }));
}

const flush = () => new Promise((r) => setTimeout(r, 0));

describe('friends page (SO-03 / SO-04)', () => {
	beforeEach(() => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve({}) } as unknown as Response)));
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		document.body.innerHTML = '';
	});

	it('SO-03: accepting the last pending request removes the whole section', async () => {
		pendingSection([101]);
		clickAccept(101);
		await flush();
		expect(document.getElementById('pendingRequestsBody')).toBeNull();
		expect(document.querySelector('.friends-section')).toBeNull();
	});

	it('SO-03: accepting one of two decrements #pendingCount and keeps the section', async () => {
		pendingSection([201, 202]);
		clickAccept(201);
		await flush();
		expect(document.getElementById('pending-row-201')).toBeNull();
		expect(document.getElementById('pendingCount')!.textContent).toBe('1');
		expect(document.querySelector('.friends-section')).not.toBeNull();
	});

	it('SO-03: dispatches friend-request-resolved for the nav badge', async () => {
		pendingSection([301]);
		const seen = vi.fn();
		document.addEventListener('friend-request-resolved', seen);
		clickAccept(301);
		await flush();
		document.removeEventListener('friend-request-resolved', seen);
		expect(seen).toHaveBeenCalled();
	});

	it('SO-04: double-clicking Accept only sends one request and no error toast', async () => {
		pendingSection([401]);
		let resolveFetch!: (v: unknown) => void;
		const pending = new Promise((r) => { resolveFetch = r; });
		const fetchMock = vi.fn(() => pending as Promise<Response>);
		vi.stubGlobal('fetch', fetchMock);

		clickAccept(401);
		clickAccept(401); // second click while first is in flight
		expect(fetchMock).toHaveBeenCalledTimes(1);

		resolveFetch({ ok: true, json: () => Promise.resolve({}) });
		await flush();
		expect(document.querySelector('.cockpit-toast--danger')).toBeNull();
	});
});
