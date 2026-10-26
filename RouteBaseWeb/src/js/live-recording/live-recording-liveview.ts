// live-recording-liveview.ts — LiveView page: watch one or many live recordings on a single map.
// SignalR observer (ReceivePoints / SessionStopped / SessionPaused / SessionResumed) fanned out
// across N sessions; the camera follows one rider at a time (CameraControls, segment-replay parity).

import { FOLLOW_ZOOM, liveRecordingMap } from './live-recording-map';
import { must } from '../core/invariant';
import { pollUntil } from '../core/poll-until';
import { cameraTarget, createSessionStore, pickInitialFollow, type LiveSessionMeta, type WatchedSession } from './live-view-sessions';
import { createRiderPanel } from './live-view-panel';
import { setupShareLinks } from '../core/share-links';
import { createCadenceTracker, type CadenceTracker, type CadenceState } from './live-cadence-state';
import { createActiveElapsed, type ActiveElapsed } from './live-elapsed';
import { renderFollowToggleLabel } from '../core/map-utils-shared';
import { setupMapToolbar } from '../core/map-toolbar';
import { readIsland } from '../core/page-bootstrap';
import type { JoinSessionResult, PointPayload } from './live-recording-shared';

/** How often to look for a friend who started streaming after the page loaded. */
const JOINER_POLL_MS = 30_000;

/** Tears the page down: timers, listeners, the pending frame, the hub and the map. Idempotent. */
export type LiveViewDispose = () => void;

