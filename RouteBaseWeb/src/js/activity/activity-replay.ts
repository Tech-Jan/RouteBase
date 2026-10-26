// activity-replay.ts — 3D Activity Replay System (ES module)
//
// Simplified camera: center always equals the marker position (same
// approach as segment-stats-replay.ts). Zoom, bearing and pitch belong to
// MapLibre's own gesture handlers while following — see
// core/replay-follow-camera.ts for who owns what.
//
// Follow engages framed straight-down (pitch 0, bearing 0); from there the
// user tilts and rotates with the normal map gestures.
//
// Uses jumpTo every frame instead of easeTo to prevent camera drift.
// After jumpTo, terrain elevation is applied to the camera's internal
// center altitude so the focal point sits on the terrain surface —
// without this the marker drifts off-center when the user tilts.
//
// Marker: GeoJSON circle layer via replay-engine-base createMarkerManager.
// One setData() per frame instead of a DOM write + MapLibre reprojection
// on every jumpTo 'move' event.
//
import type { GpsPoint } from '../core/core-types';
import { must } from '../core/invariant';
import { setTransformCenterAltitude } from '../core/map-internals';
import { normalizeTrackDistances } from '../core/map-utils-shared';
import {
	computeCumulativeTime,
	createMarkerManager,
	advanceByDistance,
	advanceByTime,
	interpolatePosition,
	type ReplayMarkerManager,
} from '../core/replay-engine-base';
import { createReplayPlaybackShell, type ReplayPlaybackShell } from '../core/replay-playback-shell';
import { engageFollow, releaseFollow } from '../core/replay-follow-camera';
import { findNearestPointIndexExact } from '../core/nearest-point';
import { CameraControls } from '../maplibre/camera-controls';
import { getCurrentMarker, setBlueMarkerVisible, setReplayActive } from '../core/map-state';

export class ActivityReplay {
	map: maplibregl.Map;
	originalTrack: GpsPoint[];
	cameraTrack: GpsPoint[] | null;
	indexMap: number[] | null;

	currentIndex: number;

	// Replay mode
	mode: string;

	// Replay timing
	BASE_SECONDS_PER_KM: number;

	// Time-based mode state
	currentTime: number;
	totalTimeSeconds: number;

	// Camera framing applied when follow engages — north-up, top-down. From then on the user's
	// gestures own zoom/bearing/pitch (MapLibre's own handlers; see core/replay-follow-camera.ts).
	FIXED_ZOOM: number;
	BASE_PITCH: number;
	BASE_BEARING: number;

	// Shared playback transport (CR1-01): frame clock, pause/resume/completion,
	// camera change-detector + terrain-elevation pairing
	_shell: ReplayPlaybackShell;

	// GeoJSON marker — shared MarkerManager from replay-engine-base.js
	_markerManager: ReplayMarkerManager | null;

	// The follow camera's idle loop + the input MapLibre can't provide (1-finger touch, keys).
	cameraControls: CameraControls;

	// Follow mode — true: camera locks to the marker (default); false: free look, the marker
	// moves through a user-panned view (single-marker analogue of the race/segments follow toggle).
	followMode: boolean;

	// Follow camera suspended by an open modal (photo viewer) — distinct from followMode, which is the
	// user's preference and must survive the modal.
	_followSuspended: boolean;

	// Callbacks
	/**
	 * `progress` and `index` are SAMPLE-space (and so, in time mode, effectively elapsed time) — that
	 * is what the seek bar and the clock readout want. `fracIndex` is the same playhead unrounded, for
	 * consumers that have to convert it into a space of their own; the elevation strip's x-axis is
	 * distance, which sample space only matches on a track with equidistant points.
	 */
	onProgress:
		((progress: number, index: number, point: GpsPoint, fracIndex: number) => void) | null;
	onComplete: (() => void) | null;
	onModeChange: ((mode: string) => void) | null;
	onFollowChange: ((following: boolean) => void) | null;

