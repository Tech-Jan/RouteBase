import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';

// /s/{token} kind="live" — the PUBLIC live map. It shipped with no test file at all: an anonymous
// surface whose only content is a map, driven by a poll loop against a token-scoped endpoint.
//
// The engine (live-recording-map) and the toolbar are faked; what runs for real is this page's own
// logic — dedup by seq, the poll chain's stop conditions, the 404 tolerance before it gives up, the
// finish handling, follow toggling, and the FE-07 map-stall retry.

const mapStub = {
	init: vi.fn(),
	destroy: vi.fn(),
	isReady: vi.fn(() => true),
	addPerson: vi.fn(),
	updateTrack: vi.fn(),
	flyToFirstPoint: vi.fn(),
	setFollowTarget: vi.fn(),
};

vi.mock('./live-recording-map', () => ({
	liveRecordingMap: mapStub,
	FOLLOW_ZOOM: 15,
}));
vi.mock('../core/map-toolbar', () => ({ setupMapToolbar: () => vi.fn() }));

interface Seed {
	status: string;
	startedAt: string;
	stoppedAt?: string | null;
	activityType?: string;
	apiBase: string;
}

function buildDom(seed: Seed): void {
	document.body.innerHTML = `
		<script type="application/json" id="lv-share-data">${JSON.stringify(seed)}</script>
		<div id="liveview-map"></div>
		<div class="map-dd-menu" id="lv-style-switcher"></div>
		<span id="lv-status-badge"></span>
		<div id="lv-live-hint" hidden></div>
		<div id="lv-conn-problem" hidden></div>
		<span id="lv-elapsed"></span><span id="lv-type"></span>
		<div id="lv-share-finished" style="display:none"></div>
		<button id="lv-share-follow-toggle"></button>
		<div id="lv-map-error" hidden></div>
		<button id="lv-map-retry-btn"></button>
		<div id="lv-map-card"></div>
		<button id="lv-map-grow-btn"></button><button id="lv-map-shrink-btn"></button>
		<button id="lv-map-reset-btn"></button><button id="lv-map-fullscreen-btn"></button>`;
}

const LIVE_SEED: Seed = {
	status: 'recording',
	startedAt: '2026-07-28T10:00:00.000Z',
	activityType: 'Ride',
	apiBase: '/s/tok/api',
};

function point(seq: number, lat = 47, lng = 8): { seq: number; lat: number; lng: number; ts: string } {
	return { seq, lat, lng, ts: '2026-07-28T10:00:00.000Z' };
}

/** Queue of poll responses; each fetch takes the next (the last one repeats). */
function respondWith(...bodies: Array<{ status?: number; body?: unknown }>): ReturnType<typeof vi.fn> {
	let i = 0;
	const fetchMock = vi.fn((_url: string) => {
		const next = must(bodies[Math.min(i++, bodies.length - 1)], 'respondWith needs >=1 body');
		const status = next.status ?? 200;
		return Promise.resolve({
			ok: status >= 200 && status < 300,
			status,
			json: () => Promise.resolve(next.body ?? {}),
		});
	});
	vi.stubGlobal('fetch', fetchMock);
	return fetchMock;
}

async function boot(seed: Seed = LIVE_SEED): Promise<() => void> {
	buildDom(seed);
	const { initShareLiveView } = await import('./share-live-view');
	const dispose = initShareLiveView();
	await vi.advanceTimersByTimeAsync(60); // map-readiness poll tick + the first fetch
    await vi.advanceTimersByTimeAsync(0);
	return dispose;
}

const badgeText = (): string => document.getElementById('lv-status-badge')!.textContent ?? '';

beforeEach(() => {
	vi.resetModules();
	vi.clearAllMocks();
	mapStub.isReady.mockReturnValue(true);
	vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
	vi.setSystemTime(new Date('2026-07-28T10:05:00.000Z'));
	respondWith({ body: { status: 'recording', points: [] } });
});

afterEach(() => {
	vi.useRealTimers();
	vi.unstubAllGlobals();
	document.body.innerHTML = '';
});

