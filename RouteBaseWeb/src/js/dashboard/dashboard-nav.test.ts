import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { pollFriendsBadge } from './dashboard-nav';

// DT-04: the friends poll must toggle only the friends pips, never every `.nav-pip`
// (a future non-friends badge would otherwise be force-toggled off the friends count).
describe('friends pending-count poll (DT-04)', () => {
	beforeEach(() => {
		document.body.innerHTML =
			'<span id="navFriendsBadge" class="nav-pip visible"></span>' +
			'<span id="navRecordBadge" class="nav-pip visible"></span>';
	});
	afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = ''; });

	it('toggles only friends pips, leaving a foreign pip untouched', async () => {
		vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ count: 0 }) } as unknown as Response)));

		await pollFriendsBadge();

		expect(document.getElementById('navFriendsBadge')!.classList.contains('visible')).toBe(false);
		expect(document.getElementById('navRecordBadge')!.classList.contains('visible')).toBe(true);
	});
});

// SO-03 + the 60 s interval can overlap, so two polls can be in flight at once. If the OLDER
// one's response arrives last, its stale count must NOT overwrite the newer poll's result.
describe('stale poll ordering: a superseded response never wins', () => {
	beforeEach(() => {
		document.body.innerHTML = '<span id="navFriendsBadge" class="nav-pip"></span>';
	});
	afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = ''; });

	it('an older poll resolving after a newer one does not flip the pip back', async () => {
		// fetch hands back deferred promises so the test controls resolution ORDER.
		const resolvers: Array<(r: unknown) => void> = [];
		const fetchMock = vi.fn(() => new Promise((resolve) => { resolvers.push(resolve); }));
		vi.stubGlobal('fetch', fetchMock as unknown as typeof fetch);

		const older = pollFriendsBadge(); // poll A — will resolve to count 1 (would SHOW)
		const newer = pollFriendsBadge(); // poll B — will resolve to count 0 (should HIDE)
		expect(resolvers.length).toBe(2);

		// Newer resolves FIRST (hide), then the older/stale one resolves LAST (count 1).
		must(resolvers[1], 'resolver 1 (two polls captured)')({ ok: true, json: async () => ({ count: 0 }) });
		await newer;
		must(resolvers[0], 'resolver 0 (two polls captured)')({ ok: true, json: async () => ({ count: 1 }) });
		await older;

		// The latest poll (count 0) is authoritative; the stale count-1 that landed last is ignored.
		expect(document.getElementById('navFriendsBadge')!.classList.contains('visible')).toBe(false);
	});
});

// CR6-01: the SO-03 event was wired to a dead module (no script tag, no importer, target
// ids in no cshtml). The listener now lives on the wired-up nav pips.
describe('SO-03: friend-request-resolved triggers an immediate pending-count refresh', () => {
	beforeEach(() => {
		document.body.innerHTML =
			'<span id="navFriendsBadge" class="nav-pip"></span>' +
			'<span id="navProfileFriendsBadge" class="nav-pip"></span>';
	});
	afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = ''; });

	it('re-fetches pending-count and toggles the pips visible when the event fires', async () => {
		const fetchMock = vi.fn(async () => ({ ok: true, json: async () => ({ count: 3 }) } as unknown as Response));
		vi.stubGlobal('fetch', fetchMock);

		// DOMContentLoaded wires setupFriendsBadge() (module-level listener, registered on import)
		// — this also fires the initial poll, which the mock covers harmlessly.
		document.dispatchEvent(new Event('DOMContentLoaded'));
		await Promise.resolve();
		await Promise.resolve();
		const callsBeforeEvent = fetchMock.mock.calls.length;

		document.dispatchEvent(new Event('friend-request-resolved'));
		await Promise.resolve();
		await Promise.resolve();
		await Promise.resolve();

		expect(fetchMock.mock.calls.length).toBe(callsBeforeEvent + 1);
		expect(fetchMock).toHaveBeenLastCalledWith('/api/friends/pending-count');
		expect(document.getElementById('navFriendsBadge')!.classList.contains('visible')).toBe(true);
		expect(document.getElementById('navProfileFriendsBadge')!.classList.contains('visible')).toBe(true);
	});
});
