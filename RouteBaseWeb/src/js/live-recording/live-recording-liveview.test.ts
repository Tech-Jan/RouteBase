import { must } from '../core/invariant';
import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from 'vitest';
import type { LiveSessionMeta } from './live-view-sessions';
import type { PointPayload } from './live-recording-shared';

// LV-06 (teardown) + PR-04 (paint coalescing).
//
// /LiveView acquired two setIntervals whose handles were not even captured, a follow-toggle listener,
// the panel's per-row listeners, a SignalR connection and a map — and released none of them. And it
// handed MapLibre the rider's entire LineString on every incoming batch. Both are pinned here.

const mapStub = {
	init: vi.fn(),
	destroy: vi.fn(),
	isReady: vi.fn(() => true),
	addPerson: vi.fn(),
	removePerson: vi.fn(),
	updateTrack: vi.fn(),
	flyToFirstPoint: vi.fn(),
	setFollowTarget: vi.fn(),
	getFollowTarget: vi.fn(() => null as string | null),
};
vi.mock('./live-recording-map', () => ({ liveRecordingMap: mapStub, FOLLOW_ZOOM: 15 }));

const conn = {
	on: vi.fn(),
	onreconnected: vi.fn(),
	invoke: vi.fn(() => Promise.resolve({ ok: true })), // JoinSession accepted, unless a test overrides it
	start: vi.fn(() => Promise.resolve()),
	stop: vi.fn(() => Promise.resolve()),
};

/** The ReceivePoints handler the page registered on the hub. */
function receivePoints(): (sessionId: string, points: PointPayload[]) => void {
	return conn.on.mock.calls.find(([ev]) => ev === 'ReceivePoints')![1];
}

let rafQueue: Map<number, FrameRequestCallback>;
let nextRafId: number;

function runFrame(): void {
	const frames = [...rafQueue.values()];
	rafQueue.clear();
	frames.forEach((cb) => cb(0));
}

const meta = (sessionId: string): LiveSessionMeta => ({
	sessionId, displayName: sessionId, isSelf: true, activityType: 'Ride',
	startedAt: '2026-07-12T10:00:00Z', status: 'recording', trackId: 1, pointCount: 0,
});

const pt = (lng: number, seq: number): PointPayload =>
	({ lat: 40, lng, seq });

function seedDom(metas: LiveSessionMeta[]): void {
	document.body.innerHTML = `
		<div id="liveview-app"></div>
		<div id="liveview-map"></div>
		<details class="map-dd" data-picker="true">
			<summary class="map-dd-summary"><span class="map-dd-value">Map</span></summary>
			<div class="map-dd-menu" id="lv-style-switcher"></div>
		</details>
		<script type="application/json" id="lv-sessions-data">${JSON.stringify(metas)}</script>
		<div id="lv-people-list"></div>
		<template id="lv-person-template">
			<div class="lv-person">
				<span class="lv-person-swatch"></span>
				<button class="lv-person-name"></button>
				<span class="lv-person-type"></span>
				<input class="lv-person-show" type="checkbox">
				<span class="lv-person-points"></span>
				<span class="lv-person-elapsed"></span>
				<span class="lv-person-status"></span>
				<a class="lv-person-activity"></a>
			</div>
		</template>
		<button id="lv-follow-toggle"></button>
		<span id="lv-status-badge"></span><div id="lv-stopped-banner"></div>
		<a id="lv-activity-link"></a>
		<span id="lv-elapsed"></span><span id="lv-points"></span>
		<span id="lv-type"></span><span id="lv-started"></span>
		<div id="lv-map-error" hidden></div>
		<button id="lv-map-retry-btn"></button>`;
}

/** Boot the page and let the map-readiness poll + the catch-up fetch settle. */
async function boot(): Promise<() => void> {
	const { initLiveViewPage } = await import('./live-recording-liveview');
	const dispose = initLiveViewPage();
	await vi.advanceTimersByTimeAsync(60); // pollUntil's first 50 ms tick → onMapReady
	await vi.advanceTimersByTimeAsync(0);  // the catch-up fetch resolves
	return dispose;
}

beforeEach(() => {
	vi.resetModules();
	vi.clearAllMocks();
	mapStub.isReady.mockReturnValue(true);
	mapStub.getFollowTarget.mockReturnValue(null);

	rafQueue = new Map();
	nextRafId = 1;
	vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
		const id = nextRafId++;
		rafQueue.set(id, cb);
		return id;
	});
	vi.stubGlobal('cancelAnimationFrame', (id: number) => { rafQueue.delete(id); });
	vi.stubGlobal('signalR', {
		HubConnectionBuilder: class {
			withUrl(): this { return this; }
			withAutomaticReconnect(): this { return this; }
			build(): typeof conn { return conn; }
		},
	});
	vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve([]) })));

	// NOT the default useFakeTimers(): it also fakes requestAnimationFrame, which would silently replace
	// the rAF stub above and make the paint-coalescing pins measure vitest's frame queue instead of ours.
	vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
	seedDom([meta('s1')]);
});

