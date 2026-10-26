// share-live-view.ts — PUBLIC (anonymous) live-recording map: /s/{token}, kind = "live".
//
// A slimmed, single-rider cousin of live-recording-liveview.ts. There is no SignalR here (the hub is
// real-user-only by design) — the anonymous viewer POLLS the token-scoped share endpoint and drives the
// same shared MapLibre live-map engine (live-recording-map.ts). "Map + minimal stats": a LIVE badge, the
// owner's display name (server-rendered), an elapsed clock and the activity type.

import { FOLLOW_ZOOM, liveRecordingMap } from './live-recording-map';
import { pollUntil } from '../core/poll-until';
import { formatDuration, type PointPayload } from './live-recording-shared';
import { createActiveElapsed } from './live-elapsed';
import { createCadenceTracker, cadenceBadge } from './live-cadence-state';
import { renderFollowToggleLabel } from '../core/map-utils-shared';
import { setupMapToolbar } from '../core/map-toolbar';
import { readIsland } from '../core/page-bootstrap';
import type { ShareLiveSeedDto, ShareLiveUpdateDto } from '../generated/contracts';

/** The static seed the server embeds — generated from C# ShareLiveSeedDto (island schema, D27). */
type ShareLiveSeed = ShareLiveSeedDto;

/** One poll response — generated from C# ShareLiveUpdateDto. */
type ShareLiveUpdate = ShareLiveUpdateDto;

/** The one rider's track colour. */
const TRACK_COLOR = '#14b8a6';
/** How often to poll for new points + status while the recording is live. */
const POLL_MS = 4000;

/** Active = still producing points; anything else is a frozen, finished recording. */
function isActive(status: string): boolean {
	return status === 'recording' || status === 'paused';
}

