/**
 * replay-playback-shell.ts — shared replay playback transport (CR1-01)
 *
 * Single source for the frame clock, rAF scheduling, pause/resume/completion
 * semantics, the speed clamp, and the camera change-detector + terrain-elevation
 * pairing that activity-replay.ts and segment-stats-replay.ts previously
 * hand-rolled identically (the RP-01/02/04 ↔ RS2-05/02/04 twin-defect layer).
 *
 * Factory style — framework-agnostic, consumable by an ES6 class (chain A) and
 * a factory-closure (chain B) alike. The shell owns nothing of the playhead:
 * domain advancement stays in the consumers behind `tick(dtMs, speed)`.
 */
import type { CameraControlsInstance } from './core-types';
import { updateCamera } from './replay-engine-base';
import { createCameraChangeDetector } from './map-utils-shared';

export interface ReplayShellHooks {
	/** Advance the domain playhead by dtMs at speed and render the frame; return true on completion. */
	tick(dtMs: number, speed: number): boolean;
	/** Playhead sits at the end (all efforts finished / clock >= total). Drives the resume() restart. */
	isAtEnd(): boolean;
	/** Rewind the domain playhead to 0 (atEnd restart). Camera tracking is reset by the shell itself. */
	restart(): void;
	/** Gate for the paused-camera idle loop: A = () => true; B = () => followEffortIndex >= 0. */
	wantsIdleLoop(): boolean;
	/** Per-frame idle-loop camera center (current marker position; map center before the first frame). */
	idleCenter(): maplibregl.LngLatLike;
	/** LIVE camera-controls accessor — B nulls/recreates its instance per session. */
	getCameraControls(): CameraControlsInstance | null;
	/** Completion callback (UI icon flip etc.). Fired once per completion. */
	onComplete(): void;
	/** Start the paused idle loop as part of the completion stop. A: true (pinned "look around at the
	 *  finish"); B: false (pinned current behavior — see CR1-07). Explicit, no default. */
	idleOnComplete: boolean;
}

export interface ReplayPlaybackShell {
	beginPlayback(): void;      // isPlaying=true, lastFrameTime=now, run first frame (A start, B start)
	pause(): void;              // stop frame; start idle loop when wantsIdleLoop() (RP-02/RS2-02)
	resume(): void;             // no-op while playing; stop idle loop; atEnd → restart()+resetCameraTracking() (RP-04/RS2-04)
	togglePlayPause(): boolean; // returns new isPlaying
	isPlaying(): boolean;
	setSpeed(mult: number): void;   // clamp 0.1..1000 (single clamp site)
	getSpeed(): number;
	syncIdleCamera(): void;     // start idle loop iff !isPlaying && wantsIdleLoop() (B follow-toggled-while-paused)
	stopIdleCamera(): void;     // getCameraControls()?.stopIdleLoop() (A destroy keeps its pinned explicit stop)
	/**
	 * Full teardown. Stops the frame and the idle loop WITHOUT the pause()→_maybeStartIdle() restart that
	 * pause() does — pause() re-arms the paused-camera idle loop, so `pause()` as a teardown leaves an rAF
	 * writing camera centres to a map that is about to be nulled (the segment-stats leak, RS2-class). This
	 * is the single safe teardown for all three replays; call it instead of pause()+stopIdleCamera().
	 */
	destroy(): void;
	resetCameraTracking(): void;    // detector.reset() + terrainElevState.lastTime = 0
	/** Centre only — zoom/bearing/pitch belong to MapLibre once a follow is engaged. */
	applyCamera(map: maplibregl.Map, params: { center: [number, number] }): void;
}

export function createReplayPlaybackShell(hooks: ReplayShellHooks): ReplayPlaybackShell {
	let isPlaying = false;
	let speed = 1.0;
	let frameId: number | null = null;
	let lastFrameTime: number | null = null;
	const detector = createCameraChangeDetector();
	const terrainElevState: { lastTime: number } = { lastTime: 0 };

	function _stopFrame(): void {
		if (frameId) {
			cancelAnimationFrame(frameId);
			frameId = null;
		}
	}

	function _maybeStartIdle(): void {
		const c = hooks.getCameraControls();
		if (c && hooks.wantsIdleLoop()) c.startIdleLoop(hooks.idleCenter);
	}

	function _loop(): void {
		if (!isPlaying) return;
		const now = performance.now();
		const dt = lastFrameTime ? now - lastFrameTime : 16;
		lastFrameTime = now;
		if (hooks.tick(dt, speed)) {
			// Completion: stop + per-chain idle policy + onComplete once (matrix #6).
			_stopFrame();
			isPlaying = false;
			if (hooks.idleOnComplete) _maybeStartIdle();
			hooks.onComplete();
			return;
		}
		frameId = requestAnimationFrame(_loop);
	}

	function resetCameraTracking(): void {
		detector.reset();
		terrainElevState.lastTime = 0;
	}

	function pause(): void {
		isPlaying = false;
		_stopFrame();
		// RP-02/RS2-02: while paused, keep re-applying the user's camera offsets each frame
		// via the idle loop — otherwise the map is frozen and the offsets all snap on resume.
		_maybeStartIdle();
	}

	function resume(): void {
		if (isPlaying) return;
		hooks.getCameraControls()?.stopIdleLoop();
		// RP-04/RS2-04: after completion the playhead sits at the end — resume would instantly
		// re-complete and wedge the play button. Restart from 0 instead.
		if (hooks.isAtEnd()) {
			hooks.restart();
			resetCameraTracking();
		}
		isPlaying = true;
		lastFrameTime = performance.now();
		_loop();
	}

	return {
		beginPlayback: (): void => {
			isPlaying = true;
			lastFrameTime = performance.now();
			_loop();
		},
		pause,
		resume,
		togglePlayPause: (): boolean => {
			if (isPlaying) pause();
			else resume();
			return isPlaying;
		},
		isPlaying: (): boolean => isPlaying,
		setSpeed: (mult: number): void => {
			speed = Math.max(0.1, Math.min(1000, mult));
		},
		getSpeed: (): number => speed,
		syncIdleCamera: (): void => {
			if (!isPlaying) _maybeStartIdle();
		},
		stopIdleCamera: (): void => {
			hooks.getCameraControls()?.stopIdleLoop();
		},
		destroy: (): void => {
			// Stop the frame directly — NOT via pause(), which would _maybeStartIdle() the very loop we
			// are tearing down. Then stop any idle loop already running.
			isPlaying = false;
			_stopFrame();
			hooks.getCameraControls()?.stopIdleLoop();
		},
		resetCameraTracking,
		applyCamera: (map: maplibregl.Map, params: { center: [number, number] }): void => {
			updateCamera(map, params, detector, terrainElevState);
		},
	};
}