afterEach(() => {
	vi.useRealTimers();
	vi.unstubAllGlobals();
	document.body.innerHTML = '';
});

describe('/LiveView teardown — LV-06', () => {
	it('dispose() clears both intervals, drops the follow-toggle listener and stops the hub', async () => {
		const dispose = await boot();
		expect(vi.getTimerCount()).toBeGreaterThan(0); // the elapsed clock + the late-joiner poll

		dispose();

		expect(vi.getTimerCount()).toBe(0);
		expect(conn.stop).toHaveBeenCalledTimes(1);
		expect(mapStub.destroy).toHaveBeenCalledTimes(1);

		// The follow toggle is inert: a click after teardown must not drive the camera.
		mapStub.setFollowTarget.mockClear();
		document.getElementById('lv-follow-toggle')!.click();
		expect(mapStub.setFollowTarget).not.toHaveBeenCalled();
	});

	it('dispose() is idempotent — pagehide can fire twice', async () => {
		const dispose = await boot();

		dispose();
		dispose();

		expect(conn.stop).toHaveBeenCalledTimes(1);
		expect(mapStub.destroy).toHaveBeenCalledTimes(1);
	});

	it('dispose() cancels a pending paint frame', async () => {
		const dispose = await boot();
		receivePoints()('s1', [pt(1, 0)]);
		expect(rafQueue.size).toBe(1);

		dispose();

		expect(rafQueue.size).toBe(0);
	});
});

describe('/LiveView hidden-tab pause — PERF-3', () => {
	/** jsdom has no page-visibility model: set the getter, then fire the event the page listens for. */
	function setHidden(hidden: boolean): void {
		Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
		document.dispatchEvent(new Event('visibilitychange'));
	}

	afterEach(() => {
		Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
	});

	it('stops the repaint + joiner poll while hidden and restarts both on return', async () => {
		const dispose = await boot();
		const fetchMock = fetch as unknown as Mock<(url: string) => Promise<unknown>>;
		const running = vi.getTimerCount();
		expect(running).toBeGreaterThan(0);

		setHidden(true);
		expect(vi.getTimerCount()).toBe(0); // neither timer earns its keep in a hidden tab

		// A whole minute in the background costs nothing — no repaint, no /sessions/live fetch.
		fetchMock.mockClear();
		await vi.advanceTimersByTimeAsync(60_000);
		expect(fetchMock.mock.calls.filter(([u]) => String(u).includes('/sessions/live'))).toHaveLength(0);

		setHidden(false);
		expect(vi.getTimerCount()).toBe(running); // both back
		// …and discovery catches up at once rather than waiting out a fresh 30 s interval.
		await vi.advanceTimersByTimeAsync(0);
		expect(fetchMock.mock.calls.filter(([u]) => String(u).includes('/sessions/live')).length).toBeGreaterThan(0);

		dispose();
	});

	it('dispose() while hidden leaves nothing behind, and a later visibilitychange is inert', async () => {
		const dispose = await boot();
		setHidden(true);
		dispose();

		setHidden(false); // the listener is gone; nothing may re-arm a timer on a torn-down page
		expect(vi.getTimerCount()).toBe(0);
	});

	it('a tab opened in the background arms neither timer until it is first focused', async () => {
		// Opened hidden (a middle-clicked "Watch Live" link) — before any visibilitychange fires. The
		// timers must not arm on a page nobody is looking at; the share twin already guards this.
		Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
		const dispose = await boot();
		expect(vi.getTimerCount()).toBe(0);

		const fetchMock = fetch as unknown as Mock<(url: string) => Promise<unknown>>;
		fetchMock.mockClear();
		await vi.advanceTimersByTimeAsync(60_000);
		expect(fetchMock.mock.calls.filter(([u]) => String(u).includes('/sessions/live'))).toHaveLength(0);

		setHidden(false); // first focus arms both, exactly as the hidden→visible path does
		expect(vi.getTimerCount()).toBeGreaterThan(0);
		dispose();
	});
});

describe('/LiveView map toolbar', () => {
	// The Style switcher moved into the shared .map-dd dropdown (segment/race parity), which only reads
	// as a picker if the page wires setupMapToolbar — otherwise the summary sits on "Map" for ever.
	it('the Style summary follows the basemap MapShell marks active', async () => {
		const dispose = await boot();

		// What MapShell renders into the menu once the registry resolves.
		document.getElementById('lv-style-switcher')!.innerHTML =
			'<button class="map-dd-item">Liberty</button><button class="map-dd-item active">Satellite</button>';
		await vi.advanceTimersByTimeAsync(0); // let the observer's microtask land

		expect(document.querySelector('.map-dd-value')!.textContent).toBe('Satellite');
		dispose();
	});
});

