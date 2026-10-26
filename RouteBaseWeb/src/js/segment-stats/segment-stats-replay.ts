/**
 * Segment Statistics — Multi-effort Replay Engine (Core)
 *
 * Drives the "race" animation: multiple effort markers move simultaneously
 * along their GPS tracks, synchronized by TIME. Each effort advances at
 * its own pace — faster efforts visually pull ahead on the map.
 *
 * Rendering is split across two modules (imported directly as ES module singletons —
 * the old window._segStatsReplay* globals are gone):
 *   segment-stats-replay-tracks.ts    (replayTracksApi)    — GeoJSON track line layers
 *   segment-stats-replay-markers.ts   (replayMarkersApi)   — moving dot markers
 *
 * Effort state is owned by:
 *   segment-stats-replay-efforts.ts   (replayEffortsApi)   — effort state machine
 *
 * Performance diagnostics are delegated to segment-stats-replay-perf.ts (replayPerfApi).
 *
 * MapLibre-only: the follow camera keeps the marker centred and leaves zoom/bearing/pitch
 * to MapLibre's gestures (core/replay-follow-camera.ts owns that split).
 *
 * Depends on: ctx (shared context)
 *             replayEffortsApi (effort state machine)
 *             replayTracksApi  (track drawing module)
 *             replayMarkersApi  (marker module)
 *             replayPerfApi     (perf diagnostics)
 *             CameraControls    (follow camera)
 *             MapLibre (maplibregl)
 */
import type { EffortPerformanceData } from './segment-stats-effort-types';
import type { ReplayEffort, ReplayMode, ReplayEffortState, ReplayPointSample, SegmentStatsReplayApi, ReplayDiagnosticSnapshot } from './segment-stats-replay-types';
import { createReplayPlaybackShell } from '../core/replay-playback-shell';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { ctx } from './segment-statistics';
import { replayEffortsApi as E } from './segment-stats-replay-efforts';
import { replayTracksApi as T } from './segment-stats-replay-tracks';
import { replayMarkersApi as M } from './segment-stats-replay-markers';
import { replayPerfApi as _perf } from './segment-stats-replay-perf';
import { CameraControls } from '../maplibre/camera-controls';
import { engageFollow, releaseFollow } from '../core/replay-follow-camera';
import { registerReplayApi, setSegmentReplayActive } from './seg-stats-shared';
import { must } from '../core/invariant';