	constructor(maplibreInstance: maplibregl.Map, gpsData: GpsPoint[], totalTimeSeconds: number) {
		if (!maplibreInstance || !gpsData || gpsData.length === 0) {
			throw new Error("ActivityReplay requires valid map instance and GPS data");
		}

		this.map = maplibreInstance;
		this.originalTrack = gpsData;
		this.cameraTrack = null;
		this.indexMap = null;

		this.currentIndex = 0;

		// Follow mode — camera locks to the marker until the user toggles free look.
		this.followMode = true;
		this._followSuspended = false;

		// Replay mode
		this.mode = "distance";

		// Replay timing
		this.BASE_SECONDS_PER_KM = 5;

		// Time-based mode state
		this.currentTime = 0;
		this.totalTimeSeconds = totalTimeSeconds || 0;

		// Camera framing applied when follow engages — north-up, top-down (see the field docs above).
		this.FIXED_ZOOM = 15.5;
		this.BASE_PITCH = 0;
		this.BASE_BEARING = 0;

		// GeoJSON marker — shared MarkerManager from replay-engine-base.js
		this._markerManager = null;

		// Process track
		this._processTrack();
		this._computeCumulativeTime();

		// Shared playback transport (CR1-01) — hooks read live instance state.
		this._shell = createReplayPlaybackShell({
			tick: (dt: number, s: number): boolean => this._tick(dt, s),
			// RP-04: after completion the clock/index sit at the end — resume restarts
			// from 0 so Play re-watches instead of hitting the completion branch.
			isAtEnd: (): boolean =>
				this.mode === "time"
					? this.currentTime >= this.totalTimeSeconds
					: this.currentIndex >= (this.cameraTrack?.length ?? 1) - 1,
			restart: (): void => {
				this.currentIndex = 0;
				this.currentTime = 0;
			},
			// RP-02: the paused-camera idle loop re-centres on the marker while paused — but only
			// while following. In free look it stays off so the user's panned view holds still.
			wantsIdleLoop: (): boolean => this.followMode,
			idleCenter: (): maplibregl.LngLatLike => {
				const pos = interpolatePosition(this.originalTrack, this.currentIndex);
				return pos ? [pos.lng, pos.lat] : this.map.getCenter();
			},
			getCameraControls: () => this.cameraControls,
			onComplete: (): void => {
				if (this.onComplete) this.onComplete();
			},
			idleOnComplete: true, // pinned: user can look around at the finish (CR1-07)
		});

		this.cameraControls = new CameraControls(this.map);

		// Callbacks
		this.onProgress = null;
		this.onComplete = null;
		this.onModeChange = null;
		this.onFollowChange = null;

	}

	// ================================================================
	// Track preparation
	// ================================================================

	_processTrack(): void {
		this.cameraTrack = this.originalTrack;
		this.indexMap = this.originalTrack.map((_: GpsPoint, i: number) => i);

		// Normalize distances (metres → km, fix non-monotonic)
		normalizeTrackDistances(this.originalTrack);
	}

	_computeCumulativeTime(): void {
		if (!this.cameraTrack) return;
		this.totalTimeSeconds = computeCumulativeTime(
			this.cameraTrack,
			this.totalTimeSeconds,
		);
	}

	// ================================================================
	// Marker — delegates to replay-engine-base createMarkerManager
	// ================================================================

	initMarker(): void {
		const fp = must(this.originalTrack[0], 'replay track non-empty (start-guarded)');
		if (!this._markerManager) {
			this._markerManager = createMarkerManager(this.map, {
				sourceId: "activity-replay-marker",
				borderLayerId: "activity-replay-marker-border",
				fillLayerId: "activity-replay-marker-fill",
				borderRadius: 14,
				fillRadius: 10,
				fillColor: "#007bff",
				borderColor: "#ffffff",
			});
		}
		this._markerManager.init(fp.lng, fp.lat);
	}

	_setMarkerPosition(lng: number, lat: number): void {
		if (this._markerManager) {
			this._markerManager.setPosition(lng, lat);
		}
	}

	_removeMarker(): void {
		if (this._markerManager) {
			this._markerManager.remove();
			this._markerManager = null;
		}
	}