describe('/s/{token} live share — points', () => {
	it('draws the catch-up history and dedups by seq, never by timestamp', async () => {
		respondWith(
			{ body: { status: 'recording', points: [point(0), point(1), point(2)] } },
			{ body: { status: 'recording', points: [point(1), point(2), point(3)] } }, // server replays 2
		);

		const dispose = await boot();
		expect(mapStub.updateTrack).toHaveBeenLastCalledWith('live', [[8, 47], [8, 47], [8, 47]]);

		await vi.advanceTimersByTimeAsync(4000); // POLL_MS
		await vi.advanceTimersByTimeAsync(0);

		// Only seq 3 was new; the two replayed points must not extend the line.
		expect(mapStub.updateTrack).toHaveBeenLastCalledWith('live', Array(4).fill([8, 47]));
		dispose();
	});

	it('flies the camera in once and follows the moving position while live', async () => {
		respondWith({ body: { status: 'recording', points: [point(0)] } });

		const dispose = await boot();

		expect(mapStub.flyToFirstPoint).toHaveBeenCalledTimes(1);
		expect(mapStub.setFollowTarget).toHaveBeenCalledWith('live', { zoom: 15 });

		await vi.advanceTimersByTimeAsync(4000);
		await vi.advanceTimersByTimeAsync(0);
		expect(mapStub.flyToFirstPoint).toHaveBeenCalledTimes(1); // not on every batch
		dispose();
	});

	it('the follow toggle hands the camera back and takes it again', async () => {
		const dispose = await boot();
		const toggle = document.getElementById('lv-share-follow-toggle')!;

		toggle.click();
		expect(mapStub.setFollowTarget).toHaveBeenLastCalledWith(null, undefined);

		toggle.click();
		expect(mapStub.setFollowTarget).toHaveBeenLastCalledWith('live', { zoom: 15 });
		dispose();
	});
});

describe('/s/{token} live share — finishing', () => {
	it('a finalized response replaces the track wholesale and freezes the page', async () => {
		respondWith(
			{ body: { status: 'recording', points: [point(0), point(1)] } },
			{ body: { status: 'finalized', points: [point(100, 48, 9), point(101, 48.1, 9.1)] } },
		);

		const dispose = await boot();
		await vi.advanceTimersByTimeAsync(4000);
		await vi.advanceTimersByTimeAsync(0);

		// The finalized payload carries SYNTHETIC seqs (blob indexes): appending by seq would drop them
		// all, so the coords must be REPLACED, not merged.
		expect(mapStub.updateTrack).toHaveBeenLastCalledWith('live', [[9, 48], [9.1, 48.1]]);
		expect(document.getElementById('lv-share-finished')!.style.display).toBe('');
		expect(mapStub.setFollowTarget).toHaveBeenLastCalledWith(null); // camera released for panning
		expect(document.getElementById('lv-share-follow-toggle')!.style.display).toBe('none');
		dispose();
	});

	it('stops polling once finished', async () => {
		const fetchMock = respondWith({ body: { status: 'finalized', points: [point(0)] } });

		const dispose = await boot();
		const afterFinish = fetchMock.mock.calls.length;

		await vi.advanceTimersByTimeAsync(60_000);
		expect(fetchMock.mock.calls.length).toBe(afterFinish);
		dispose();
	});

	it('a recording that ended before the page opened still loads its frozen track', async () => {
		respondWith({ body: { status: 'finalized', points: [point(0), point(1)] } });

		const dispose = await boot({
			...LIVE_SEED, status: 'finalized', stoppedAt: '2026-07-28T10:02:00.000Z',
		});

		expect(mapStub.updateTrack).toHaveBeenCalled();
		expect(document.getElementById('lv-elapsed')!.textContent).not.toBe('');
		dispose();
	});
});

describe('/s/{token} live share — a dead link', () => {
	it('tolerates a burst of 404s (rate limiting looks identical) before giving up', async () => {
		respondWith({ status: 404 });

		const dispose = await boot();

		// One 404 proves nothing — the page must still be reading as live.
		expect(badgeText().toLowerCase()).not.toContain('unavailable');

		// A solid minute of them does: 15 consecutive → stop and say so.
		for (let i = 0; i < 15; i++) {
			await vi.advanceTimersByTimeAsync(4000);
			await vi.advanceTimersByTimeAsync(0);
		}

		expect(badgeText().toUpperCase()).toContain('UNAVAILABLE');
		dispose();
	});

	it('stops polling once the link is declared unavailable', async () => {
		const fetchMock = respondWith({ status: 404 });
		const dispose = await boot();

		for (let i = 0; i < 15; i++) {
			await vi.advanceTimersByTimeAsync(4000);
			await vi.advanceTimersByTimeAsync(0);
		}
		const settled = fetchMock.mock.calls.length;

		await vi.advanceTimersByTimeAsync(60_000);
		expect(fetchMock.mock.calls.length).toBe(settled); // an abandoned tab stops draining the bucket
		dispose();
	});
});

