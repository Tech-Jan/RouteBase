import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { haversineDistance } from '../core/geo';

/**
 * live-recording-record.ts — 434 lines, the Record page's entire state machine, previously only touched
 * transitively (via live-recording.test.ts's single pagehide case). The uploader's own retry/dedup logic has
 * its own tests (live-recording-uploader.test.ts, LR-01..04); here we stub `createUploader` and pin how the
 * PAGE reacts to what it reports — especially the Stop button's cancel-and-resume path (LR-02): a drain
 * failure the user declines to override MUST re-arm GPS capture, or uploads end silently while the UI still
 * reads "recording".
 */

const uploaderHandle = {
	enqueue: vi.fn(),
	flush: vi.fn(() => Promise.resolve()),
	drainForStop: vi.fn(() => Promise.resolve(true)),
	snapshotForBeacon: vi.fn(() => null as { clientSequence: number; points: unknown[] } | null),
	hasUnsent: vi.fn(() => false),
	bufferedCount: vi.fn(() => 0),
	getClientSequence: vi.fn(() => 0),
	resume: vi.fn(),
};
const createUploader = vi.fn((_deps: unknown) => uploaderHandle);
vi.mock('./live-recording-uploader', async () => {
	const actual = await vi.importActual<typeof import('./live-recording-uploader')>('./live-recording-uploader');
	return {
		sanitizePoint: actual.sanitizePoint, // real + pure — let onPosition's sanitization run for real
		createUploader: (deps: unknown) => createUploader(deps),
	};
});

const startElapsedTimer = vi.fn((..._a: unknown[]) => 123);
vi.mock('./live-recording-shared', () => ({ startElapsedTimer: (...a: unknown[]) => startElapsedTimer(...a) }));

import { initRecordPage } from './live-recording-record';

function buildActiveDom(): void {
	document.body.innerHTML = `
		<div id="rec-start" style="display:none"></div>
		<div id="rec-active" data-session-id="sess-1" data-track-id="9"
			data-started-at="2026-07-17T10:00:00.000Z" data-last-sequence="0" data-point-count="0"></div>
		<button id="rec-start-btn"></button>
		<button id="rec-stop-btn"></button>
		<div id="rec-start-error" style="display:none"></div>
		<div id="rec-gps-text"></div>
		<div id="rec-elapsed"></div>
		<div id="rec-distance"></div>
		<div id="rec-speed"></div>
		<div id="rec-points"></div>
		<div id="rec-share-link"></div>
		<a id="rec-share-url"></a>
		<div id="rec-stopped-banner" style="display:none"></div>
		<a id="rec-activity-link" style="display:none"></a>
	`;
}

function buildStartDom(): void {
	document.body.innerHTML = `
		<div id="rec-start"></div>
		<div id="rec-active" style="display:none"></div>
		<button id="rec-start-btn"></button>
		<button id="rec-stop-btn"></button>
		<div id="rec-start-error" style="display:none"></div>
		<div id="rec-gps-text"></div>
		<div id="rec-elapsed"></div>
		<div id="rec-distance"></div>
		<div id="rec-speed"></div>
		<div id="rec-points"></div>
		<div id="rec-share-link"></div>
		<a id="rec-share-url"></a>
		<div id="rec-stopped-banner" style="display:none"></div>
		<a id="rec-activity-link" style="display:none"></a>
		<select id="rec-activity-type"><option value="Ride" selected>Ride</option></select>
		<input id="rec-track-name" value="Evening Ride" />
		<select id="rec-visibility"><option value="private" selected>private</option></select>
	`;
}

let watchPositionCalls = 0;
let lastPositionSuccess: ((p: GeolocationPosition) => void) | null = null;
const clearWatchMock = vi.fn();

function stubGeolocation(): void {
	watchPositionCalls = 0;
	lastPositionSuccess = null;
	vi.stubGlobal('navigator', {
		geolocation: {
			watchPosition: vi.fn((success: (p: GeolocationPosition) => void) => {
				watchPositionCalls++;
				lastPositionSuccess = success;
				return watchPositionCalls;
			}),
			clearWatch: clearWatchMock,
		},
	});
}