	static setBlueMarkerVisibility(visible: boolean): void {
		const marker = getCurrentMarker();
		if (marker && typeof marker.getElement === "function") {
			const el = marker.getElement();
			if (el) el.style.display = visible ? "block" : "none";
		}
		setBlueMarkerVisible(visible);
	}

	// ================================================================
	// Mode switching
	// ================================================================

	setMode(newMode: string): void {
		if (newMode !== "distance" && newMode !== "time") return;
		if (newMode === this.mode) return;
		const wasPlaying = this.isPlaying;
		const track = this.cameraTrack;
		if (!track) return;

		if (newMode === "time") {
			const idx = Math.floor(this.currentIndex);
			const cum = idx < track.length ? track[idx]?._cumulativeTime : undefined;
			this.currentTime =
				cum !== undefined
					? cum
					: (idx / Math.max(1, track.length - 1)) * this.totalTimeSeconds; // RP-09: single-point guard
		} else {
			this.currentIndex = Math.max(
				0,
				Math.min(this.currentIndex, track.length - 1),
			);
		}

		this.mode = newMode;
		if (this.onModeChange) this.onModeChange(newMode);
		if (wasPlaying) {
			this.pause();
			this.resume();
		}
	}

	getMode(): string {
		return this.mode;
	}
	getTotalTime(): number {
		return this.totalTimeSeconds;
	}

	getCurrentTime(): number {
		if (this.mode === "time") return this.currentTime;
		const track = this.cameraTrack;
		if (!track) return 0;
		const idx = Math.floor(this.currentIndex);
		const cum = idx < track.length ? track[idx]?._cumulativeTime : undefined;
		if (cum !== undefined) return cum;
		return (idx / Math.max(1, track.length - 1)) * this.totalTimeSeconds; // RP-09: single-point guard
	}

	// ================================================================
	// Playback control
	// ================================================================

	/** Playback state lives in the shell; getter keeps the `isPlaying: boolean` property contract. */
	get isPlaying(): boolean {
		return this._shell.isPlaying();
	}

	start(): void {
		this.currentIndex = 0;
		this.currentTime = 0;

		// Reset camera change tracking so the first frame always fires jumpTo
		this._shell.resetCameraTracking();

		ActivityReplay.setBlueMarkerVisibility(false);
		setReplayActive(true);
		// Add GeoJSON marker layers (waits for map load if needed)
		this.initMarker();

		// Start in follow mode — engage the shared follow seam (the follow takes the centre, MapLibre
		// keeps the gestures). setFollow(false) hands the centre back for free look. Seeding it with the
		// first point IS the jump to start: engage frames and centres in one write.
		this.followMode = true;
		const fp = must(this.originalTrack[0], 'replay track non-empty (start-guarded)');
		engageFollow(this.map, this.cameraControls, { ...this._followSeed(), center: [fp.lng, fp.lat] });

		try {
			// Apply terrain elevation fix for initial position
			if (
				this.map.getTerrain &&
				this.map.getTerrain() &&
				typeof this.map.queryTerrainElevation === "function"
			) {
				const elev = this.map.queryTerrainElevation({
					lng: fp.lng,
					lat: fp.lat,
				});
				if (elev !== null && elev !== undefined) {
					if (typeof this.map.setCenterElevation === "function") {
						this.map.setCenterElevation(elev);
					} else if (this.map.transform) {
						setTransformCenterAltitude(this.map.transform, elev);
					}
				}
			}
		} catch (e: unknown) { console.warn('[activity-replay] initial terrain clamp failed:', e); }

		this._shell.beginPlayback();
	}

	// RP-02 (paused-camera idle loop) and RP-04 (resume-after-completion restart)
	// semantics live in the shell, driven by this instance's hooks.
	pause(): void {
		this._shell.pause();
	}

	resume(): void {
		this._shell.resume();
	}

	seek(percentage: number = 0): void {
		const track = this.cameraTrack;
		if (!track) return;
		this.seekToIndex((percentage / 100) * Math.max(1, track.length - 1)); // RP-09: single-point guard
	}

