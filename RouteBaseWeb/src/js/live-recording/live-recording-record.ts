// live-recording-record.ts — Record page: navigator.geolocation → batched POSTs to the recording API.

import { asyncCallback } from '../core/async-listener';
import { readData, readDataInt, setData } from '../core/data-attrs';
import { haversineDistance } from '../core/geo';
import { createUploader, sanitizePoint } from './live-recording-uploader';
import type { RecordableActivityType } from '../activity/activity-type-vocabulary';
import { startElapsedTimer } from './live-recording-shared';
import { MAX_WATCHABLE } from './live-view-sessions';
import type { AppendPointsResponse, ApiError, RecordingSessionDto } from '../generated/contracts';

/**
 * FUX-06: run a session row action (abandon / retry-finalize / delete) — reload on success, otherwise
 * re-enable the button AND say what failed. These used to swallow every failure into a silent
 * button-re-enable, so a server error looked identical to "nothing happened". `btn` is disabled by the
 * caller before its confirm/teardown; this only re-enables on the failure path.
 */
async function runSessionAction(btn: HTMLButtonElement, url: string, method: string, failMsg: string): Promise<void> {
	try {
		const resp = await fetch(url, { method });
		if (resp.ok) { window.location.reload(); return; } // reload so the table + start form both refresh
	} catch {
		// fall through to the shared failure path (network error)
	}
	btn.disabled = false;
	alert(failMsg);
}

/**
 * "Live Now" picker: tick riders (or Select all) and open them together on one LiveView map.
 * The per-row "Watch Live" links still open a single rider, unchanged.
 */
function initLivePicker(): void {
	const selectAll0 = document.getElementById('rec-live-select-all') as HTMLInputElement | null;
	const watchBtn0 = document.getElementById('rec-watch-selected') as HTMLButtonElement | null;
	if (!selectAll0 || !watchBtn0) return; // nobody is live
	// Narrowed rebinds (G3): the closures below outlive the guard.
	const selectAll = selectAll0, watchBtn = watchBtn0;

	const picks = Array.from(document.querySelectorAll<HTMLInputElement>('.rec-live-pick'));

	function selected(): string[] {
		return picks.filter((p) => p.checked).map((p) => p.value).slice(0, MAX_WATCHABLE);
	}

	function syncButton(): void {
		const n = selected().length;
		watchBtn.disabled = n === 0;
		selectAll.checked = n > 0 && n === picks.length;
	}

	for (const pick of picks) pick.addEventListener('change', syncButton);

	selectAll.addEventListener('change', () => {
		// The map caps at MAX_WATCHABLE riders, so "select all" ticks no more than that.
		picks.forEach((p, i) => { p.checked = selectAll.checked && i < MAX_WATCHABLE; });
		syncButton();
	});

	watchBtn.addEventListener('click', () => {
		const ids = selected();
		if (ids.length > 0) window.open(`/LiveView?sessions=${ids.join(',')}`, '_blank');
	});

	syncButton();
}