const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 20));

beforeEach(() => {
	vi.clearAllMocks();
	Object.assign(uploaderHandle, {
		drainForStop: vi.fn(() => Promise.resolve(true)),
		hasUnsent: vi.fn(() => false),
		bufferedCount: vi.fn(() => 0),
	});
	stubGeolocation();
	vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve({}) })));
});

afterEach(() => {
	vi.unstubAllGlobals();
});

describe('Stop button: drain-then-confirm gate (LR-02)', () => {
	it('drain fails and the user cancels the data-loss prompt ⇒ GPS capture is RE-ARMED, not left dead', async () => {
		buildActiveDom();
		uploaderHandle.drainForStop = vi.fn(() => Promise.resolve(false));
		uploaderHandle.hasUnsent = vi.fn(() => true);
		uploaderHandle.bufferedCount = vi.fn(() => 7);
		vi.stubGlobal('confirm', vi.fn(() => false)); // user declines to lose the 7 points

		initRecordPage();
		expect(watchPositionCalls).toBe(1); // the initial activateRecordingView → startCapture

		document.getElementById('rec-stop-btn')!.dispatchEvent(new Event('click'));
		await settle();

		expect(window.confirm).toHaveBeenCalledWith(expect.stringContaining('7 GPS points'));
		// The re-arm: a second watchPosition call and a second uploader.resume() — this is the exact call
		// the audit named as the one a future edit could silently drop.
		expect(watchPositionCalls).toBe(2);
		expect(uploaderHandle.resume).toHaveBeenCalledTimes(2);
		expect((document.getElementById('rec-stop-btn') as HTMLButtonElement).disabled).toBe(false);
		expect(document.getElementById('rec-stopped-banner')!.style.display).toBe('none'); // never actually stopped
	});

	it('drain fails but the user confirms anyway ⇒ proceeds to stop, does NOT re-arm', async () => {
		buildActiveDom();
		uploaderHandle.drainForStop = vi.fn(() => Promise.resolve(false));
		uploaderHandle.hasUnsent = vi.fn(() => true);
		uploaderHandle.bufferedCount = vi.fn(() => 3);
		vi.stubGlobal('confirm', vi.fn(() => true));
		const fetchMock = vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ trackId: 9 }) }));
		vi.stubGlobal('fetch', fetchMock);

		initRecordPage();
		expect(watchPositionCalls).toBe(1);

		document.getElementById('rec-stop-btn')!.dispatchEvent(new Event('click'));
		await settle();

		expect(watchPositionCalls).toBe(1); // no re-arm
		expect(fetchMock).toHaveBeenCalledWith('/api/recording/sessions/sess-1/stop', { method: 'POST' });
		expect(document.getElementById('rec-stopped-banner')!.style.display).toBe('');
	});

	it('drain succeeds ⇒ no confirm prompt at all, stops directly', async () => {
		buildActiveDom();
		uploaderHandle.drainForStop = vi.fn(() => Promise.resolve(true));
		uploaderHandle.hasUnsent = vi.fn(() => true); // must NOT be consulted when drainForStop already succeeded
		const confirmSpy = vi.fn(() => false);
		vi.stubGlobal('confirm', confirmSpy);
		const fetchMock = vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ trackId: 9 }) }));
		vi.stubGlobal('fetch', fetchMock);

		initRecordPage();
		document.getElementById('rec-stop-btn')!.dispatchEvent(new Event('click'));
		await settle();

		expect(confirmSpy).not.toHaveBeenCalled();
		expect(fetchMock).toHaveBeenCalledWith('/api/recording/sessions/sess-1/stop', { method: 'POST' });
	});

	it('the server stop call itself failing re-arms capture too (network/5xx, not just the confirm decline)', async () => {
		buildActiveDom();
		uploaderHandle.drainForStop = vi.fn(() => Promise.resolve(true));
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, status: 500, json: () => Promise.resolve({}) })));

		initRecordPage();
		expect(watchPositionCalls).toBe(1);

		document.getElementById('rec-stop-btn')!.dispatchEvent(new Event('click'));
		await settle();

		expect(watchPositionCalls).toBe(2);
		expect(document.getElementById('rec-gps-text')!.textContent).toContain('Stop failed');
		expect((document.getElementById('rec-stop-btn') as HTMLButtonElement).disabled).toBe(false);
	});
});