export function initLiveViewPage(): LiveViewDispose {
	const metas = readIsland<LiveSessionMeta[]>('lv-sessions-data', []);
	if (metas.length === 0) return () => { /* nothing was acquired */ };

	const store = createSessionStore();

	// ── Cadence (per rider) ──────────────────────────────────────────────
	// Each rider's phone owns its own upload cadence and warms up independently, so one tracker per
	// session. The badge shows LIVE only once two updates land close together, never on a single arrival
	// that might just be a coincidental idle upload. See live-cadence-state.ts.
	const cadence = new Map<string, CadenceTracker>();
	const trackerFor = (sessionId: string): CadenceTracker => {
		let t = cadence.get(sessionId);
		if (!t) cadence.set(sessionId, (t = createCadenceTracker()));
		return t;
	};
	const cadenceOf = (session: WatchedSession): CadenceState =>
		trackerFor(session.meta.sessionId).state(session.status, Date.now());

	// ── Elapsed (per rider) ──────────────────────────────────────────────
	// Active riding time (live-elapsed.ts): the elapsed cell ticks while recording, FREEZES while paused
	// and holds a final value once ended — consistently with the public share view (FE-08/FE-12). One
	// accumulator per rider, seeded at that rider's own start.
	const elapsedTrackers = new Map<string, ActiveElapsed>();
	const elapsedOf = (session: WatchedSession): number => {
		let e = elapsedTrackers.get(session.meta.sessionId);
		if (!e) elapsedTrackers.set(session.meta.sessionId,
			(e = createActiveElapsed(new Date(session.meta.startedAt).getTime())));
		return e.sample(session.status, Date.now());
	};

	const panel = createRiderPanel(
		must(document.getElementById('lv-people-list'), 'LiveView #lv-people-list'),
		document.getElementById('lv-person-template') as HTMLTemplateElement,
		{
			onToggleVisible: (sessionId, visible) => setVisible(sessionId, visible),
			onFollow: (sessionId) => setFollow(sessionId),
			cadenceOf,
			elapsedOf,
		},
	);

	let followId: string | null = pickInitialFollow(metas);
	let lastFollowId: string | null = followId; // remembered across (unfollow) → (follow)
	let mapReady = false;
	/** The map has been moved off the world view at least once (by a follow lock-on or a fly-to). */
	let initialViewSet = false;

	for (const meta of metas) store.add(meta);

	// ── Share-live link (owner only) ─────────────────────────────────────
	// Mint a public link for the viewer's OWN session (kind "live", keyed by its track id). No-ops when
	// the owner-gated button+dialog markup is absent (watching only others', or a stranger's shared URL).
	const self = metas.find((m) => m.isSelf);
	if (self) setupShareLinks({ kind: 'live', resourceId: self.trackId });

	// ── Map ──────────────────────────────────────────────────────────────

	// Held so the FUX-01 retry path can re-init the engine with the exact same config after a stall.
	const mapInitOpts = {
		containerId: 'liveview-map',
		styleSwitcherId: 'lv-style-switcher',
		sizeControls: {
			mapDivId: 'liveview-map',
			growBtnId: 'lv-map-grow-btn', shrinkBtnId: 'lv-map-shrink-btn',
			resetBtnId: 'lv-map-reset-btn', defaultHeight: 600,
			fullscreenBtnId: 'lv-map-fullscreen-btn', fullscreenCardId: 'lv-map-card',
		},
	};
	liveRecordingMap.init(mapInitOpts);

	const mapError = document.getElementById('lv-map-error');
	const mapRetryBtn = document.getElementById('lv-map-retry-btn');

	// Style/Size <details> shell — same toolbar as the segment and race pages. After init, so the Style
	// summary reads the basemap MapShell just marked active (the observer covers the other order too).
	const teardownToolbar = setupMapToolbar();

	/**
	 * Drives the camera towards what the panel says it should be following. The map facade holds the
	 * truth (which rider it is centring, if any), so this is idempotent and safe to call on every point.
	 *
	 * A rider with no fix yet is NOT followed: the idle loop would pin the map to the world view, and
	 * since CameraControls clamps the user's zoom to ±5 around the seeded one, the viewer could not
	 * even zoom in to the riders who *do* have tracks. Their first point engages the camera instead.
	 */
	function applyCamera(): void {
		if (!mapReady) return;

		const want = cameraTarget(followId, store);
		const current = liveRecordingMap.getFollowTarget();
		if (want === current) return;

		if (!want) {
			liveRecordingMap.setFollowTarget(null);
			return;
		}
		// Free camera → following zooms in to the rider; rider → rider keeps the viewer's zoom.
		liveRecordingMap.setFollowTarget(want, current === null ? { zoom: FOLLOW_ZOOM } : undefined);
		initialViewSet = true;
	}

	// ── Paint coalescing (PR-04) ─────────────────────────────────────────
	//
	// updateTrack hands MapLibre the rider's ENTIRE accumulated LineString, which it re-parses and
	// re-tiles — O(n) per call, with n growing all session. Painting straight off each ReceivePoints
	// batch multiplied that by the number of riders watched, which is exactly what this slice added.
	// Marking dirty and flushing once per frame bounds it to ≤1 setData per rider per frame, whatever
	// the batch arrival rate. (The O(n) re-tile itself is inherent — MapLibre has no append API.)

	const dirty = new Set<string>();
	let frameId: number | null = null;

	function paint(session: WatchedSession): void {
		dirty.add(session.meta.sessionId);
		if (frameId === null) frameId = requestAnimationFrame(flush);
	}

	function flush(): void {
		frameId = null;
		// Not ready yet: leave the ids dirty (the store keeps every point) — onMapReady flushes them.
		if (!mapReady) return;

		let firstPainted: WatchedSession | null = null;
		for (const sessionId of dirty) {
			const session = store.get(sessionId);
			if (!session || !session.visible) continue;
			liveRecordingMap.updateTrack(sessionId, session.coords);
			firstPainted ??= session;
		}
		dirty.clear();
		if (!firstPainted) return;

		applyCamera();

		// Not following anyone (yet) — the first track to arrive still has to put the map somewhere,
		// or it sits on the world view with the tracks off-screen.
		if (!initialViewSet && liveRecordingMap.getFollowTarget() === null && firstPainted.coords.length > 0) {
			initialViewSet = true;
			const [lng, lat] = must(firstPainted.coords[firstPainted.coords.length - 1], 'non-empty coords (guarded above)');
			liveRecordingMap.flyToFirstPoint(lat, lng);
		}
	}

	/** Draw now instead of next frame — page load and catch-up settle land the whole history at once. */
	function flushNow(): void {
		if (frameId !== null) {
			cancelAnimationFrame(frameId);
			frameId = null;
		}
		flush();
	}

	function render(session: WatchedSession): void {
		panel.updateRow(session);
		if (session.meta.sessionId === followId) panel.renderKpis(session);
		panel.renderPageStatus(store.all());
	}

	// The map is the one part that can fail (WebGL/CDN), so the panel and the catch-ups do not wait
	// on it: rows render immediately and points accumulate in the store. onMapReady then draws
	// whatever has already arrived — nothing is lost in the gap.
	function onMapReady(): void {
		mapReady = true;
		for (const session of store.all()) {
			if (!session.visible) continue;
			liveRecordingMap.addPerson(session.meta.sessionId, session.color, session.color);
			paint(session); // draws the catch-up that may have landed while the map was still loading
		}
		flushNow();
		applyCamera();
	}

	// ── Map readiness (bounded — a dead CDN/WebGL must not spin forever, LR-08) ──
	// The map is the centre of the page, so a stall must NOT fail to a blank pane with only a console
	// warning (FUX-01). After a generous wait we reveal a visible error + a Retry that re-inits the
	// engine — the same pane, copy and retry the public share twin already ships (FE-07). 30 s (matches
	// the share twin, up from 10 s), so a merely-cold CDN doesn't surface a needless error mid-load.
	const MAP_READY_MAX_TICKS = 600; // ×50 ms = 30 s

	function showMapError(): void {
		console.warn('LiveView map never became ready — giving up after 30 s');
		if (mapError) mapError.hidden = false;
	}

	function waitForMap(): void {
		void pollUntil(
			() => {
				if (!liveRecordingMap.isReady()) return false;
				onMapReady();
				return true;
			},
			{ intervalMs: 50, maxTicks: MAP_READY_MAX_TICKS, onTimeout: showMapError },
		);
	}

	function retryMap(): void {
		if (mapReady || disposed) return;
		if (mapError) mapError.hidden = true;
		liveRecordingMap.destroy(); // drop the stalled shell before rebuilding it
		liveRecordingMap.init(mapInitOpts);
		waitForMap(); // onMapReady re-adds every rider + re-applies the camera once it comes up
	}
	mapRetryBtn?.addEventListener('click', retryMap);
	waitForMap();

	// ── Panel + history: independent of the map ──────────────────────────

	for (const session of store.all()) {
		panel.addRow(session);
		void loadCatchup(session.meta.sessionId);
	}
	panel.setFollowed(followId);
	panel.renderKpis(followId ? store.get(followId) ?? null : null);
	panel.renderPageStatus(store.all());

	// ── Follow camera (segment-replay parity: explicit unfollow, drag rotates) ────

	function setFollow(sessionId: string | null): void {
		followId = sessionId;
		if (sessionId) lastFollowId = sessionId;
		panel.setFollowed(sessionId);
		syncFollowToggle();
		panel.renderKpis(sessionId ? store.get(sessionId) ?? null : null);

		// Re-centre on the new target (or free the camera). A target with no fix yet stays free until
		// their first point lands in paint().
		applyCamera();
	}

	// Two toggles, one handler + sync: the riders-panel toggle and an on-map pill the fullscreen CSS reveals
	// (the panel is hidden when the map is fullscreen). Same label helper as the replays/share view.
	const followToggles = ['lv-follow-toggle', 'lv-follow-toggle-map']
		.map((id) => document.getElementById(id))
		.filter((el): el is HTMLElement => el != null);
	const onFollowToggleClick = (): void => setFollow(followId != null ? null : lastFollowId);

	function syncFollowToggle(): void {
		const following = followId != null;
		const title = following
			? 'Free camera — stop following'
			: 'Follow ' + (store.get(lastFollowId ?? '')?.meta.displayName ?? 'a rider');
		for (const el of followToggles) {
			renderFollowToggleLabel(el, following);
			el.title = title;
		}
	}
	for (const el of followToggles) el.addEventListener('click', onFollowToggleClick);
	syncFollowToggle();

	function setVisible(sessionId: string, visible: boolean): void {
		store.setVisible(sessionId, visible);
		const session = store.get(sessionId);
		if (!session) return;

		// Never follow someone who isn't drawn — before the map-ready guard, or unticking a rider while
		// the map is still loading would leave the panel highlighting a follow target the camera dropped.
		if (!visible && followId === sessionId) setFollow(null);
		if (!mapReady) return;

		if (visible) {
			// removePerson dropped the engine's copy of the track — addPerson starts empty, paint refills it.
			liveRecordingMap.addPerson(sessionId, session.color, session.color);
			paint(session);
		} else {
			liveRecordingMap.removePerson(sessionId);
		}
	}

	// ── Catch-up (full history per rider, from session start) ─────────────

	async function loadCatchup(sessionId: string, retry = true): Promise<void> {
		try {
			const r = await fetch(`/api/recording/sessions/${sessionId}/points`);
			if (!r.ok) throw new Error('catch-up fetch ' + r.status);
			const pts: PointPayload[] = await r.json();
			settle(sessionId, pts);
		} catch (e) {
			if (retry) {
				setTimeout(() => void loadCatchup(sessionId, false), 2000);
				return;
			}
			console.warn(`LiveView catch-up failed for ${sessionId}; flushing queued live batches only`, e);
			settle(sessionId, []); // give up on history but still flush live batches so the map isn't frozen
		}
	}

	function settle(sessionId: string, catchup: PointPayload[]): void {
		const paintable = store.settleCatchup(sessionId, catchup);
		const session = store.get(sessionId);
		if (!session) return;
		if (paintable) {
			// Deliberately NOT a cadence sample (FE-03): the catch-up arrives at page-open time, which says
			// nothing about the phone's UPLOAD cadence — counting it let an idle upload landing ≤15 s after
			// open fake the confirmed-fast LIVE state. Only live batches / reconnect fills sample.
			paint(session);
			flushNow(); // the whole history, once, on load — not worth deferring a frame
		}
		render(session);
	}

	// ── Teardown (LV-06) ─────────────────────────────────────────────────
	// Every timer, listener, frame and connection this page acquires is released here. Declared before
	// the acquisitions so the SignalR guard below can return it.

	let disposed = false;
	let connection: signalR.HubConnection | null = null;
	let joinerTimer: number | null = null;

	function dispose(): void {
		if (disposed) return; // pagehide can fire twice (bfcache in, then the real unload)
		disposed = true;

		if (clockTimer !== null) clearInterval(clockTimer);
		if (joinerTimer !== null) clearInterval(joinerTimer);
		document.removeEventListener('visibilitychange', onVisibility);
		if (frameId !== null) cancelAnimationFrame(frameId);

		for (const el of followToggles) el.removeEventListener('click', onFollowToggleClick);
		mapRetryBtn?.removeEventListener('click', retryMap);
		teardownToolbar();
		panel.dispose();
		// A rejected stop must not surface as an unhandled rejection mid-teardown.
		void connection?.stop().catch(() => { /* going away anyway */ });
		liveRecordingMap.destroy();
	}

	// ── Elapsed clock (one timer for every rider) ─────────────────────────

	// The 1 s repaint. Restartable: it is stopped while the tab is hidden (PERF-3) and re-armed on return.
	function tickClock(): void {
		for (const session of store.all()) {
			if (session.status === 'recording') panel.updateRow(session);
		}
		// The header badge aggregates wall-clock-dependent cadence states (live → waiting after 30 s of
		// silence), so it must re-evaluate on the tick like the rows do — without this, a stalled stream
		// left the header pulsing LIVE while its row said WAITING (FE-02).
		panel.renderPageStatus(store.all());
		const followed = followId ? store.get(followId) : null;
		if (followed) panel.renderKpis(followed);
	}

	// PERF-3: a tab opened in the background (e.g. middle-clicking a rider's "Watch Live" link) must not
	// arm the repaint before it is ever seen — the share twin already guards this (share-live-view.ts).
	// onVisibility starts it the moment the tab is focused.
	let clockTimer: number | null = document.hidden ? null : window.setInterval(tickClock, 1000);

	// ── SignalR ──────────────────────────────────────────────────────────

	if (typeof signalR === 'undefined') {
		console.error('SignalR client not loaded');
		return dispose; // the clock, the listener and the map still need tearing down
	}

	const conn = new signalR.HubConnectionBuilder()
		.withUrl('/hubs/recording')
		.withAutomaticReconnect()
		.build();
	connection = conn;

	conn.on('ReceivePoints', (sessionId: string, points: PointPayload[]) => {
		// The hub already tags every batch with its session — that is what routes it to the right rider.
		if (!store.receive(sessionId, points)) return;
		trackerFor(sessionId).onUpdate(Date.now()); // a live batch landed — a cadence sample for this rider
		const session = must(store.get(sessionId), 'store.receive(sessionId) returned true');
		paint(session);
		render(session);
	});

	function onStatusEvent(sessionId: string, status: string, trackId?: number): void {
		const session = store.get(sessionId);
		if (!session) return;
		store.setStatus(sessionId, status);
		if (trackId) session.meta.trackId = trackId;
		render(session);
	}

	conn.on('SessionStopped', (sessionId: string, trackId: number) => onStatusEvent(sessionId, 'stopped', trackId));
	conn.on('SessionPaused', (sessionId: string) => onStatusEvent(sessionId, 'paused'));
	conn.on('SessionResumed', (sessionId: string) => onStatusEvent(sessionId, 'recording'));

	/**
	 * PI-08: a refused join used to just return, indistinguishable on the client from "joined fine, no
	 * GPS fix yet" — the row sat there silently instead of telling the viewer why. Marks the row
	 * 'unavailable' (distinct from 'recording' with zero points) and returns whether the join succeeded,
	 * so callers can skip the now-pointless catch-up fetch for a rider they were refused.
	 */
	async function joinSession(sessionId: string): Promise<boolean> {
		const result = await conn.invoke<JoinSessionResult>('JoinSession', sessionId);
		if (result.ok) return true;

		console.warn(`LiveView: join refused for session ${sessionId} (${result.reason ?? 'unknown reason'})`);
		const session = store.get(sessionId);
		if (session) {
			store.setStatus(sessionId, 'unavailable');
			render(session);
		}
		return false;
	}

	/** Group membership is lost on reconnect — rejoin every session, then fetch each disconnect gap. */
	async function rejoinAndCatchUp(retry = true): Promise<void> {
		try {
			for (const session of store.all()) {
				const sessionId = session.meta.sessionId;
				if (!(await joinSession(sessionId))) continue; // refused — nothing to catch up on for this rider

				const after = session.lastSeq >= 0 ? `?after=${session.lastSeq}` : '';
				const resp = await fetch(`/api/recording/sessions/${sessionId}/points${after}`);
				if (!resp.ok) throw new Error('reconnect catch-up ' + resp.status);

				const pts: PointPayload[] = await resp.json();
				if (store.appendAfterReconnect(sessionId, pts)) {
					trackerFor(sessionId).onUpdate(Date.now()); // gap filled after a reconnect — a cadence sample
					paint(session);
					render(session);
				}
			}
		} catch (e) {
			if (retry) {
				setTimeout(() => void rejoinAndCatchUp(false), 2000);
				return;
			}
			console.warn('LiveView reconnect failed', e);
			panel.markStale();
		}
	}

	conn.onreconnected(() => void rejoinAndCatchUp());

	conn
		.start()
		.then(async () => {
			for (const session of store.all()) await joinSession(session.meta.sessionId);
		})
		.catch((err: unknown) => {
			console.error('SignalR error:', err);
			panel.markStale(); // initial connect failed — the badge would otherwise falsely read LIVE
		});

	// ── Late joiners: a friend who goes live after the page loaded ────────
	// Their row appears unticked — never drawn behind the viewer's back.

	async function pollJoiners(): Promise<void> {
		try {
			const resp = await fetch('/api/recording/sessions/live');
			if (!resp.ok) return;

			const metas: LiveSessionMeta[] = await resp.json();
			for (const meta of metas) {
				if (store.get(meta.sessionId)) continue;

				const session = store.add(meta);
				if (!session) return; // watch limit reached — ignore the rest

				store.setVisible(meta.sessionId, false);
				panel.addRow(session);
				if (await joinSession(meta.sessionId)) void loadCatchup(meta.sessionId);
			}
		} catch {
			// transient — the next tick tries again
		}
	}

	// PERF-3: same background-tab guard as the clock above — no joiner poll until the tab is looked at.
	joinerTimer = document.hidden ? null : window.setInterval(() => { void pollJoiners(); }, JOINER_POLL_MS);

	// PERF-3: neither timer earns its keep in a hidden tab — the repaint paints nothing anyone sees, and
	// the joiner poll asks the server about riders nobody is looking at. The public share twin already
	// pauses (its perf-4); this mirrors it. SignalR stays connected throughout, so live points keep
	// arriving and the rows are correct the moment the tab is visible again — only DISCOVERY of a new
	// joiner waits for the return, which is the accepted trade.
	function onVisibility(): void {
		if (disposed) return;
		if (document.hidden) {
			if (clockTimer !== null) { clearInterval(clockTimer); clockTimer = null; }
			if (joinerTimer !== null) { clearInterval(joinerTimer); joinerTimer = null; }
			return;
		}
		if (clockTimer === null) { tickClock(); clockTimer = window.setInterval(tickClock, 1000); }
		if (joinerTimer === null) {
			void pollJoiners(); // catch up at once on whoever went live while the tab was away
			joinerTimer = window.setInterval(() => { void pollJoiners(); }, JOINER_POLL_MS);
		}
	}
	document.addEventListener('visibilitychange', onVisibility);

	return dispose;
}