	/** Move the playhead to a track sample — the index-space counterpart of {@link seek}'s percentage. */
	seekToIndex(index: number): void {
		const track = this.cameraTrack;
		if (!track) return;
		const idx = Math.max(0, Math.min(Math.floor(index), track.length - 1));
		this.currentIndex = idx;
		this.currentTime =
			track[idx]?._cumulativeTime ??
			(idx / Math.max(1, track.length - 1)) * this.totalTimeSeconds; // RP-09: single-point guard

		this._shell.resetCameraTracking();

		this.updateFrame(idx);
	}

	/**
	 * Move the playhead to the track sample nearest `lngLat`.
	 *
	 * The photo viewer's seek: paging through the photos pans the map to each one, and closing there
	 * has to resume from THAT point rather than snapping back to where the rider was when it opened.
	 */
	seekToPosition(lngLat: [number, number]): void {
		const idx = findNearestPointIndexExact(lngLat[1], lngLat[0], this.originalTrack);
		if (idx < 0) return;
		this.seekToIndex(idx);
	}

	setSpeed(multiplier: number = 1): void {
		this._shell.setSpeed(multiplier);
	}

	// ================================================================
	// Follow mode — single-marker analogue of the race/segments follow toggle.
	// Following: the camera's centre locks to the marker. Free look: panning
	// returns and the marker moves through a user-panned view.
	// ================================================================

	/** The framing each engage applies once; every field named, so the map's getters stay unread. */
	private _followSeed(): { zoom: number; bearing: number; pitch: number } {
		return { zoom: this.FIXED_ZOOM, bearing: this.BASE_BEARING, pitch: this.BASE_PITCH };
	}

	setFollow(on: boolean): void {
		if (on === this.followMode) return;
		this.followMode = on;
		if (on) {
			// Re-take the centre and snap straight back to the marker.
			engageFollow(this.map, this.cameraControls, this._followSeed());
			this._shell.resetCameraTracking();
			this.updateFrame(Math.floor(this.currentIndex));
			this._shell.syncIdleCamera(); // paused + follow → track the marker right away
		} else {
			// Hand the canvas back for free pan/zoom; stop the paused idle re-centre.
			releaseFollow(this.map, this.cameraControls);
			this._shell.stopIdleCamera();
		}
		if (this.onFollowChange) this.onFollowChange(on);
	}

	/**
	 * Suspend the follow CAMERA while a modal (the photo viewer) is open, leaving the user's follow
	 * PREFERENCE untouched so the header toggle doesn't flip under them.
	 *
	 * Why this exists: engageFollow installs a `transformCameraUpdate` hook that forces every camera
	 * update's centre back to the rider. Pausing playback does not remove it, so a photo's
	 * `easeTo({center: photo})` applied its zoom and then had its centre overridden on the very next
	 * frame — the map stayed locked on the marker and never panned to the photo. Pause ≠ release-follow.
	 */
	suspendFollowForModal(): void {
		if (!this.followMode || this._followSuspended) return;
		this._followSuspended = true;
		releaseFollow(this.map, this.cameraControls);
		this._shell.stopIdleCamera();
	}

	/**
	 * Re-engage the follow camera after the modal closes — unless the user turned follow off meanwhile.
	 *
	 * Deliberately SEEDLESS, unlike start() and the follow toggle: an omitted seed means "keep what the
	 * map already has" (see FollowCameraSeed), so the tilt, rotation and zoom the user set before opening
	 * the photo survive it. Re-applying the base framing here reset their view on every photo they
	 * glanced at. The centre still snaps back to the rider on the updateFrame below, and the R key still
	 * resets to the framing start() registered — that lives on CameraControls, not in this call.
	 */
	restoreFollowAfterModal(): void {
		if (!this._followSuspended) return;
		this._followSuspended = false;
		if (!this.followMode) return;
		engageFollow(this.map, this.cameraControls);
		this._shell.resetCameraTracking();
		this.updateFrame(Math.floor(this.currentIndex));
		this._shell.syncIdleCamera();
	}

	toggleFollow(): void {
		this.setFollow(!this.followMode);
	}

	getFollowMode(): boolean {
		return this.followMode;
	}