// the api object is created at module load and populated by the module's own bottom-of-file
// call. Every method/helper receives the page context via the `ctx` PARAMETER of initSegmentStatsReplay
// rather than the imported singleton (read only for that one top-level call), so registerReplayApi and
// api population run at the same module-eval point as before.
export const replayApi = {} as SegmentStatsReplayApi;
export function initSegmentStatsReplay(ctx: SegmentStatisticsContext): void {

	// ====================================================================
	// STATE
	// ====================================================================

	let map: maplibregl.Map | null = null;

	let currentTime = 0;

	let followEffortIndex = -1;
	let suppressedTracks = false; // heatmap view: follow mode draws only the followed effort
	let _visibleIndices: number[] = []; // remembered from init so addLoadedEfforts can rebuild (PS-03)

	let onProgress: ((pct: number, states: ReplayEffortState[]) => void) | null = null;
	let onComplete: (() => void) | null = null;
	let onEffortFinish: ((idx: number, order: number, time: number) => void) | null = null;

	// Shared playback transport (CR1-01) — hooks are closures over live module state
	// (factory paradigm kept, CR1-08). Multi-effort follow target via _followCenter.
	const shell = createReplayPlaybackShell({
		tick: (dtMs: number, spd: number): boolean => _tick(dtMs, spd),
		// RP-04 twin (RS2-04): after completion the playhead sits at the end and every effort
		// is finished — resume would instantly re-complete + wedge the play button. Restart from 0.
		isAtEnd: (): boolean => {
			const s = E.getEffortStates();
			return s.length > 0 && s.every((x) => x.finished);
		},
		restart: (): void => {
			currentTime = 0;
			E.reset();
			M.resetMarkerFeatures(highlightedEfforts);
		},
		// RP-02 twin (RS2-02): the paused-camera idle loop runs only while following. Pause does
		// NOT deactivate the follow camera — follow mode resumes seamlessly on resume.
		wantsIdleLoop: (): boolean => followEffortIndex >= 0,
		idleCenter: _followCenter,
		getCameraControls: () => cameraControls,
		onComplete: (): void => {
			if (onComplete) onComplete();
		},
		// CR1-07 (user-approved 2026-07-11): idle the follow camera on completion, matching
		// chain A — while following, the camera stays live at the finish instead of freezing
		// until play/seek (the residual corner of the RS2-02 class).
		idleOnComplete: true,
	});

	// ====================================================================
	// PUBLIC API
	// ====================================================================

	const api = replayApi;
	// Register in the shared slot for map-hover.ts, which reads it to avoid a circular import.
	registerReplayApi(api);

	api.init = (
		mapInstance: maplibregl.Map,
		perfData: EffortPerformanceData[],
		visibleIndices: number[],
		suppressTracks = false,
	): boolean => {
		api.destroy();

		map = mapInstance;
		suppressedTracks = suppressTracks;
		_visibleIndices = visibleIndices.slice();
		currentTime = 0;
		followEffortIndex = -1;
		cameraControls = null;
		highlightedEfforts = {};
		_hoverEffortIndex = -1;

		// Build effort list and initial states
		E.build(perfData, visibleIndices, ctx.effortColors);
		if (E.getEfforts().length === 0) return false;

		// Bind rendering modules + perf diagnostic snapshot
		T.bind(map, E.getEfforts());
		M.bind(map, E.getEfforts());
		_perf.setDiagnosticSnapshot((): ReplayDiagnosticSnapshot => {
			const snapT = T.getDiagnosticSnapshot();
			const snapM = M.getDiagnosticSnapshot();
			return {
				...snapT,
				...snapM,
				// Sum the ML source/layer counts from both modules rather than letting
				// spread pick one over the other.
				mlSources: (snapT.mlTrackSources || 0) + (snapM.mlMarkerSources || 0),
				mlLayers: (snapT.mlTrackLayers || 0) + (snapM.mlMarkerLayers || 0),
				followFrameCount: 0,
				effortCount: E.getEfforts().length,
			};
		});

		// Step 1: fit bounds FIRST so _pixelOrigin is stable
		_fitBounds();

		// Step 2: draw track polylines (skipped in heatmap view — markers race over the raster
		// instead); Step 3: create markers
		if (!suppressTracks) T.draw();
		M.create();

		// Bind interaction listeners
		_bindInteractionListeners();

		return true;
	};

	// PS-03: merge freshly-fetched full-res effort points and rebuild the effort model + render
	// layers in place, preserving the live playhead / sync mode / highlights. Called once per
	// full-res chunk so replayed efforts sharpen from downsampled to byte-identical progressively.
	// The visible effort SET is unchanged (chunks only upgrade geometry), so marker/track indices —
	// and any follow/highlight referencing them — stay stable.
	api.addLoadedEfforts = (perfData: EffortPerformanceData[]): void => {
		if (!map) return;
		const savedMode = E.getMode();

		// Rebuild the effort list against the merged data. The visible SET is unchanged (chunks only
		// upgrade geometry), so we UPDATE the render layers in place rather than destroy+recreate them:
		// a teardown removes the GeoJSON sources/layers then re-adds them, flashing the markers/tracks
		// off then back on once per chunk (the replay-start flicker). setData sharpens without the flash.
		E.build(perfData, _visibleIndices, ctx.effortColors);
		E.setMode(savedMode);
		if (E.getEfforts().length === 0) return;

		M.rebind(E.getEfforts());
		T.rebind(E.getEfforts());
		if (!suppressedTracks) T.updateEffortTracks();

		// Markers keep their live finished/highlight state (features are stable); just re-derive the
		// positions from the sharpened points at the current playhead and push them in one setData.
		E.seekReset(currentTime, M);
		E.updateAllMarkers(currentTime, M);
		M.flushMarkerGeoSource();
		if (onProgress) onProgress(E.getProgressPct(currentTime), E.getEffortStates());
	};

	api.start = (): void => {
		currentTime = 0;
		shell.resetCameraTracking();
		E.reset();
		M.resetMarkerFeatures(highlightedEfforts);

		setSegmentReplayActive(true);
		_perf.start();
		shell.beginPlayback();
	};

	// RS2-02 (paused-follow idle loop) and RS2-04 (resume-after-completion restart)
	// semantics live in the shell, driven by the hooks above.
	api.pause = (): void => shell.pause();
	api.resume = (): void => shell.resume();
	api.togglePlayPause = (): boolean => shell.togglePlayPause();

	api.seek = (percentage: number): void => {
		currentTime = (percentage / 100) * E.getMaxTime();
		shell.resetCameraTracking();

		E.seekReset(currentTime, M);
		E.updateAllMarkers(currentTime, M);
		if (onProgress) onProgress(E.getProgressPct(currentTime), E.getEffortStates());
	};

	api.setSpeed = (mult: number): void => shell.setSpeed(mult);

	api.setMode = (mode: ReplayMode): void => {
		E.setMode(mode);
		// Reposition every marker for the new domain at the current playhead. seekReset
		// re-derives finished/active + rewinds the forward-scan cursor (the domain changed,
		// so a cached lower-bound index may now be too high); updateAllMarkers then snaps
		// the active markers to their interpolated positions.
		E.seekReset(currentTime, M);
		E.updateAllMarkers(currentTime, M);
		if (onProgress) onProgress(E.getProgressPct(currentTime), E.getEffortStates());
	};

	api.getMode = (): ReplayMode => E.getMode();

	api.setFollowEffort = (effortIdx: number): void => {
		const prevFollow = followEffortIndex;

		followEffortIndex = effortIdx;

		if (prevFollow >= 0 && prevFollow !== effortIdx) {
			if (!highlightedEfforts[prevFollow] && prevFollow !== _hoverEffortIndex) {
				api.setMarkerHighlight(prevFollow, false);
			}
		}

		if (followEffortIndex >= 0) {
			api.setMarkerHighlight(followEffortIndex, true);
		}

		// Heatmap-view replay draws no base tracks — reveal just the followed effort's path, clear on
		// unfollow. Other views already show every track, so there's nothing to toggle.
		if (suppressedTracks) {
			if (followEffortIndex >= 0) T.showEffortTrack(followEffortIndex);
			else T.hideEffortTrack();
		}

		if (cameraControls && typeof cameraControls.reset === "function") cameraControls.reset();

		_updateFollowMode();

		if (onFollowChange) onFollowChange(effortIdx);
	};

	api.isPlaying = (): boolean => shell.isPlaying();
	api.getSpeed = (): number => shell.getSpeed();
	api.getEfforts = (): ReplayEffort[] => E.getEfforts();
	api.getEffortStates = (): ReplayEffortState[] => E.getEffortStates();
	api.getProgressPct = (): number => E.getProgressPct(currentTime);
	api.getMaxTime = (): number => E.getMaxTime();
	api.getCurrentTime = (): number => currentTime;

	api.onProgress = (cb: (pct: number, states: ReplayEffortState[]) => void): void => {
		onProgress = cb;
	};
	api.onComplete = (cb: () => void): void => {
		onComplete = cb;
	};
	api.onEffortFinish = (cb: (idx: number, order: number, time: number) => void): void => {
		onEffortFinish = cb;
	};

	let onFollowChange: ((idx: number) => void) | null = null;
	let highlightedEfforts: Record<string, boolean> = {};
	let _hoverEffortIndex = -1;

	api.onFollowChange = (cb: (idx: number) => void): void => {
		onFollowChange = cb;
	};
	api.getFollowIndex = (): number => followEffortIndex;

	api.getHighlightedEfforts = (): number[] =>
		Object.keys(highlightedEfforts).map((k: string) => parseInt(k));

	api.toggleEffortHighlight = (idx: number, on: boolean): void => {
		const efforts = E.getEfforts();
		if (idx < 0 || idx >= efforts.length) return;
		if (on) {
			highlightedEfforts[idx] = true;
		} else {
			delete highlightedEfforts[idx];
		}
		if (idx !== followEffortIndex) {
			api.setMarkerHighlight(idx, on);
		}
	};

	api.setHoverEffort = (idx: number): void => {
		const prev = _hoverEffortIndex;
		_hoverEffortIndex = idx;
		if (prev >= 0 && prev !== idx && prev !== followEffortIndex && !highlightedEfforts[prev]) {
			api.setMarkerHighlight(prev, false);
		}
		if (idx >= 0) {
			api.setMarkerHighlight(idx, true);
		}
	};

	api.setSegmentBackgroundVisible = (visible: boolean): void => {
		T.setSegmentBackgroundVisible(visible);
	};

	api.setMarkerHighlight = (idx: number, on: boolean): void => {
		M.setMarkerHighlight(idx, on);
	};

	api.destroy = (): void => {
		// destroy(), not pause(): pause() re-arms the paused-camera idle loop, and this teardown then
		// nulls `map`/`cameraControls` under it — the RS2-class leak of an rAF centring a dead map. The
		// _unbindInteractionListeners() below only saves us when releaseFollow does not throw first.
		shell.destroy();
		_perf.stop();
		if (followEffortIndex >= 0) M.setMarkerHighlight(followEffortIndex, false);

		// Must unbind before nulling map/cameraControls — re-enables MapLibre
		// interaction handlers that were disabled when follow mode activated.
		_unbindInteractionListeners();

		T.destroyAll();
		M.destroyAll();
		E.destroy();

		map = null;
		currentTime = 0;
		followEffortIndex = -1;
		suppressedTracks = false;
		cameraControls = null;
		highlightedEfforts = {};
		_hoverEffortIndex = -1;
		setSegmentReplayActive(false);
	};

	// ====================================================================
	// ANIMATION TICK — the shell owns the frame clock; this advances the
	// playhead per frame and returns true on completion (shell stops + fires onComplete)
	// ====================================================================

	function _tick(dtMs: number, spd: number): boolean {
		const _frameT = _perf.frameStart(followEffortIndex >= 0);

		currentTime += (dtMs / 1000) * spd;

		const { allFinished, activeCount } = E.advance(currentTime, M, onEffortFinish);
		let _t = _perf.phase("markers", _frameT);

		_updateCamera();
		_t = _perf.phase("camera", _t);

		M.flushMarkerGeoSource();
		_t = _perf.phase("flush", _t);

		if (onProgress) onProgress(E.getProgressPct(currentTime), E.getEffortStates());
		_t = _perf.phase("progress", _t);

		_perf.frameEnd(_frameT, activeCount);

		return allFinished;
	}

	// ====================================================================
	// USER INTERACTION TRACKING
	// ====================================================================

	let cameraControls: CameraControls | null = null;
	// Base framing, applied ONCE when an effort is followed. After that the zoom/bearing/pitch are
	// MapLibre's — the user pinches, twists and tilts while the camera tracks the effort.
	const FOLLOW_SEED = { zoom: 15, bearing: 0, pitch: 30 } as const;

	let interactionListenersBound = false;

	function _bindInteractionListeners(): void {
		_unbindInteractionListeners();
		if (!map) return;
		cameraControls = new CameraControls(map);
		_updateFollowMode();
		interactionListenersBound = true;
	}

	function _unbindInteractionListeners(): void {
		if (!interactionListenersBound || !map) return;
		// RS2-07: re-enabling the native map handlers must be unconditional — releaseFollow's finally
		// guarantees it even when deactivate() throws (getCanvas() on a disposed map). Teardown swallows
		// that throw; the follow toggle above does not.
		try {
			releaseFollow(map, cameraControls);
		} catch (_e) { /* teardown: may already be removed */ }
		cameraControls = null;
		interactionListenersBound = false;
	}

	// Camera center for the paused idle loop (RS2-02): the followed effort's last marker position,
	// falling back to the current map center before the first frame.
	function _followCenter(): maplibregl.LngLatLike {
		const st = E.getEffortStates()[followEffortIndex];
		return st && Number.isFinite(st.lastLat) && Number.isFinite(st.lastLng)
			? [st.lastLng, st.lastLat] : must(map, 'follow camera runs with a bound map').getCenter();
	}

	function _updateFollowMode(): void {
		if (!map) return;
		// The shared follow seam (core/replay-follow-camera) owns the canvas handover. FOLLOW_SEED names
		// every field, so the map's own getters are never consulted — this page frames the effort, it
		// does not adopt the current view the way /LiveView does.
		if (followEffortIndex >= 0 && cameraControls) {
			engageFollow(map, cameraControls, FOLLOW_SEED);
			// Follow toggled on while paused → start the idle loop so the marker is tracked immediately.
			shell.syncIdleCamera();
		} else {
			releaseFollow(map, cameraControls);
		}
	}

	// ====================================================================
	// CAMERA
	// ====================================================================

	function _updateCamera(): void {
		if (followEffortIndex < 0 || followEffortIndex >= E.getEffortStates().length) return;

		const state = must(E.getEffortStates()[followEffortIndex], 'followEffortIndex < length (guarded above)');
		const lat = state.lastLat;
		const lng = state.lastLng;
		if (!Number.isFinite(lat) || !Number.isFinite(lng)) return; // 0 is a valid coord (equator/meridian)

		// Whether to move the camera at all is follow-mode domain logic — the gate stays here.
		if (map && cameraControls && cameraControls.isActive) {
			shell.applyCamera(map, { center: [lng, lat] });
		}
	}

	function _fitBounds(): void {
		const segCoords = ctx.config.segmentCoords;
		if (!segCoords || segCoords.length === 0 || !map) return;

		let minLat = Infinity,
			maxLat = -Infinity,
			minLng = Infinity,
			maxLng = -Infinity;
		// ?? NaN: loop-bounded [lat,lng] pair reads; NaN fails every comparison and leaves the
		// bound unchanged, exactly as undefined did.
		for (const c of segCoords) {
			const clat = c[0] ?? NaN, clng = c[1] ?? NaN;
			if (clat < minLat) minLat = clat;
			if (clat > maxLat) maxLat = clat;
			if (clng < minLng) minLng = clng;
			if (clng > maxLng) maxLng = clng;
		}
		try {
			map.fitBounds(
				[
					[minLng, minLat],
					[maxLng, maxLat],
				],
				{ padding: 40 },
			);
		} catch (e: unknown) { console.warn('[seg-stats-replay] fitBounds failed:', e); }
	}

	// ====================================================================
	// INTERPOLATED EFFORT DATA
	// ====================================================================

	api.getEffortDataAtCurrentTime = (effortIdx: number): ReplayPointSample | null =>
		E.getDataAtTime(effortIdx, currentTime);

	api.getEffortDataAtCurrentDistance = api.getEffortDataAtCurrentTime;
} // end initSegmentStatsReplay

if (!ctx) { throw new Error("segment-stats-replay: no ctx"); }
initSegmentStatsReplay(ctx);