export function initShareLiveView(): () => void {
	const rawSeed = readIsland<ShareLiveSeed | null>('lv-share-data', null);
	if (!rawSeed) return () => { /* nothing acquired */ };
	// Non-null alias: the payload is present past this guard, so downstream closures (incl. the hoisted
	// poll/finish handlers TS won't narrow a `| null` const into) see a definite ShareLiveSeed.
	const seed: ShareLiveSeed = rawSeed;

	const typeEl = document.getElementById('lv-type');
	if (typeEl) typeEl.textContent = seed.activityType || '—';
	const elapsedEl = document.getElementById('lv-elapsed');
	const badgeEl = document.getElementById('lv-status-badge');
	const liveHint = document.getElementById('lv-live-hint');
	const finishedBanner = document.getElementById('lv-share-finished');
	const followToggle = document.getElementById('lv-share-follow-toggle');
	const mapError = document.getElementById('lv-map-error');
	const mapRetryBtn = document.getElementById('lv-map-retry-btn');
	const connProblem = document.getElementById('lv-conn-problem');

	const startedAt = new Date(seed.startedAt);
	// Elapsed = active riding time: ticks while recording, freezes while paused, holds a final value once
	// ended. Shared with the authenticated LiveView (live-elapsed.ts) so the two views never disagree (FE-08).
	const elapsed = createActiveElapsed(startedAt.getTime());

	// ── Map (shared engine) ──────────────────────────────────────────────
	// Reset must return to the height the page loaded with — keep in step with the
	// .cockpit-shell--share desktop rule in liveview.css (@media min-width:901, viewport minus ~340px chrome, min 600).
	const defaultMapHeight = window.innerWidth >= 901 ? Math.max(600, window.innerHeight - 340) : 600;
	// Held so the FE-07 retry path can re-init the engine with the exact same config after a stall.
	const mapInitOpts = {
		containerId: 'liveview-map',
		styleSwitcherId: 'lv-style-switcher',
		sizeControls: {
			mapDivId: 'liveview-map',
			growBtnId: 'lv-map-grow-btn', shrinkBtnId: 'lv-map-shrink-btn',
			resetBtnId: 'lv-map-reset-btn', defaultHeight: defaultMapHeight,
			fullscreenBtnId: 'lv-map-fullscreen-btn', fullscreenCardId: 'lv-map-card',
		},
	};
	liveRecordingMap.init(mapInitOpts);

	// Style/Size <details> shell — same toolbar as the owner LiveView and the segment/race pages.
	const teardownToolbar = setupMapToolbar();

	const coords: [number, number][] = [];
	let lastSeq = -1;
	let mapReady = false;
	let followEngaged = false;
	let finished = false;
	let disposed = false;
	// Camera follows the moving position while live; the viewer can toggle it off to pan freely.
	let following = isActive(seed.status);
	// The initial whole-history fetch arrives at page-open time, which says nothing about the phone's
	// UPLOAD cadence — only deltas after it are cadence samples (FE-03; see live-cadence-state.ts header).
	let historyLoaded = false;
	let consecutive404s = 0;

	// ── Cadence badge ────────────────────────────────────────────────────
	// The badge stays "Warming up" until two updates arrive close together — a single fresh point can't
	// tell fast cadence from "you tuned in just as a 60 s idle upload landed". See live-cadence-state.ts.
	const cadence = createCadenceTracker();
	let currentStatus = seed.status;
	// The badge is a role="status" live region (_ShareLiveBody.cshtml), but the 1 s clock re-runs
	// paintBadge every tick — rewriting identical innerHTML would make a screen reader re-announce the
	// same state every second. Only touch the DOM when the rendered state actually changes.
	let lastBadgeSig = '';

	function paintBadge(): void {
		if (!badgeEl) return;
		const b = cadenceBadge(cadence.state(currentStatus, Date.now()));
		const sig = `${b.cls}|${b.text}|${b.title}|${b.ramping}|${b.live}`;
		if (sig === lastBadgeSig) return;
		lastBadgeSig = sig;
		badgeEl.className = 'lv-status-badge lv-status-badge--' + b.cls;
		badgeEl.title = b.title;
		// text is a fixed label from our own map — safe to inject; the dot only rides the confirmed-live state.
		// eslint-disable-next-line no-unsanitized/property -- both parts are literals from cadenceBadge's own label map
		badgeEl.innerHTML = (b.live ? '<span class="lv-live-dot" aria-hidden="true"></span> ' : '') + b.text;
		// The plain-language "getting live" line shows through the ramp only; tooltips don't exist on phones.
		if (liveHint) liveHint.hidden = !b.ramping;
	}
	paintBadge(); // replace the server-rendered "LIVE" placeholder with the real initial state at once

	function paint(): void {
		if (!mapReady || coords.length === 0) return;
		liveRecordingMap.updateTrack('live', coords);

		// First fix flies the camera in; it then follows the moving position unless the viewer turned that off.
		if (!followEngaged) {
			followEngaged = true;
			const last = coords[coords.length - 1];
			if (last === undefined) return;
			const [lng, lat] = last;
			liveRecordingMap.flyToFirstPoint(lat, lng);
			if (following && !finished) liveRecordingMap.setFollowTarget('live', { zoom: FOLLOW_ZOOM });
		}
	}

	// ── Follow / unfollow toggle ─────────────────────────────────────────
	function syncFollowToggle(): void {
		if (!followToggle) return;
		renderFollowToggleLabel(followToggle, following); // "(unfollow)"/"(follow)" + is-following, shared with the replays
		followToggle.title = following ? 'Free camera — stop following' : 'Follow the moving position';
	}
	followToggle?.addEventListener('click', () => {
		following = !following;
		liveRecordingMap.setFollowTarget(following ? 'live' : null, following ? { zoom: FOLLOW_ZOOM } : undefined);
		syncFollowToggle();
	});
	syncFollowToggle();

	function ingest(points: PointPayload[]): void {
		let added = false;
		for (const p of points) {
			if (p.seq <= lastSeq) continue; // dedup on the dense server ordinal, never ts (LV-01)
			coords.push([p.lng, p.lat]);
			lastSeq = p.seq;
			added = true;
		}
		if (added) {
			// Only post-catch-up deltas are cadence samples: counting the catch-up let an idle upload
			// landing ≤15 s after page open fake the confirmed-fast LIVE state (FE-03).
			if (historyLoaded) cadence.onUpdate(Date.now());
			paint();
		}
	}

	// ── Elapsed clock ────────────────────────────────────────────────────
	// Live → tick every second. Already finished on load (a late opener) → show the real recorded
	// duration once (stopped − started) and never tick.
	let clockTimer: number | null = null;
	// Route through the active-riding accumulator, keyed on the live status: it advances while recording
	// and FREEZES while paused instead of ticking straight through pauses (FE-08).
	function renderElapsed(toMs: number): void {
		if (elapsedEl) elapsedEl.textContent = formatDuration(elapsed.sample(currentStatus, toMs));
	}
	// One timer drives both the clock and the badge: the badge's warming→live/→waiting edges are
	// time-based, so it has to be re-evaluated every second, not only when points arrive. Extracted so the
	// hidden-tab guard (perf-4) can stop and restart it.
	function startClock(): void {
		if (clockTimer !== null) return;
		clockTimer = window.setInterval(() => { renderElapsed(Date.now()); paintBadge(); }, 1000);
	}
	if (isActive(seed.status)) {
		renderElapsed(Date.now());
		if (!document.hidden) startClock(); // a tab opened in the background stays paused until it's viewed (perf-4)
	} else {
		// Already finished at page load (a late opener): show the real recorded duration, no ticking. The
		// first poll below still fetches the frozen final track, then flips the banner via markFinished().
		renderElapsed(seed.stoppedAt ? new Date(seed.stoppedAt).getTime() : Date.now());
	}

	// ── Map readiness (bounded — a dead CDN/WebGL must not spin forever) ──
	// The map is this page's ONLY content, so a stall must NOT fail silently (FE-07). After a generous
	// wait we surface a visible error + a Retry that re-inits the engine, instead of leaving a blank page.
	const MAP_READY_MAX_TICKS = 600; // ×50 ms = 30 s (was 10 s — too tight on a cold CDN/slow device)

	function showMapError(): void {
		console.warn('Share live map never became ready');
		if (mapError) mapError.hidden = false;
	}

	function waitForMap(): void {
		void pollUntil(
			() => {
				if (!liveRecordingMap.isReady()) return false;
				mapReady = true;
				liveRecordingMap.addPerson('live', TRACK_COLOR, TRACK_COLOR);
				paint(); // draw whatever catch-up already landed
				return true;
			},
			{ intervalMs: 50, maxTicks: MAP_READY_MAX_TICKS, onTimeout: showMapError },
		);
	}

	function retryMap(): void {
		if (mapReady || disposed) return;
		if (mapError) mapError.hidden = true;
		liveRecordingMap.destroy(); // drop the stalled shell before rebuilding it
		// The rebuilt engine has no follow target; only the first-paint block flies the camera in and
		// re-engages follow, so it must run again — else the map returns with a dead camera while the
		// toggle still says "(unfollow)".
		followEngaged = false;
		liveRecordingMap.init(mapInitOpts);
		waitForMap();
	}
	mapRetryBtn?.addEventListener('click', retryMap);
	waitForMap();

	// ── Finish handling ──────────────────────────────────────────────────
	function markFinished(): void {
		if (finished) return;
		finished = true;
		if (clockTimer !== null) { clearInterval(clockTimer); clockTimer = null; }
		// Freeze at the real stop time: seed.stoppedAt for a late opener; ~now for a live watcher who saw
		// the transition (its seed had no stoppedAt).
		renderElapsed(seed.stoppedAt ? new Date(seed.stoppedAt).getTime() : Date.now());
		paintBadge(); // currentStatus is terminal here → renders ENDED and drops the live dot
		if (finishedBanner) finishedBanner.style.display = '';
		// Release the follow camera so the viewer can pan/zoom the whole frozen track; the toggle is moot now.
		following = false;
		if (mapReady) liveRecordingMap.setFollowTarget(null);
		if (followToggle) followToggle.style.display = 'none';
	}

	// ── Poll loop (sequential, non-overlapping; stops on finish, link death or teardown) ──
	// The FIRST fetch always runs (even for an already-finished recording — it loads the frozen final
	// track); only the RECURRING poll stops once finished.
	//
	// A revoked/expired link 404s forever BY DESIGN (indistinguishable from a wrong URL) — and so does a
	// rate-limiter rejection, so one 404 proves nothing. A solid minute of them does (FE-01): stop polling
	// and say so, instead of decaying to WAITING/"lost signal" while an abandoned tab drains the token
	// bucket indefinitely.
	const MAX_CONSECUTIVE_404S = 15;

	// FUX-06: a run of non-404 failures (a 5xx or a network drop) is recoverable — unlike the 404
	// give-up (markUnavailable), which declares the link dead. Surface a quiet "retrying" note after a
	// few in a row, cleared on the next good poll, so a persistent server/connection fault isn't silent.
	const MAX_FAILURES_BEFORE_NOTICE = 3; // ~12 s at POLL_MS — a single blip clears before this
	let consecutiveFailures = 0;

	function noteConnectionOk(): void {
		consecutiveFailures = 0;
		if (connProblem) connProblem.hidden = true;
	}
	function noteConnectionFailure(): void {
		if (++consecutiveFailures >= MAX_FAILURES_BEFORE_NOTICE && connProblem) connProblem.hidden = false;
	}

	function markUnavailable(): void {
		if (finished) return;
		finished = true;
		if (clockTimer !== null) { clearInterval(clockTimer); clockTimer = null; }
		if (badgeEl) {
			badgeEl.className = 'lv-status-badge lv-status-badge--ended';
			badgeEl.textContent = 'UNAVAILABLE';
			badgeEl.title = 'This share link is no longer available.';
		}
		if (liveHint) liveHint.hidden = true; // paints the badge directly, bypassing paintBadge's toggle
		if (connProblem) connProblem.hidden = true; // the link is dead, not merely flaky
		if (mapReady) liveRecordingMap.setFollowTarget(null);
		if (followToggle) followToggle.style.display = 'none';
	}

	// Paused entirely while the tab is hidden — an anonymous, unbounded viewer must not keep draining the
	// token bucket from a backgrounded tab (perf-4). visibilitychange resumes it; `polling` guards the
	// resume kick against overlapping an in-flight fetch.
	let pollTimer: number | null = null;
	let polling = false;

	function scheduleNextPoll(): void {
		if (disposed || finished || document.hidden) return;
		pollTimer = window.setTimeout(() => { pollTimer = null; void poll(); }, POLL_MS);
	}

	async function poll(): Promise<void> {
		if (disposed || document.hidden || polling) return;
		polling = true;
		try {
			const r = await fetch(`${seed.apiBase}/live?after=${lastSeq}`);
			if (r.ok) {
				consecutive404s = 0;
				noteConnectionOk();
				const update: ShareLiveUpdate = await r.json();
				currentStatus = update.status;
				if (update.status === 'finalized') {
					// A finalized response carries the authoritative downsampled final track with SYNTHETIC
					// seqs (blob indexes, not staging seqs) — replace wholesale, never seq-dedup-append, so a
					// watcher whose stop+finalize landed inside one poll gap still gets the finish.
					if (update.points?.length) {
						coords.length = 0;
						for (const p of update.points) coords.push([p.lng, p.lat]);
						paint();
					}
					markFinished();
					return;
				}
				ingest(update.points ?? []);
				historyLoaded = true;
				if (!isActive(update.status)) { markFinished(); return; }
				paintBadge(); // reflect status (e.g. paused) + any cadence change right away, not on the next tick
			} else if (r.status === 404) {
				if (++consecutive404s >= MAX_CONSECUTIVE_404S) { markUnavailable(); return; }
			} else {
				noteConnectionFailure(); // a 5xx etc. — recoverable, surfaced after a few in a row
			}
		} catch {
			noteConnectionFailure(); // network drop — the next tick retries; say so if it persists
		} finally {
			polling = false;
		}
		scheduleNextPoll();
	}
	// Kick off immediately (after=-1 → whole history, live tail or frozen final track).
	void poll();

	// perf-4: stop polling + ticking while the tab is hidden; resume (if still live) when it returns.
	function onVisibility(): void {
		if (document.hidden) {
			if (clockTimer !== null) { clearInterval(clockTimer); clockTimer = null; }
			return; // the poll chain stops itself — scheduleNextPoll + the top-of-poll guard both no-op while hidden
		}
		if (disposed || finished) return;
		if (isActive(currentStatus)) { renderElapsed(Date.now()); paintBadge(); startClock(); }
		if (pollTimer === null && !polling) void poll(); // the chain died while hidden — restart it
	}
	document.addEventListener('visibilitychange', onVisibility);

	// ── Teardown ─────────────────────────────────────────────────────────
	function dispose(): void {
		if (disposed) return;
		disposed = true;
		if (clockTimer !== null) clearInterval(clockTimer);
		if (pollTimer !== null) { clearTimeout(pollTimer); pollTimer = null; }
		document.removeEventListener('visibilitychange', onVisibility);
		teardownToolbar();
		liveRecordingMap.destroy();
	}
	return dispose;
}

document.addEventListener('DOMContentLoaded', () => {
	if (!document.getElementById('share-live-app')) return;
	const dispose = initShareLiveView();
	// pagehide fires on bfcache ENTRY too (persisted=true); disposing there destroys the map shell —
	// including its own pageshow recovery listener — so Back restored a blank, dead page (FE-05).
	// Only a real unload disposes; dispose() is idempotent.
	window.addEventListener('pagehide', (e) => { if (!e.persisted) dispose(); });
});
