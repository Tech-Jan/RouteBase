// touch-gesture-handler.ts — 1-finger rotate + tilt for the follow camera.
//
// This is the ONLY touch gesture the follow implements. Everything multi-touch — pinch zoom,
// twist rotate, 2-finger tilt — is MapLibre's again: the follow leaves those handlers enabled and
// centre-anchored (see core/replay-follow-camera.ts). 1 finger is ours because MapLibre's
// 1-finger touch gesture is pan, and pan is off while the follow owns the centre.
//
// Writes the map camera directly — one jumpTo per move, bearing and pitch together. MapLibre's
// transform clamps pitch to the map's own min/max, so there is nothing to clamp here.
//
// It does NOT call preventDefault. It used to, on touchstart and touchmove, to stop the page
// scrolling under a one-finger drag — but cancelling those events also suppresses the browser's
// compatibility mouse events, `click` included, so nothing on the map could be TAPPED during a
// replay on a phone (photo pins and clusters are map layers reached through `map.on('click', …)`).
// Scroll suppression is now declared where it belongs, as `touch-action: none` on the canvas
// container for the duration of the follow — see core/replay-follow-camera.setCanvasTouchAction.
//
// Uses import type nowhere now: the old offset-writing coupling to CameraControls is gone.

import { withHandlersShielded } from "../core/map-utils-shared";

/** Internal 2D point used for touch tracking. */
interface TouchPoint {
	x: number;
	y: number;
}

/**
 * Pixels a finger must travel before this becomes a rotate rather than a tap. Mirrors MapLibre's own
 * `clickTolerance`: without it a tap that wobbles by a pixel nudges the camera, which reads as the
 * map twitching every time you try to open a photo.
 */
const MOVE_TOLERANCE_PX = 3;

export class TouchGestureHandler {
	private _map: maplibregl.Map;
	private _el: HTMLElement;
	private _active: boolean;

	private _touches: Map<number, TouchPoint>;
	private _lastSingleX: number;
	private _lastSingleY: number;
	/** A multi-touch gesture belongs to MapLibre — stay out of the way until every finger lifts. */
	private _yieldedToMultiTouch: boolean;
	/** False until this touch clears MOVE_TOLERANCE_PX — until then it is still a candidate tap. */
	private _movedEnough: boolean;

	// Touch gesture sensitivity constants (fixed defaults; kept public for readability).
	DRAG_BEARING_SENSITIVITY: number;
	DRAG_PITCH_SENSITIVITY: number;

	// Bound handler references — stored once for clean add/remove
	private _onTouchStart: (e: TouchEvent) => void;
	private _onTouchMove: (e: TouchEvent) => void;
	private _onTouchEnd: (e: TouchEvent) => void;

	constructor(map: maplibregl.Map) {
		this._map = map;
		this._el = map.getCanvas() as HTMLElement;
		this._active = false;

		this._touches = new Map<number, TouchPoint>();
		this._lastSingleX = 0;
		this._lastSingleY = 0;
		this._yieldedToMultiTouch = false;
		this._movedEnough = false;

		// Sensitivity — tuned for small screens
		this.DRAG_BEARING_SENSITIVITY = 0.35;
		this.DRAG_PITCH_SENSITIVITY = 0.25;

		this._onTouchStart = this._handleTouchStart.bind(this);
		this._onTouchMove = this._handleTouchMove.bind(this);
		this._onTouchEnd = this._handleTouchEnd.bind(this);
	}

	// ==================== Lifecycle ====================

	activate(): void {
		if (this._active) return;
		this._active = true;
		this._resetGestureState();

		// Passive: nothing here cancels an event any more, and a listener that promises not to lets the
		// browser stop waiting on it. Scrolling is held off by touch-action, not by preventDefault.
		this._el.addEventListener("touchstart", this._onTouchStart, { passive: true });
		this._el.addEventListener("touchmove", this._onTouchMove, { passive: true });
		this._el.addEventListener("touchend", this._onTouchEnd);
		this._el.addEventListener("touchcancel", this._onTouchEnd);
	}

	deactivate(): void {
		if (!this._active) return;
		this._active = false;
		this._resetGestureState();

		this._el.removeEventListener("touchstart", this._onTouchStart);
		this._el.removeEventListener("touchmove", this._onTouchMove);
		this._el.removeEventListener("touchend", this._onTouchEnd);
		this._el.removeEventListener("touchcancel", this._onTouchEnd);
	}