export function initRecordPage(): void {
	initLivePicker();

	const startSection0 = document.getElementById('rec-start');
	const activeSection0 = document.getElementById('rec-active');
	const startBtn = document.getElementById('rec-start-btn') as HTMLButtonElement;
	const stopBtn = document.getElementById('rec-stop-btn') as HTMLButtonElement;
	const startError0 = document.getElementById('rec-start-error');
	const gpsText0 = document.getElementById('rec-gps-text');
	const elapsedEl0 = document.getElementById('rec-elapsed');
	const distanceEl0 = document.getElementById('rec-distance');
	const speedEl0 = document.getElementById('rec-speed');
	const pointsEl0 = document.getElementById('rec-points');
	const shareDiv0 = document.getElementById('rec-share-link');
	const shareUrl = document.getElementById('rec-share-url') as HTMLAnchorElement;
	const stoppedBanner0 = document.getElementById('rec-stopped-banner');
	const activityLink = document.getElementById('rec-activity-link') as HTMLAnchorElement;
	if (!startSection0 || !activeSection0 || !startError0 || !gpsText0 || !elapsedEl0
		|| !distanceEl0 || !speedEl0 || !pointsEl0 || !shareDiv0 || !stoppedBanner0) return;
	// Narrowed rebinds (G3): control-flow narrowing does not cross into the closures below, and the
	// alternative was a `!` on every later use. All non-null from here on.
	const startSection = startSection0, activeSection = activeSection0, startError = startError0;
	const gpsText = gpsText0, elapsedEl = elapsedEl0, distanceEl = distanceEl0, speedEl = speedEl0;
	const pointsEl = pointsEl0, shareDiv = shareDiv0, stoppedBanner = stoppedBanner0;

	// The GPS/status line is an aria-live region (role set here, not in Record.cshtml). onPosition rewrites
	// it with fresh coordinates on every GPS fix (~1 Hz), so a naive live region would read the coordinates
	// aloud every second. Route every write through a change-guard keyed on MEANING, not the raw text: the
	// per-fix coordinate readout shares one stable 'fix' key, so a run of fixes neither re-announces nor
	// rewrites the node; each distinct status/error message announces once. Mirrors share-live-view.ts's
	// lastBadgeSig — only paint when the meaning changed.
	gpsText.setAttribute('role', 'status');
	let lastGpsKey = '';
	function setGpsStatus(text: string, key: string = text): void {
		if (key === lastGpsKey) return;
		lastGpsKey = key;
		gpsText.textContent = text;
	}

	// Friends' live-session start times (data-started-at is UTC-stamped ISO) → local "since HH:mm".
	document.querySelectorAll<HTMLElement>('.rec-friend-time[data-started-at]').forEach((el) => {
		const iso = readData(el, 'startedAt');
		if (iso) el.textContent = 'since ' + new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
	});

	// State
	let sessionId: string | null = readData(activeSection, 'sessionId') || null;
	const trackIdAttr = readData(activeSection, 'trackId');
	let trackId: number | null = trackIdAttr ? parseInt(trackIdAttr) : null;
	const startedAtAttr = readData(activeSection, 'startedAt');
	let startedAt: Date | null = startedAtAttr ? new Date(startedAtAttr) : null;
	let totalDistanceM = 0;
	// Declared with the rest of the state, not next to armUnloadGuard: showRemoteStopped and
	// activateRecordingView both touch it and are defined far above that block (TDZ, not style).
	let unloadGuardArmed = false;
	let pointCount = readDataInt(activeSection, 'pointCount', 0);
	let gpsWatchId: number | null = null;
	let batchTimerId: number | null = null;
	let elapsedTimerId: number | null = null;
	let statusPollTimerId: number | null = null;
	let lastLat: number | null = null;
	let lastLng: number | null = null;

	const wait = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms));

	// ── Buffered uploader ────────────────────────────────────────────────
	// Owns the GPS buffer, the frozen in-flight batch, the client sequence, and all failure handling
	// (400 poison/oversize, 404 session-inactive, 429/network retry, dedup `inserted:0` resync via the
	// AQ-S1 lastSequence contract). Extracted to live-recording-uploader.ts for unit testing (LR-01..04).
	const uploader = createUploader({
		post: async (seq, points) => {
			try {
				const resp = await fetch(`/api/recording/sessions/${sessionId}/points`, {
					method: 'POST',
					headers: { 'Content-Type': 'application/json' },
					body: JSON.stringify({ clientSequence: seq, points }),
				});
				if (resp.ok) {
					// Mirrors AppendPointsResponse.cs. `watched` (is anyone on the live view?) is read only by
					// the Android recorder, which slows its upload cadence when nobody is — the browser
					// recorder's own tab is a viewer, so it always uploads at its normal rate.
					const d: AppendPointsResponse = await resp.json();
					return { ok: true, status: resp.status, inserted: d.inserted, lastSequence: d.lastSequence };
				}
				const e = (await resp.json().catch(() => ({}))) as Partial<ApiError>;
				return { ok: false, status: resp.status, errorText: e.message };
			} catch {
				return { ok: false, status: 0 }; // network hiccup → uploader keeps the frozen batch, retries next tick
			}
		},
		onInserted: (n) => {
			pointCount += n;
			pointsEl.textContent = String(pointCount);
		},
		onStatus: (msg) => {
			setGpsStatus(msg);
		},
		onSessionInactive: () => {
			void handleSessionInactive();
		},
		initialSequence: readDataInt(activeSection, 'lastSequence', 0),
	});

	// ── Remote-stop / session-inactive handling ──────────────────────────
	// Reuses the prebuilt stopped-banner + rec-activity-link exactly like the local-stop path (no
	// string-built markup — LR-10); hides the share link since the session is over.
	function showRemoteStopped(tid: number | null): void {
		stopRecordingInternals();
		disarmUnloadGuard(); // the ride is over — no warning to give, and the page can be cached again
		stopBtn.style.display = 'none';
		shareDiv.style.display = 'none';
		stoppedBanner.style.display = '';
		if (tid) {
			activityLink.href = `/Activities/Details/${tid}`;
			activityLink.style.display = '';
		}
	}

	// Called when an append 404s (session paused/stopped elsewhere) — resolves the real state and
	// stops the retry loop rather than appending into a 404 forever (LR-01).
	async function handleSessionInactive(): Promise<void> {
		if (!sessionId) return;
		try {
			const resp = await fetch(`/api/recording/sessions/${sessionId}`);
			if (!resp.ok) return;
			const data: RecordingSessionDto = await resp.json();
			if (data.status === 'recording') {
				// The pause that 404'd our append was resumed remotely before this probe landed (FE-06).
				// Nothing local was torn down on this path — the GPS watch is still buffering — so only
				// the halted uploader needs re-arming, or nothing uploads again until Stop.
				uploader.resume();
			} else if (data.status === 'paused') {
				pauseCaptureKeepPolling();
				setGpsStatus('Recording paused elsewhere — uploads stopped.');
			} else {
				showRemoteStopped(data.trackId);
			}
		} catch {
			// network hiccup — leave the uploader halted; the 30 s status poll will catch up on reconnect
		}
	}

	// ── Freeze local capture on a remote pause, but KEEP the 30 s status poll ──
	// The poll is the only thing that can notice the later remote resume/stop; tearing it down with the
	// rest (the old behavior) left the page permanently deaf after a remote pause (FE-06).
	function pauseCaptureKeepPolling(): void {
		if (gpsWatchId != null) { navigator.geolocation.clearWatch(gpsWatchId); gpsWatchId = null; }
		if (batchTimerId != null) { clearInterval(batchTimerId); batchTimerId = null; }
		if (elapsedTimerId != null) { clearInterval(elapsedTimerId); elapsedTimerId = null; }
	}

	// ── Tear down GPS + timers without touching the DOM ──────────────────
	function stopRecordingInternals(): void {
		pauseCaptureKeepPolling();
		if (statusPollTimerId != null) { clearInterval(statusPollTimerId); statusPollTimerId = null; }
	}

	// ── Poll the server every 30 s to detect remote stops/resumes (e.g. via website while APK records) ──
	// Split out so a bfcache restore can run ONE check at once instead of waiting up to 30 s (PERF-5):
	// while the page was frozen the ride may have been stopped or resumed from the phone.
	async function checkSessionStatusOnce(sid: string): Promise<void> {
		try {
			const resp = await fetch(`/api/recording/sessions/${sid}`);
			if (!resp.ok) return;
			const data: RecordingSessionDto = await resp.json();
			if (data.status !== 'recording' && data.status !== 'paused') {
				showRemoteStopped(data.trackId);
			} else if (data.status === 'recording' && gpsWatchId == null) {
				// Remotely resumed after a remote pause (FE-06): re-arm capture. startCapture() is
				// restartable by design and clears the uploader halt.
				if (elapsedTimerId == null && startedAt) elapsedTimerId = startElapsedTimer(startedAt, elapsedEl);
				setGpsStatus('Recording resumed elsewhere — uploads restarted.');
				startCapture();
			}
		} catch {
			// network hiccup — keep polling
		}
	}

	function startStatusPoll(sid: string): void {
		statusPollTimerId = window.setInterval(() => { void checkSessionStatusOnce(sid); }, 30_000);
	}

	function activateRecordingView(sid: string, tid: number, started: Date): void {
		sessionId = sid;
		armUnloadGuard(); // a live session is the only reason to hold the page out of bfcache (PERF-5)
		trackId = tid;
		startedAt = started;
		startSection.style.display = 'none';
		activeSection.style.display = '';
		setData(activeSection, 'sessionId', sid);
		setData(activeSection, 'trackId', String(tid));
		setData(activeSection, 'startedAt', started.toISOString());
		// Share link
		const liveUrl = `/LiveView/${sid}`;
		shareUrl.href = liveUrl;
		shareUrl.textContent = window.location.origin + liveUrl;
		shareDiv.style.display = '';

		// Elapsed timer
		elapsedTimerId = startElapsedTimer(started, elapsedEl);

		// Remote-stop detection
		startStatusPoll(sid);

		// Start geolocation + batch timer
		startCapture();
	}

	// ── GPS capture (watchPosition + 5 s batch flush) — restartable so a cancelled Stop re-arms it ──
	function onPosition(pos: GeolocationPosition): void {
		// Sanitize before touching distance or the buffer: a dropped point must not advance distance,
		// and clamped altitude/speed/timestamp defend the server validator (LR-01).
		const point = sanitizePoint(
			{ lat: pos.coords.latitude, lng: pos.coords.longitude, altitude: pos.coords.altitude, speed: pos.coords.speed, timestamp: pos.timestamp },
			Date.now(),
		);
		if (!point) return;

		if (point.speed != null) speedEl.textContent = (point.speed * 3.6).toFixed(1) + ' km/h';
		// Stable 'fix' key: a run of fixes updates distance/speed/points but must not re-announce the line
		// (item 3). The next non-fix status message re-arms it, so a fresh position shows after each event.
		setGpsStatus(`GPS fix: ${point.lat.toFixed(5)}, ${point.lng.toFixed(5)}`, 'fix');

		if (lastLat != null && lastLng != null) {
			totalDistanceM += haversineDistance(lastLat, lastLng, point.lat, point.lng);
			distanceEl.textContent = (totalDistanceM / 1000).toFixed(2) + ' km';
		}
		lastLat = point.lat;
		lastLng = point.lng;

		uploader.enqueue(point);
	}

	function startCapture(): void {
		uploader.resume(); // clear any halt from a prior 404
		// Idempotent re-arm: clear any live watch/timer FIRST, so overlapping re-arms can never stack a
		// second GPS watch + batch timer. The race: Stop nulls both, then awaits the drain (~5 s), during
		// which the 30 s status poll can fire, see status==='recording' && gpsWatchId==null and re-arm —
		// and then a declined data-loss confirm re-arms again, leaking the poll's watch. Two watches feed
		// one uploader → double-counted distance and double upload/battery for the rest of the ride.
		if (gpsWatchId != null) { navigator.geolocation.clearWatch(gpsWatchId); gpsWatchId = null; }
		if (batchTimerId != null) { clearInterval(batchTimerId); batchTimerId = null; }
		if (!navigator.geolocation) {
			setGpsStatus('Geolocation not available in this browser.');
			return;
		}
		gpsWatchId = navigator.geolocation.watchPosition(
			onPosition,
			(err) => { setGpsStatus(`GPS error: ${err.message}`); },
			{ enableHighAccuracy: true, maximumAge: 0 },
		);
		// Batch POST every 5 seconds. A failed send keeps its points frozen for the next tick.
		batchTimerId = window.setInterval(() => {
			void uploader.flush();
		}, 5000);
	}

	// ── Start button handler ──────────────────────────────────────────────

	if (startBtn) {
		startBtn.addEventListener('click', asyncCallback('start recording', async () => {
			startBtn.disabled = true;
			startError.style.display = 'none';

			// rec-activity-type <option>s are server-rendered from ActivityTypeVocabulary.Recordable.
			const activityType = (document.getElementById('rec-activity-type') as HTMLSelectElement).value as RecordableActivityType;
			const trackName = (document.getElementById('rec-track-name') as HTMLInputElement).value.trim() || 'Live Recording';
			const visibility = (document.getElementById('rec-visibility') as HTMLSelectElement).value;

			try {
				const resp = await fetch('/api/recording/sessions', {
					method: 'POST',
					headers: { 'Content-Type': 'application/json' },
					body: JSON.stringify({ activityType, trackName, visibility, clientVersion: 'web-1.0' }),
				});
				if (!resp.ok) {
					startError.textContent = `Failed to start (${resp.status})`;
					startError.style.display = '';
					startBtn.disabled = false;
					return;
				}
				const data: RecordingSessionDto = await resp.json();
				activateRecordingView(data.sessionId, data.trackId, new Date(data.startedAt));
			} catch (err) {
				startError.textContent = `Error: ${err}`;
				startError.style.display = '';
				startBtn.disabled = false;
			}
		}));
	}

	// ── Stop button handler ───────────────────────────────────────────────

	if (stopBtn) {
		stopBtn.addEventListener('click', asyncCallback('stop recording', async () => {
			if (!sessionId) return;
			stopBtn.disabled = true;

			// Tear down inputs first so no new points arrive mid-drain (keep the elapsed timer for UX).
			if (gpsWatchId != null) { navigator.geolocation.clearWatch(gpsWatchId); gpsWatchId = null; }
			if (batchTimerId != null) { clearInterval(batchTimerId); batchTimerId = null; }

			// Drain the buffer + frozen batch, retrying past the 1 s limiter window (LR-02).
			const drained = await uploader.drainForStop(5, 1100, wait);
			if (!drained && uploader.hasUnsent()) {
				if (!confirm(`${uploader.bufferedCount()} GPS points could not be uploaded and will be lost — stop anyway?`)) {
					startCapture(); // re-arm the interval flush + GPS watch and abort the stop
					stopBtn.disabled = false;
					return;
				}
			}

			// try/catch like every sibling handler: an uncaught rejection here wedges the button
			// disabled with GPS + uploads already torn down, while the server session keeps recording.
			let resp: Response | null = null;
			try {
				resp = await fetch(`/api/recording/sessions/${sessionId}/stop`, { method: 'POST' });
			} catch { /* network failure — recover below */ }
			if (resp?.ok) {
				const data: RecordingSessionDto = await resp.json();
				const tid = data.trackId ?? trackId;

				stopRecordingInternals();

				stopBtn.style.display = 'none';
				stoppedBanner.style.display = '';
				if (tid) {
					activityLink.href = `/Activities/Details/${tid}`;
					activityLink.style.display = '';
				}
			} else {
				startCapture(); // re-arm since we tore down inputs
				stopBtn.disabled = false;
				setGpsStatus('Stop failed — check the connection and try again.');
			}
		}));
	}

	// ── Abandon buttons in the "My Sessions" table ────────────────────────

	document.querySelectorAll<HTMLButtonElement>('.session-abandon-btn').forEach((btn) => {
		btn.addEventListener('click', asyncCallback('abandon session', async () => {
			const sid = readData(btn, 'sessionId');
			if (!sid) return;

			const isCurrentSession = (sid === sessionId);
			if (!confirm(isCurrentSession
				? 'Abandon the current recording? All GPS data will be kept but stats will not be computed.'
				: 'Abandon this session? All GPS data will be kept but stats will not be computed.')) {
				return;
			}

			btn.disabled = true;

			if (isCurrentSession) {
				// Tear down inputs, then drain buffered points before abandoning (abandon keeps GPS data
				// by design, so an un-drained tail is the same loss as a stop — LR-02).
				if (gpsWatchId != null) { navigator.geolocation.clearWatch(gpsWatchId); gpsWatchId = null; }
				if (batchTimerId != null) { clearInterval(batchTimerId); batchTimerId = null; }
				await uploader.drainForStop(5, 1100, wait);
				stopRecordingInternals();
			}

			await runSessionAction(btn, `/api/recording/sessions/${sid}/abandon`, 'POST',
				"Couldn't abandon the session. Please try again.");
		}));
	});

	// ── Retry buttons for finalize_failed sessions (RF-02) ────────────────

	document.querySelectorAll<HTMLButtonElement>('.session-retry-btn').forEach((btn) => {
		btn.addEventListener('click', asyncCallback('retry finalize', async () => {
			const sid = readData(btn, 'sessionId');
			if (!sid) return;

			btn.disabled = true;
			// Server re-arms + attempts finalize now; 200 whether it finalized or re-parked. Reload either
			// way so the row reflects the new status (Done or Finalize failed).
			await runSessionAction(btn, `/api/recording/sessions/${sid}/retry-finalize`, 'POST',
				"Couldn't retry finalizing the session. Please try again.");
		}));
	});

	// ── Delete buttons for abandoned sessions ─────────────────────────────

	document.querySelectorAll<HTMLButtonElement>('.session-delete-btn').forEach((btn) => {
		btn.addEventListener('click', asyncCallback('delete session', async () => {
			const sid = readData(btn, 'sessionId');
			if (!sid) return;

			if (!confirm('Permanently delete this abandoned session record? This cannot be undone.')) return;

			btn.disabled = true;
			await runSessionAction(btn, `/api/recording/sessions/${sid}`, 'DELETE',
				"Couldn't delete the session. Please try again.");
		}));
	});

	// ── Unload safety (LR-04): warn on leave + keepalive-flush the tail ──
	// Armed only WHILE a session is live (PERF-5). A registered beforeunload listener disqualifies the
	// page from bfcache for its whole lifetime — even idle, with nothing to warn about — so an idle
	// /Record used to make Back a full reload. The prompt itself is unchanged.
	function onBeforeUnload(e: BeforeUnloadEvent): void {
		if (sessionId && (gpsWatchId != null || uploader.hasUnsent())) {
			e.preventDefault();
			e.returnValue = ''; // legacy browsers require a truthy returnValue to show the prompt
		}
	}

	function armUnloadGuard(): void {
		if (unloadGuardArmed) return;
		window.addEventListener('beforeunload', onBeforeUnload);
		unloadGuardArmed = true;
	}

	function disarmUnloadGuard(): void {
		if (!unloadGuardArmed) return;
		window.removeEventListener('beforeunload', onBeforeUnload);
		unloadGuardArmed = false;
	}

	// bfcache restore (PERF-5): the page comes back with frozen state and a possibly dead GPS watch, and
	// the ride may have been stopped or resumed from the phone while it sat in the cache. Re-sync from
	// the server rather than trusting what is on screen.
	window.addEventListener('pageshow', (e: PageTransitionEvent) => {
		if (!e.persisted) return; // a normal load already built its state from the server-rendered DOM
		if (!sessionId) { disarmUnloadGuard(); return; }
		armUnloadGuard();
		void checkSessionStatusOnce(sessionId);
	});
	// pagehide fires on real navigation and bfcache entry; a resend of the frozen batch is absorbed by
	// the server's dedup, so this is safe to fire unconditionally. keepalive survives page teardown.
	window.addEventListener('pagehide', () => {
		if (!sessionId) return;
		const snap = uploader.snapshotForBeacon(400);
		if (!snap) return;
		try {
			void fetch(`/api/recording/sessions/${sessionId}/points`, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ clientSequence: snap.clientSequence, points: snap.points }),
				keepalive: true,
			}).catch(() => {});
		} catch {
			// page is going away — best effort only
		}
	});

	// ── If page loaded with an active session already, activate immediately ──

	if (sessionId && trackId !== null && startedAt !== null) {
		activateRecordingView(sessionId, trackId, startedAt);
	}
}