describe('/LiveView join refusal — PI-08', () => {
	it('a refused initial JoinSession marks the row unavailable, distinctly from LIVE-with-no-points', async () => {
		conn.invoke.mockImplementation(() => Promise.resolve({ ok: false, reason: 'forbidden' }));

		await boot();

		const badge = document.querySelector<HTMLElement>('.lv-person-status')!;
		expect(badge.textContent).toBe('UNAVAILABLE');
		expect(badge.title).toBe('Live view unavailable for this rider');
	});

	it('a late joiner refused JoinSession is marked unavailable and its history is never fetched', async () => {
		await boot(); // s1 (default seed) joins fine — the default conn.invoke mock is still active

		// s2 appears later via the joiner poll; from here on every JoinSession is refused.
		conn.invoke.mockImplementation(() => Promise.resolve({ ok: false, reason: 'not_found' }));
		const fetchMock = fetch as unknown as Mock<(url: string) => Promise<unknown>>;
		fetchMock.mockImplementation((url: string) =>
			Promise.resolve({
				ok: true,
				json: () => Promise.resolve(url.includes('/sessions/live') ? [meta('s2')] : []),
			}),
		);
		fetchMock.mockClear();

		await vi.advanceTimersByTimeAsync(30_000); // JOINER_POLL_MS
		await vi.advanceTimersByTimeAsync(0);

		const badge = document.querySelector<HTMLElement>(`[data-session-id="s2"] .lv-person-status`)!;
		expect(badge.textContent).toBe('UNAVAILABLE');
		expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/points'))).toBe(false);
	});
});

describe('/LiveView paint coalescing — PR-04', () => {
	it('N batches inside one frame become ONE setData per rider, carrying the final coords', async () => {
		await boot();
		mapStub.updateTrack.mockClear();
		const receive = receivePoints();

		receive('s1', [pt(1, 0)]);
		receive('s1', [pt(2, 1)]);
		receive('s1', [pt(3, 2)]);
		receive('s1', [pt(4, 3)]);
		receive('s1', [pt(5, 4)]);
		expect(mapStub.updateTrack).not.toHaveBeenCalled(); // nothing painted yet — one frame, one paint

		runFrame();

		expect(mapStub.updateTrack).toHaveBeenCalledTimes(1);
		const [, coords] = must(mapStub.updateTrack.mock.calls[0], 'one updateTrack call');
		expect(coords).toHaveLength(5); // the FINAL accumulated array, not the first batch's
	});

	it('two riders dirty in the same frame produce exactly two setData', async () => {
		seedDom([meta('s1'), meta('s2')]);
		await boot();
		mapStub.updateTrack.mockClear();
		const receive = receivePoints();

		receive('s1', [pt(1, 0)]);
		receive('s2', [pt(9, 0)]);
		runFrame();

		expect(mapStub.updateTrack).toHaveBeenCalledTimes(2);
	});

	it('a hidden rider is not painted, even with points queued for the frame', async () => {
		await boot();
		mapStub.updateTrack.mockClear();

		receivePoints()('s1', [pt(1, 0)]);
		(document.querySelector('.lv-person-show') as HTMLInputElement).checked = false;
		document.querySelector('.lv-person-show')!.dispatchEvent(new Event('change'));
		runFrame();

		expect(mapStub.updateTrack).not.toHaveBeenCalled();
	});
});

describe('/LiveView map stall — FUX-01 (ported from the share twin FE-07)', () => {
	// The map is the centre of the page, so a never-ready engine (dead CDN / WebGL failure) must surface
	// a visible error + a Retry rather than a blank pane with only a console.warn.
	it('reveals the error pane when the engine never becomes ready', async () => {
		mapStub.isReady.mockReturnValue(false);
		const dispose = await boot();

		await vi.advanceTimersByTimeAsync(50 * 600 + 100); // MAP_READY_MAX_TICKS × 50 ms

		expect(document.getElementById('lv-map-error')!.hidden).toBe(false);
		dispose();
	});

	it('Retry rebuilds the engine and re-adds every rider once it comes up', async () => {
		mapStub.isReady.mockReturnValue(false);
		const dispose = await boot();
		await vi.advanceTimersByTimeAsync(50 * 600 + 100);
		expect(document.getElementById('lv-map-error')!.hidden).toBe(false);

		mapStub.isReady.mockReturnValue(true);
		mapStub.addPerson.mockClear();
		document.getElementById('lv-map-retry-btn')!.click();
		await vi.advanceTimersByTimeAsync(60); // the rebuilt engine's first readiness tick → onMapReady

		expect(mapStub.destroy).toHaveBeenCalled();  // drop the stalled shell first
		expect(mapStub.init).toHaveBeenCalledTimes(2);
		expect(document.getElementById('lv-map-error')!.hidden).toBe(true);
		// onMapReady re-adds the rider so the rebuilt map isn't left blank.
		expect(mapStub.addPerson).toHaveBeenCalledWith('s1', expect.anything(), expect.anything());
		dispose();
	});

	it('Retry is inert once the page is torn down', async () => {
		mapStub.isReady.mockReturnValue(false);
		const dispose = await boot();
		await vi.advanceTimersByTimeAsync(50 * 600 + 100);

		dispose();
		mapStub.init.mockClear();
		mapStub.isReady.mockReturnValue(true);
		document.getElementById('lv-map-retry-btn')!.click();
		await vi.advanceTimersByTimeAsync(60);

		expect(mapStub.init).not.toHaveBeenCalled(); // the listener is gone; a click can't rebuild a dead page
	});
});