describe('Stop mid-drain: the 30 s status poll must not stack a second GPS watch', () => {
	// The race the idempotent startCapture() guards: Stop nulls the watch, awaits the drain, and while it
	// is pending the 30 s poll sees status==='recording' && gpsWatchId==null and re-arms; then the declined
	// data-loss confirm re-arms AGAIN. Without clear-before-arm, the poll's watch is never cleared and two
	// watches feed one uploader for the rest of the ride. Invariant: never more than one LIVE watch.
	it('re-arms exactly one live watch across a poll-during-drain then a declined confirm', async () => {
		vi.useFakeTimers();
		buildActiveDom();

		let resolveDrain: (() => void) | null = null;
		uploaderHandle.drainForStop = vi.fn(() => new Promise<boolean>((res) => { resolveDrain = () => res(false); }));
		uploaderHandle.hasUnsent = vi.fn(() => true);
		uploaderHandle.bufferedCount = vi.fn(() => 7);
		vi.stubGlobal('confirm', vi.fn(() => false)); // user declines to lose the points → re-arm
		// Every fetch here is the status poll; keep the session 'recording' so the poll re-arms mid-drain.
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ status: 'recording', trackId: 9 }) })));

		initRecordPage();
		expect(watchPositionCalls).toBe(1); // initial activate → startCapture

		// Enter the Stop handler: it clears the watch (clearWatch(1)) then parks on the drain await.
		document.getElementById('rec-stop-btn')!.dispatchEvent(new Event('click'));
		await Promise.resolve();

		// The 30 s poll fires while the drain is still pending → re-arm #1 (watch 2).
		await vi.advanceTimersByTimeAsync(30_000);
		expect(watchPositionCalls).toBe(2);

		// Drain resolves false → declined confirm → re-arm #2 (watch 3). The fix clears watch 2 first.
		resolveDrain!();
		await vi.advanceTimersByTimeAsync(20);
		expect(watchPositionCalls).toBe(3);

		// Live = armed minus cleared. armed {1,2,3}; cleared must be {1,2} so only watch 3 survives.
		const armed = new Set([1, 2, 3].slice(0, watchPositionCalls));
		for (const [id] of clearWatchMock.mock.calls) armed.delete(id as number);
		expect(armed.size).toBe(1); // exactly one GPS watch still live — no leak

		vi.useRealTimers();
	});
});