	private _resetGestureState(): void {
		this._touches.clear();
		this._yieldedToMultiTouch = false;
		this._movedEnough = false;
	}

	// ==================== Touch event handlers ====================

	private _handleTouchStart(e: TouchEvent): void {
		if (!this._active) return;

		for (let i = 0; i < e.changedTouches.length; i++) {
			const t = e.changedTouches[i];
			if (t) this._touches.set(t.identifier, { x: t.clientX, y: t.clientY });
		}

		if (this._touches.size >= 2) {
			this._yieldedToMultiTouch = true;
			return;
		}
		if (!this._ours()) return;

		// No preventDefault: this touch may still turn out to be a tap, and cancelling here would rob
		// it of its click (see the module header). `touch-action: none` handles the scrolling.
		this._movedEnough = false;
		const first = this._touches.values().next().value as TouchPoint;
		this._lastSingleX = first.x;
		this._lastSingleY = first.y;
	}

	private _handleTouchMove(e: TouchEvent): void {
		if (!this._active) return;

		for (let i = 0; i < e.changedTouches.length; i++) {
			const t = e.changedTouches[i];
			if (t && this._touches.has(t.identifier)) {
				this._touches.set(t.identifier, { x: t.clientX, y: t.clientY });
			}
		}

		if (!this._ours()) return;
		this._rotateAndTilt();
	}

	private _handleTouchEnd(e: TouchEvent): void {
		if (!this._active) return;

		for (let i = 0; i < e.changedTouches.length; i++) {
			const t = e.changedTouches[i];
			if (t) this._touches.delete(t.identifier);
		}

		// Only a clean lift releases the multi-touch yield. Resuming on the *first* finger up would
		// hand us the tail of MapLibre's pinch — and our jumpTo resets its handlers mid-gesture.
		if (this._touches.size === 0) {
			this._yieldedToMultiTouch = false;
			return;
		}

		if (this._touches.size === 1 && !this._yieldedToMultiTouch) {
			const remaining = this._touches.values().next().value as TouchPoint;
			this._lastSingleX = remaining.x;
			this._lastSingleY = remaining.y;
			// Re-anchored on a different finger, so it has its own tolerance to clear.
			this._movedEnough = false;
		}
	}

	/** Exactly one finger down, and it did not start life as part of a MapLibre gesture. */
	private _ours(): boolean {
		return this._touches.size === 1 && !this._yieldedToMultiTouch;
	}

	// ==================== Gesture interpretation ====================

	/** 1-finger drag → bearing (horizontal) + pitch (vertical), written straight to the map. */
	private _rotateAndTilt(): void {
		const pt = this._touches.values().next().value as TouchPoint;
		const deltaX = pt.x - this._lastSingleX;
		const deltaY = pt.y - this._lastSingleY;

		// Hold the anchor until the tolerance is cleared, so the whole gesture is measured from where
		// the finger LANDED. Advancing it per-event would let a slow drag creep past in sub-pixel steps
		// that each read as a tap.
		if (!this._movedEnough) {
			if (Math.hypot(deltaX, deltaY) < MOVE_TOLERANCE_PX) return;
			this._movedEnough = true;
		}

		this._lastSingleX = pt.x;
		this._lastSingleY = pt.y;
		if (deltaX === 0 && deltaY === 0) return;

		const map = this._map;
		// Shielded, like every other per-frame camera write in the follow path (writeFollowCentre,
		// applyTerrainElevation): a bare jumpTo runs stop() → handlers.stop() → reset() on EVERY handler.
		// Nothing MapLibre-side is forming during a *pure* 1-finger drag, but the instant the user brings a
		// second finger down to pinch, this frame's reset would wipe the pinch/tilt handler as it starts.
		// Shielding makes the write invisible to the handlers (our own gesture is driven by _touches, not by
		// any handler state, so it is unaffected). See core/map-utils-shared.withHandlersShielded.
		try {
			withHandlersShielded(map, () => {
				map.jumpTo({
					bearing: map.getBearing() + deltaX * this.DRAG_BEARING_SENSITIVITY,
					pitch: map.getPitch() - deltaY * this.DRAG_PITCH_SENSITIVITY,
				});
			});
		} catch (_e) { /* defensive: the map may be disposed mid-gesture */ }
	}
}