	// ================================================================
	// Animation ticks — the shell owns the frame clock; these advance the
	// playhead per frame and delegate to replay-engine-base helpers.
	// Return true on completion (shell stops + idles camera + fires onComplete).
	// ================================================================

	_tick(dtMs: number, speed: number): boolean {
		return this.mode === "time"
			? this._tickTime(dtMs, speed)
			: this._tickDistance(dtMs, speed);
	}

	_tickDistance(dtMs: number, speed: number): boolean {
		const track = this.cameraTrack;
		if (!track) return false;

		const kmPerSec = (1 / this.BASE_SECONDS_PER_KM) * speed;
		const advanceKm = (kmPerSec * dtMs) / 1000;

		const result = advanceByDistance(
			track,
			this.currentIndex,
			advanceKm,
		);
		this.currentIndex = result.index;

		if (result.finished) {
			this.currentIndex = track.length - 1;
			this.updateFrame(Math.floor(this.currentIndex));
			return true;
		}

		this.updateFrame(Math.floor(this.currentIndex));
		return false;
	}

	_tickTime(dtMs: number, speed: number): boolean {
		const track = this.cameraTrack;
		if (!track) return false;

		this.currentTime += (dtMs / 1000) * speed;

		if (this.currentTime >= this.totalTimeSeconds) {
			this.currentTime = this.totalTimeSeconds;
			this.currentIndex = track.length - 1;
			this.updateFrame(Math.floor(this.currentIndex));
			return true;
		}

		const result = advanceByTime(
			track,
			this.currentTime,
			this.currentIndex,
		);
		this.currentIndex = result.index;

		if (result.finished) {
			this.currentIndex = track.length - 1;
			this.updateFrame(Math.floor(this.currentIndex));
			return true;
		}

		this.updateFrame(Math.floor(this.currentIndex));
		return false;
	}

	// ================================================================
	// Frame update — marker + camera (delegates to replay-engine-base helpers)
	// ================================================================

	updateFrame(cameraIndex: number): void {
		const track = this.cameraTrack;
		if (!track || cameraIndex < 0 || cameraIndex >= track.length) return;

		const pos = interpolatePosition(this.originalTrack, this.currentIndex);
		if (!pos) return;

		const mLng = pos.lng,
			mLat = pos.lat;

		// Update GeoJSON marker position (single setData() — no DOM reprojection)
		this._setMarkerPosition(mLng, mLat);

		// Camera — center = marker position, but only while this replay actually owns the camera. In
		// free look the user's view holds still and the marker moves through it; while a modal has the
		// camera (the photo viewer), a seek must move the playhead WITHOUT yanking the view off the
		// photo the user is looking at.
		if (this.followMode && !this._followSuspended) {
			this._shell.applyCamera(this.map, { center: [mLng, mLat] as [number, number] });
		}

		// Progress callback — include current point data for UI display
		const progress = (Math.floor(cameraIndex) / Math.max(1, this.originalTrack.length - 1)) * 100; // RP-09: single-point guard
		const currentPoint = this.originalTrack[Math.floor(this.currentIndex)];
		if (this.onProgress && currentPoint !== undefined) {
			this.onProgress(progress, Math.floor(this.currentIndex), currentPoint, this.currentIndex);
		}
	}

	// ================================================================
	// Lifecycle
	// ================================================================

	complete(): void {
		this._shell.pause();
		if (this.onComplete) this.onComplete();
	}

	destroy(): void {
		this._shell.destroy(); // stop the frame + idle loop without pause()'s idle-restart (P-A6)
		// releaseFollow (not a bare deactivate) so the native gesture handlers come back on — and
		// un-anchored — if we tore down mid-follow. A bare deactivate would leave dragPan disabled.
		releaseFollow(this.map, this.cameraControls);
		this._removeMarker();
		ActivityReplay.setBlueMarkerVisibility(true);
		setReplayActive(false);
		try {
			this.map.easeTo({ pitch: 0, bearing: 0, duration: 1000 });
		} catch (_e) { /* teardown: may already be removed */ }
	}
}