describe('/s/{token} live share — connection problems (FUX-06)', () => {
	it('surfaces a retrying note after repeated non-404 failures and clears it on recovery', async () => {
		// Three 5xx in a row, then a good poll. (A 5xx is recoverable — distinct from the 404 give-up.)
		respondWith({ status: 500 }, { status: 500 }, { status: 500 }, { body: { status: 'recording', points: [] } });
		const dispose = await boot(); // poll #1 (500)
		const conn = document.getElementById('lv-conn-problem')!;
		expect(conn.hidden).toBe(true); // one failure isn't enough — a blip must not latch the note

		await vi.advanceTimersByTimeAsync(4000); // poll #2 (500)
		await vi.advanceTimersByTimeAsync(4000); // poll #3 (500) → threshold reached
		expect(conn.hidden).toBe(false);

		await vi.advanceTimersByTimeAsync(4000); // poll #4 (200) → recovered
		expect(conn.hidden).toBe(true);
		dispose();
	});

	it('does not raise the note for a burst of 404s — those are the link-death path, not a blip', async () => {
		respondWith({ status: 404 });
		const dispose = await boot();

		for (let i = 0; i < 5; i++) {
			await vi.advanceTimersByTimeAsync(4000);
			await vi.advanceTimersByTimeAsync(0);
		}

		expect(document.getElementById('lv-conn-problem')!.hidden).toBe(true);
		dispose();
	});
});

describe('/s/{token} live share — the map is the whole page (FE-07)', () => {
	it('surfaces an error when the engine never becomes ready', async () => {
		mapStub.isReady.mockReturnValue(false);
		const dispose = await boot();

		await vi.advanceTimersByTimeAsync(50 * 600 + 100); // MAP_READY_MAX_TICKS × 50 ms

		expect(document.getElementById('lv-map-error')!.hidden).toBe(false);
		dispose();
	});

	it('Retry rebuilds the engine and re-flies the camera once it comes up', async () => {
		mapStub.isReady.mockReturnValue(false);
		respondWith({ body: { status: 'recording', points: [point(0)] } });
		const dispose = await boot();
		await vi.advanceTimersByTimeAsync(50 * 600 + 100);
		expect(document.getElementById('lv-map-error')!.hidden).toBe(false);

		mapStub.isReady.mockReturnValue(true);
		document.getElementById('lv-map-retry-btn')!.click();
		await vi.advanceTimersByTimeAsync(60);

		expect(mapStub.destroy).toHaveBeenCalled();       // drop the stalled shell first
		expect(mapStub.init).toHaveBeenCalledTimes(2);
		expect(document.getElementById('lv-map-error')!.hidden).toBe(true);
		// The rebuilt engine has no camera: without re-flying, the map returns dead while the toggle
		// still claims to be following.
		expect(mapStub.flyToFirstPoint).toHaveBeenCalled();
		dispose();
	});
});

describe('/s/{token} live share — status badge (role="status")', () => {
	// The badge is a polite live region, but the 1 s clock re-runs paintBadge every tick. Rewriting the
	// same label every second would make a screen reader re-announce it — so an unchanged state must not
	// touch the DOM. A preserved child-node identity proves innerHTML was not rewritten.
	it('does not rewrite the badge DOM on clock ticks when the state is unchanged', async () => {
		respondWith({ body: { status: 'recording', points: [] } }); // no points → stays CONNECTING
		const dispose = await boot();

		const badge = document.getElementById('lv-status-badge')!;
		expect(badge.textContent).toContain('CONNECTING');
		const node = badge.firstChild; // the "CONNECTING" text node

		await vi.advanceTimersByTimeAsync(3000); // three clock ticks, no state change (< POLL_MS)
		await vi.advanceTimersByTimeAsync(0);

		expect(badge.firstChild).toBe(node); // identical node → paintBadge left the DOM alone
		dispose();
	});
});

describe('/s/{token} live share — teardown', () => {
	it('dispose() stops every timer and the poll chain', async () => {
		const fetchMock = respondWith({ body: { status: 'recording', points: [point(0)] } });
		const dispose = await boot();

		dispose();
		const settled = fetchMock.mock.calls.length;

		await vi.advanceTimersByTimeAsync(60_000);
		expect(fetchMock.mock.calls.length).toBe(settled);
		expect(vi.getTimerCount()).toBe(0);
		expect(mapStub.destroy).toHaveBeenCalled();
	});
});