describe('GPS fix handling (onPosition)', () => {
	it('a fix updates the distance/speed readouts and enqueues the sanitized point', async () => {
		buildActiveDom();
		initRecordPage();
		expect(lastPositionSuccess).not.toBeNull();

		lastPositionSuccess!({
			coords: { latitude: 40, longitude: -70, altitude: 100, speed: 5, accuracy: 5, altitudeAccuracy: null, heading: null },
			timestamp: Date.now(),
		} as GeolocationPosition);

		expect(uploaderHandle.enqueue).toHaveBeenCalledTimes(1);
		const point = must(uploaderHandle.enqueue.mock.calls[0], 'one enqueue call')[0] as { lat: number; lng: number };
		expect(point.lat).toBeCloseTo(40, 6);
		expect(document.getElementById('rec-speed')!.textContent).toBe('18.0 km/h'); // 5 m/s → km/h

		lastPositionSuccess!({
			coords: { latitude: 40.001, longitude: -70, altitude: 100, speed: 5, accuracy: 5, altitudeAccuracy: null, heading: null },
			timestamp: Date.now(),
		} as GeolocationPosition);

		// Distance only accumulates from the SECOND fix onward (haversine needs a previous point). It must
		// equal the haversine hop between the two exact fixes, rendered as km — not merely "non-empty".
		const expectedKm = (haversineDistance(40, -70, 40.001, -70) / 1000).toFixed(2);
		expect(document.getElementById('rec-distance')!.textContent).toBe(`${expectedKm} km`);
		// Independent magnitude sanity: 0.001° of latitude ≈ 111 m ≈ 0.11 km (robust to the Earth-radius const).
		expect(expectedKm).toBe('0.11');
		expect(uploaderHandle.enqueue).toHaveBeenCalledTimes(2);
	});

	it('an unusable fix (out-of-range lat) is dropped — no enqueue, no distance update', async () => {
		buildActiveDom();
		initRecordPage();

		lastPositionSuccess!({
			coords: { latitude: 999, longitude: -70, altitude: 100, speed: 5, accuracy: 5, altitudeAccuracy: null, heading: null },
			timestamp: Date.now(),
		} as GeolocationPosition);

		expect(uploaderHandle.enqueue).not.toHaveBeenCalled();
	});

	// Item 3: the status line is a role=status live region, and onPosition rewrites it on every fix. A run
	// of fixes must NOT re-announce the coordinates (worse than silence); only a meaning change may repaint.
	it('sets role=status and does not rewrite the line across a run of GPS fixes', () => {
		buildActiveDom();
		initRecordPage();
		const line = document.getElementById('rec-gps-text')!;
		expect(line.getAttribute('role')).toBe('status');

		lastPositionSuccess!({
			coords: { latitude: 40, longitude: -70, altitude: 100, speed: 5, accuracy: 5, altitudeAccuracy: null, heading: null },
			timestamp: Date.now(),
		} as GeolocationPosition);
		const first = line.textContent;
		expect(first).toContain('GPS fix:');

		// A second fix at DIFFERENT coordinates: the 'fix' meaning is unchanged, so the node stays put — the
		// live position is conveyed by the distance/speed/points cells (asserted above), not by re-announcing.
		lastPositionSuccess!({
			coords: { latitude: 40.5, longitude: -70.5, altitude: 100, speed: 6, accuracy: 5, altitudeAccuracy: null, heading: null },
			timestamp: Date.now(),
		} as GeolocationPosition);
		expect(line.textContent).toBe(first);
	});
});

describe('Start button', () => {
	it('a successful start activates the recording view and begins GPS capture', async () => {
		buildStartDom();
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
			ok: true,
			json: () => Promise.resolve({ sessionId: 'sess-new', trackId: 42, startedAt: '2026-07-17T12:00:00.000Z' }),
		})));

		initRecordPage();
		document.getElementById('rec-start-btn')!.dispatchEvent(new Event('click'));
		await settle();

		expect(document.getElementById('rec-start')!.style.display).toBe('none');
		expect(document.getElementById('rec-active')!.style.display).toBe('');
		expect(document.getElementById('rec-active')!.dataset.sessionId).toBe('sess-new');
		expect(watchPositionCalls).toBe(1);
		expect(startElapsedTimer).toHaveBeenCalledTimes(1);
	});

	it('a failed start surfaces the error and re-enables the button without touching GPS', async () => {
		buildStartDom();
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, status: 400, json: () => Promise.resolve({}) })));

		initRecordPage();
		document.getElementById('rec-start-btn')!.dispatchEvent(new Event('click'));
		await settle();

		expect(document.getElementById('rec-start-error')!.style.display).toBe('');
		expect(document.getElementById('rec-start-error')!.textContent).toContain('400');
		expect((document.getElementById('rec-start-btn') as HTMLButtonElement).disabled).toBe(false);
		expect(watchPositionCalls).toBe(0);
	});
});

describe('session row actions (FUX-06)', () => {
	it('a failed abandon re-enables the button and alerts instead of silently doing nothing', async () => {
		buildStartDom();
		const btn = document.createElement('button');
		btn.className = 'session-abandon-btn';
		btn.dataset.sessionId = 'sess-other'; // not the current session → no teardown, just the POST
		document.body.appendChild(btn);

		const alertSpy = vi.fn();
		vi.stubGlobal('alert', alertSpy);
		vi.stubGlobal('confirm', vi.fn(() => true));
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, status: 500, json: () => Promise.resolve({}) })));

		initRecordPage();
		btn.dispatchEvent(new Event('click'));
		await settle();

		expect(alertSpy).toHaveBeenCalledWith("Couldn't abandon the session. Please try again.");
		expect(btn.disabled).toBe(false);
	});
});

