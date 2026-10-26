/**
 * Activity Details - Replay UI Module
 * Drives the MapLibre 3D replay engine (ActivityReplay): controls, info overlay,
 * seek/speed, and the fullscreen exit paths.
 *
 * Supports two replay modes:
 *   - 'distance': smooth scenic flythrough (speed = km/s viewing rate)
 *   - 'time': real-time pace (speed = 1x/2x/10x etc., dynamic options based on duration)
 */

/** Configuration passed to setupReplayControls from the page init module. */
interface ReplayUiConfig {
	totalTimeSeconds: number;
}

import { asyncCallback } from '../core/async-listener';
import { must } from '../core/invariant';
import type { GpsPoint } from '../core/core-types';
import { renderFollowToggleLabel } from '../core/map-utils-shared';
import { createReplayTransport } from '../core/replay-transport';
import { createReplayHistorySession } from '../core/replay-history-session';
import { createActivityReplayProfile, type ActivityReplayProfile } from './activity-replay-profile';
import { buildGpsSpeedScale, type SpeedScale } from '../core/speed-scale';
import { createSpeedLabel } from '../core/speed-label';
import { centredMeanKmh, windowSecondsFor, type DistanceSeries } from '../core/speed-window';
import { createTrailingThrottle } from '../core/trailing-throttle';
import { getGpsData } from '../core/gps-data';
import { getMaplibreMap } from '../core/map-state';
import { registerReplayButtonVisibility, closeAllActionPanels } from './details-shared';
import { ActivityReplay } from './activity-replay';
import { registerPlaybackHold } from '../core/playback-hold';
import { pollUntil } from '../core/poll-until';

export function setupReplayControls(config: ReplayUiConfig): void {
	initReplayControls(config);
}