describe('bfcache + the unload guard (PERF-5)', () => {
	/** Count the listeners the page holds on window, by type. */
	function windowListenerSpy(): { count(type: string): number; restore(): void } {
		const add = window.addEventListener.bind(window);
		const remove = window.removeEventListener.bind(window);
		const live = new Map<string, Set<EventListenerOrEventListenerObject>>();

		window.addEventListener = ((type: string, fn: EventListenerOrEventListenerObject, opts?: unknown) => {
			if (!live.has(type)) live.set(type, new Set());
			live.get(type)!.add(fn);
			add(type, fn as EventListener, opts as AddEventListenerOptions);
		}) as typeof window.addEventListener;

		window.removeEventListener = ((type: string, fn: EventListenerOrEventListenerObject, opts?: unknown) => {
			live.get(type)?.delete(fn);
			remove(type, fn as EventListener, opts as EventListenerOptions);
		}) as typeof window.removeEventListener;

		return {
			count: (type) => live.get(type)?.size ?? 0,
			restore: () => { window.addEventListener = add; window.removeEventListener = remove; },
		};
	}

	it('an IDLE /Record registers no beforeunload — that listener alone would block bfcache', () => {
		buildStartDom();
		const spy = windowListenerSpy();

		initRecordPage();

		expect(spy.count('beforeunload')).toBe(0);
		spy.restore();
	});

	it('starting a session arms the guard; a remote stop disarms it again', async () => {
		vi.useFakeTimers();
		buildStartDom();
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
			ok: true,
			json: () => Promise.resolve({ sessionId: 'sess-new', trackId: 42, startedAt: '2026-07-17T12:00:00.000Z' }),
		})));
		const spy = windowListenerSpy();

		initRecordPage();
		document.getElementById('rec-start-btn')!.dispatchEvent(new Event('click'));
		await vi.advanceTimersByTimeAsync(20);
		expect(spy.count('beforeunload')).toBe(1); // a live ride still warns on leave

		// The ride is stopped from the phone: the 30 s poll notices and ends the session here.
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
			ok: true, json: () => Promise.resolve({ status: 'finalized', trackId: 42 }),
		})));
		await vi.advanceTimersByTimeAsync(30_000);
		await vi.advanceTimersByTimeAsync(20);

		expect(document.getElementById('rec-stopped-banner')!.style.display).toBe('');
		expect(spy.count('beforeunload')).toBe(0); // nothing left to warn about → cacheable again
		spy.restore();
		vi.useRealTimers();
	});

	it('a bfcache restore re-syncs from the server instead of trusting the frozen screen', async () => {
		buildActiveDom();
		const fetchMock = vi.fn((_url: string) => Promise.resolve({
			ok: true, json: () => Promise.resolve({ status: 'finalized', trackId: 7 }),
		}));
		vi.stubGlobal('fetch', fetchMock);

		initRecordPage();
		fetchMock.mockClear();

		// Frozen in the cache, the ride ended on the phone; the page comes back showing "recording".
		window.dispatchEvent(Object.assign(new Event('pageshow'), { persisted: true }));
		await settle();

		expect(fetchMock.mock.calls.some(([u]) => String(u).includes('/api/recording/sessions/'))).toBe(true);
		expect(document.getElementById('rec-stopped-banner')!.style.display).toBe('');
	});

	it('a NORMAL load (persisted=false) does not re-fetch — the DOM the server rendered is the truth', async () => {
		buildActiveDom();
		const fetchMock = vi.fn((_url: string) => Promise.resolve({ ok: true, json: () => Promise.resolve({}) }));
		vi.stubGlobal('fetch', fetchMock);

		initRecordPage();
		fetchMock.mockClear();

		window.dispatchEvent(Object.assign(new Event('pageshow'), { persisted: false }));
		await settle();

		expect(fetchMock).not.toHaveBeenCalled();
	});
});