function initReplayControls(config: ReplayUiConfig): void {
	// Typed as the engine itself — a hand-maintained mirror interface here made every engine API change
	// a two-file edit while the concrete class is constructed right below.
	let activityReplay: ActivityReplay | null = null;
	// RP-01: synchronous re-entrancy guard — set before the fullscreen await, so a second
	// click during that window (or a keyboard re-fire) can't spawn a second engine.
	let replayStarting = false;
	// Shared history session (CR1-05): dummy-entry push, back-button + Esc exits.
	const session = createReplayHistorySession({
		stateKey: "replayActive",
		isActive: () => activityReplay !== null,
		exit: () => exitReplay(),
	});
	const playReplayBtn = document.getElementById("playReplayBtn");
	const replayControls = document.getElementById("replayControls");
	const replayInfo = document.getElementById("replayInfo");
	const replayExit = document.getElementById("replayExit");
	const replayDock = document.getElementById("activityReplayDock");
	/** The elevation strip + its drawer. Lives exactly as long as one replay session. */
	let replayProfile: ActivityReplayProfile | null = null;
	const replayFollowToggle = document.getElementById("replayFollowToggle");
	const mapCard = document.getElementById("mapCard");

	// The live speed number is measured from the DISTANCE channel over a window centred on the
	// playhead (core/speed-window) and only redrawn when it leaves the number shown
	// (core/speed-label). Re-averaging the speed channel stacked a second boxcar on the server's own.
	const speedLabel = createSpeedLabel();
	// Playback multiplier, captured from the transport: it sets how many ride-seconds one reading
	// covers, so the number stays readable at 60x instead of collapsing to a single sample.
	let playbackRate = 1;

	// The transport owns play/pause, the scrubber (rAF-coalesced — the seek activity-replay never had, so
	// dragging the scrubber no longer fires a full updateFrame per input event), the speed −/+ stepper and
	// the elapsed clock. Wired once for the page; the hooks read the live `activityReplay` slot and no-op
	// while no replay is active. An activity scrub seeks by fraction of the track.
	const transport = createReplayTransport(
		{
			playPauseId: "replayPlayPause",
			seekId: "replaySeek",
			currentTimeId: "replayCurrentTime",
			speedSelectId: "replaySpeed",
			speedSlowerId: "replaySpeedSlower",
			speedFasterId: "replaySpeedFaster",
			speedLabelId: "replaySpeedLabel",
			speedPrefKey: "activity",
		},
		{
			seekToPercent: (percent) => {
				activityReplay?.seek(percent); // nothing to reset: the speed is a function of the playhead
			},
			togglePlayPause: () => {
				if (!activityReplay) return false;
				if (activityReplay.isPlaying) { activityReplay.pause(); return false; }
				activityReplay.resume();
				return true;
			},
			// The transport routes the ladder's initial default through here too, so this is the one
			// place that sees every rate change — the engine exposes no getter.
			setSpeed: (mult) => { playbackRate = mult; activityReplay?.setSpeed(mult); },
		},
	);

	// Info display elements — cached once to avoid per-frame getElementById calls
	const replayInfoDistance = document.getElementById("replayInfoDistance");
	const replayInfoSpeed = document.getElementById("replayInfoSpeed");
	const replayInfoSpeedBar = document.getElementById("replayInfoSpeedBar");
	const replayInfoAltitude = document.getElementById("replayInfoAltitude");
	const replayInfoHR = document.getElementById("replayInfoHR");
	const replayInfoCompletion = document.getElementById("replayInfoCompletion"); // RP-09: cached, not per-frame
	// Last speed string written, so a steady rider costs no DOM write at all.
	let lastSpeedText: string | null = null;

	// The info panel's 5 Hz repaint. The latest frame's arguments are held in plain slots rather than
	// an object so the 60 Hz producer allocates nothing; the throttle reads whatever landed last.
	let infoPoint: GpsPoint | null = null;
	let infoIndex = 0;
	let infoRideSeconds = 0;
	const infoThrottle = createTrailingThrottle(1000 / 5, () => {
		if (infoPoint) updateReplayInfo(infoPoint, infoIndex, infoRideSeconds);
	});

	// Fixed speed→width mapping for this activity (time-weighted CDF). Built lazily
	// on first use from the activity's GPS track so the bar stays comparable across
	// the whole replay instead of tracking a drifting max.
	let speedScale: SpeedScale | null = null;
	function getSpeedScale(): SpeedScale {
		if (speedScale) return speedScale;
		speedScale = buildGpsSpeedScale(getGpsData());
		return speedScale;
	}

	// (ride seconds → cumulative km) for the speed window, memoised like the scale above. GpsPoint
	// distance is ALREADY km and monotonic — normalizeTrackDistances settles both before replay —
	// and _cumulativeTime is written onto the track by replay-engine-base.
	let distanceSeries: DistanceSeries | null = null;
	function getDistanceSeries(): DistanceSeries | null {
		if (distanceSeries) return distanceSeries;
		const track = getGpsData();
		if (!track || track.length < 2) return null;
		distanceSeries = {
			length: track.length,
			secondsAt: (i) => track[i]?._cumulativeTime ?? Number.NaN,
			kmAt: (i) => track[i]?.distance ?? Number.NaN,
		};
		return distanceSeries;
	}

	/** Ride seconds at a fractional index — getCurrentTime() floors to the point in distance mode. */
	function rideSecondsAt(fracIndex: number, fallback: number): number {
		const track = getGpsData();
		const i = Math.floor(fracIndex);
		const a = track?.[i]?._cumulativeTime;
		if (a === undefined) return fallback;
		const b = track?.[i + 1]?._cumulativeTime;
		return b === undefined ? a : a + (b - a) * (fracIndex - i);
	}

	// Show replay button whenever we have GPS data and the MapLibre map is active.
	// Registered in details-shared so initMapLibreTerrain can call it via the onMapReady hook.
	function updateReplayButtonVisibility(): void {
		if (!playReplayBtn) return;

		const gpsDataSource = getGpsData();
		const hasGpsData = gpsDataSource.length >= 10;

		if (hasGpsData && !!getMaplibreMap()) {
			playReplayBtn.style.display = "inline-block";
		} else {
			playReplayBtn.style.display = "none";
		}
	}
	registerReplayButtonVisibility(updateReplayButtonVisibility);

	// Initial visibility check — poll until the map is ready, capped at ~30s (RP-08). The
	// onMapReady hook (registerReplayButtonVisibility) is the reliable trigger after that.
	void pollUntil(
		() => {
			updateReplayButtonVisibility();
			return !!playReplayBtn && playReplayBtn.style.display === "inline-block";
		},
		{ intervalMs: 500, maxTicks: 60 }, // RP-08: silent give-up — onMapReady hook remains the reliable trigger after this
	);

	// Start replay
	playReplayBtn?.addEventListener("click", asyncCallback('start replay', async (e: Event) => {
		e.preventDefault();

		// RP-01: ignore re-entrant starts (a running replay, or one mid-start across the
		// 150ms fullscreen wait) — a second engine orphans the first with dueling rAF loops.
		if (activityReplay || replayStarting) return;

		// Both guards below are UNREACHABLE-BY-DESIGN: updateReplayButtonVisibility keeps the button
		// hidden until GPS ≥ 10 points AND the map exists, so a click means one of those went away
		// underneath us (a torn-down map mid-style-switch). That is a bug state, not a user error —
		// log it and do nothing. It used to alert(), which taught the user to dismiss a modal for
		// something they could neither cause nor fix.
		const gpsDataSource = getGpsData();

		if (gpsDataSource.length < 10) {
			console.error("[Replay] Insufficient GPS data:", gpsDataSource.length, "points");
			return;
		}

		// Replay runs on the MapLibre 3D engine — it must be active.
		const maplibreMap = getMaplibreMap();
		if (!maplibreMap) {
			console.error("[Replay] No MapLibre map — replay button was live without one");
			return;
		}

		// RP-06: close any armed photo-placement / segment panel before the fullscreen
		// flythrough so a stray map click can't commit a pin at the camera location.
		closeAllActionPanels();

		replayStarting = true;
		try {
			// Hide the replay button up-front (RP-01) so a rapid second click has nothing to
			// hit; the catch block restores it on failure.
			playReplayBtn.style.display = "none";

			// Trigger fullscreen
			const mapFullscreenBtn = document.getElementById("mapFullscreenBtn");
			const isAlreadyFullscreen = mapCard?.classList.contains("map-fullscreen") ?? false;

			if (mapFullscreenBtn && !isAlreadyFullscreen) {
				mapFullscreenBtn.click();
				await new Promise((resolve) => setTimeout(resolve, 150));
			}

			// Notify the map about container size change after fullscreen
			if (typeof maplibreMap.resize === "function") {
				maplibreMap.resize();
			}

			// Hide the fullscreen overlay controls during replay
			const mapControls = (mapCard?.querySelector(".det-hero-tr") ?? null) as HTMLElement | null;
			if (mapControls) mapControls.style.display = "none";

			speedLabel.reset();     // the label changes subject with the session
			distanceSeries = null;  // and the track it measures may be a different one
			lastSpeedText = null;
			activityReplay = new ActivityReplay(
				maplibreMap,
				gpsDataSource, // Use the fallback-checked data source
				config.totalTimeSeconds,
			);

			// Setup progress callback
			activityReplay.onProgress = (
				percentage: number, index: number, point: GpsPoint, fracIndex: number,
			) => {
				// The transport writes the scrubber (guarded against the user's own drag) and the elapsed
				// clock. Elapsed is the engine's live currentTime — not the raw config total, which showed a
				// frozen "0:00" when the server total was 0 (RP-05).
				const currentTime = must(activityReplay, 'onProgress fires only from a live replay').getCurrentTime();
				transport.render(percentage, currentTime);
				// The info panel repaints at 5 Hz, not 60 — it was writing five elements and two
				// toFixed strings every frame for numbers that change far more slowly (PERF-2, the
				// same reasoning as the segment sidebar). Trailing, so a seek while paused still lands.
				infoPoint = point;
				infoIndex = index;
				infoRideSeconds = rideSecondsAt(fracIndex, currentTime);
				infoThrottle.request(false);
				// The strip gets the INDEX (fracIndex), not the percentage. Its x-axis is distance travelled
				// while the percentage is a sample fraction — sharing one number between them was what put the
				// marker off the summit. The strip converts, against the distances it drew from; it throttles
				// itself to 30 Hz.
				replayProfile?.tick(fracIndex);
			};

			// Setup completion callback
			activityReplay.onComplete = () => transport.setPlayIcon(false);

			// Keep the header follow/free toggle in sync with the engine's camera state.
			activityReplay.onFollowChange = () => syncFollowToggle();

			// Show controls, exit button and info. The bar is now a single
			// always-visible row, so no collapsible second-row wiring is needed.
			if (replayControls) {
				replayControls.style.display = "flex";
				replayControls.style.zIndex = "10001";
			}
			// The dock wraps the bar, so it has to appear with it — and the strip mounts inside it, but
			// only for a ride that HAS a profile (the module decides; see activity-replay-profile.ts).
			if (replayDock) {
				replayDock.style.display = "flex";
				replayDock.style.zIndex = "10001";
			}
			replayProfile = createActivityReplayProfile({
				points: gpsDataSource,
				onResize: () => maplibreMap.resize(),
			});
			if (replayExit) {
				replayExit.style.display = "flex";
			}
			if (replayInfo) {
				replayInfo.style.display = "block";
				replayInfo.style.zIndex = "10000";

				// Collapse by default on mobile
				if (window.matchMedia("(max-width: 600px)").matches) {
					replayInfo.classList.add("ri-collapsed");
				}

				// Toggle collapse on header click
				const infoHeader = replayInfo.querySelector(
					".replay-info-header",
				) as HTMLElement & { _collapseWired?: boolean };
				if (infoHeader && !infoHeader._collapseWired) {
					infoHeader._collapseWired = true;
					const info = replayInfo; // narrowed rebind for the click closure (G3)
					infoHeader.addEventListener("click", (_e: Event) => {
						info.classList.toggle("ri-collapsed");
					});
				}
			}

			// Replay always runs in time mode (real pace) — the mode toggle was removed.
			// Set the engine to time mode before building the matching speed options.
			if (activityReplay) {
				activityReplay.setMode("time");
			}
			_buildSpeedOptions();

			// Dummy history entry so the browser back button exits the replay
			// instead of leaving the page (handled by the session module).
			session.enter();

			document.body.classList.add('replay-active');

			// Let a modal (the photo lightbox) pause playback while it is up. Routed through the transport's
			// icon so a hold the user didn't trigger still leaves the button honest.
			const replay = activityReplay;
			registerPlaybackHold({
				isPlaying: () => replay.isPlaying,
				pause: () => { replay.pause(); transport.setPlayIcon(false); },
				resume: () => { replay.resume(); transport.setPlayIcon(true); },
				// The viewer pans the map to the photo. Pausing alone doesn't allow that: the follow
				// camera's hook keeps forcing the centre back to the rider, so the easeTo applied its
				// zoom and never moved. Hand the camera over for as long as the photo is up; the user's
				// follow preference (and the header toggle) is untouched either way.
				suspendCamera: () => replay.suspendFollowForModal(),
				restoreCamera: () => replay.restoreFollowAfterModal(),
				// Paging the viewer carries the playhead to each photo, so closing resumes from the one
				// the user stopped on. Seek bar, clock and elevation strip follow via onProgress.
				seekToPosition: (lngLat) => replay.seekToPosition(lngLat),
			});

			activityReplay.start();
			syncFollowToggle(); // start() locks follow on — reflect "(unfollow)" in the header
		} catch (error: unknown) {
			console.error("Error starting replay:", error);
			const msg = error instanceof Error ? error.message : String(error);
			alert("Error starting replay: " + msg);

			if (activityReplay) {
				activityReplay.destroy();
				activityReplay = null;
			}
			// The strip mounts before start() runs, so a failure after that would leave it on screen with
			// nothing driving it.
			replayProfile?.destroy();
			replayProfile = null;
			if (replayDock) replayDock.style.display = "none";

			if (playReplayBtn) playReplayBtn.style.display = "inline-block";
			const mapControls = (mapCard?.querySelector(".det-hero-tr") ?? null) as HTMLElement | null;
			if (mapControls) mapControls.style.display = "";
		} finally {
			// RP-01: clear the start guard; a running replay is still gated by activityReplay.
			replayStarting = false;
		}
	}));

	// Play/pause, the scrubber (rAF-coalesced) and the speed <select> are wired by the transport created
	// at the top — including the photo-lightbox playback hold, which flips the icon through it.

	// Header follow/free toggle — stopPropagation so the click doesn't also fire the header's
	// collapse handler (both live on .replay-info-header).
	function handleFollowToggle(e: Event): void {
		e.stopPropagation();
		if (!activityReplay) return;
		activityReplay.toggleFollow();
	}

	// Reflect the camera state on the header toggle: shows the action a click performs —
	// "(unfollow)" while following, "(follow)" in free look (mirrors segment-stats-replay-ui).
	function syncFollowToggle(): void {
		if (!replayFollowToggle) return;
		const following = !!activityReplay && activityReplay.getFollowMode();
		renderFollowToggleLabel(replayFollowToggle, following);
		replayFollowToggle.title = following
			? "Free camera — stop following the marker"
			: "Follow the marker";
	}

	replayExit?.addEventListener("click", () => exitReplay());
	replayFollowToggle?.addEventListener("click", handleFollowToggle);

	// ====================================================================
	// DYNAMIC SPEED OPTIONS
	// ====================================================================

	function _buildSpeedOptions(): void {
		// The ladder is scaled to the ride's duration and shared with the segment + race replays
		// (core/speed-ladder, via the transport) — it sets the default speed on the engine and refreshes
		// the stepper. The old activity-only ladder (roundReplaySpeed) has been retired.
		const maxTimeSec =
			(activityReplay && activityReplay.getTotalTime()) ||
			config.totalTimeSeconds ||
			3600;
		transport.buildLadder(maxTimeSec);
	}

	// ====================================================================
	// INFO DISPLAY
	// ====================================================================

	function updateReplayInfo(point: GpsPoint, index: number, rideSeconds: number): void {
		if (!replayInfo) return;

		if (replayInfoDistance && point.distance !== undefined) {
			replayInfoDistance.textContent = point.distance.toFixed(2) + " km";
		}
		if (point.speed !== undefined) {
			// One measurement drives both: the latched whole number for the label, the unrounded value
			// for the bar — a bar fed the latched number would staircase. The stored speed channel is
			// the fallback when the track carries no usable time/distance to measure across.
			const series = getDistanceSeries();
			const measured = series && centredMeanKmh(series, rideSeconds, windowSecondsFor(playbackRate));
			const valueKmh = measured ?? point.speed;
			const text = speedLabel.read(valueKmh) + " km/h";
			if (replayInfoSpeed && lastSpeedText !== text) {
				replayInfoSpeed.textContent = text;
				lastSpeedText = text;
			}
			if (replayInfoSpeedBar) {
				const scale = getSpeedScale();
				let pct = scale.isEmpty ? 0 : scale.fill(valueKmh);
				if (valueKmh > 0 && pct < 2) pct = 2; // keep the slowest moment visible
				replayInfoSpeedBar.style.width = pct.toFixed(1) + "%";
			}
		}
		if (replayInfoAltitude && point.altitude !== undefined) {
			replayInfoAltitude.textContent = Math.round(point.altitude) + " m";
		}
		if (replayInfoHR) {
			replayInfoHR.textContent =
				point.heartRate && point.heartRate > 0
					? Math.round(point.heartRate) + " bpm"
					: "-- bpm";
		}

		// Update completion percentage if element exists
		if (replayInfoCompletion && activityReplay) {
			const dataLen = getGpsData().length;
			const totalPoints = dataLen > 0 ? dataLen : 1;
			const progress = (index / (totalPoints - 1)) * 100;
			replayInfoCompletion.textContent = progress.toFixed(1) + "%";
		}
	}

	function exitReplay(): void {
		document.body.classList.remove('replay-active');
		registerPlaybackHold(null); // the engine is about to be destroyed — drop the slot and any hold
		infoThrottle.cancel();      // a queued trailing repaint would fire into a torn-down panel
		infoPoint = null;
		if (activityReplay) {
			activityReplay.destroy();
			activityReplay = null;
		}
		replayProfile?.destroy();
		replayProfile = null;

		if (replayDock) replayDock.style.display = "none";
		if (replayControls) replayControls.style.display = "none";
		if (replayExit) replayExit.style.display = "none";
		if (replayInfo) {
			replayInfo.style.display = "none";
			replayInfo.classList.remove("ri-collapsed");
		}

		const mapControls = mapCard?.querySelector(".det-hero-tr") as HTMLElement | null;
		if (mapControls) mapControls.style.display = "";
		if (playReplayBtn) playReplayBtn.style.display = "";

		// Exit CSS fullscreen if active
		const mapFullscreenBtn = document.getElementById("mapFullscreenBtn");
		const isFullscreen = mapCard?.classList.contains("map-fullscreen");
		if (mapFullscreenBtn && isFullscreen) mapFullscreenBtn.click();

		updateReplayButtonVisibility();

		transport.setPlayIcon(true);

		// Remove the dummy history entry that was pushed when replay started
		// (guarded — a back-button exit already moved us back).
		session.leave();
	}

	document.addEventListener("fullscreenchange", () => {
		if (!document.fullscreenElement && activityReplay) exitReplay();
	});

	document.addEventListener("webkitfullscreenchange", () => {
		if (
			!(document as Document & { webkitFullscreenElement?: Element })
				.webkitFullscreenElement &&
			activityReplay
		)
			exitReplay();
	});

}
