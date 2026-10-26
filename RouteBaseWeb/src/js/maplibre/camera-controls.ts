// camera-controls.ts — the follow camera's idle loop, plus the input MapLibre cannot provide.
//
// While following, MapLibre owns zoom/bearing/pitch (see core/replay-follow-camera.ts) and this
// class owns exactly three things:
//   - the rAF loop that keeps the followed point centred, through a writer the follow seam installs
//   - 1-finger touch rotate + tilt, delegated to TouchGestureHandler: MapLibre has no 1-finger
//     equivalent once dragPan is off, and dragPan must be off because the follow owns the centre
//   - the arrow / +- / R keys, because MapLibre's own keyboard handler pans (and eases), so the
//     follow disables it
//
// Everything else — pinch, twist, 2-finger tilt, wheel zoom, right-drag rotate — is MapLibre's
// again. The offset model this class used to carry (userZoom/Bearing/PitchOffset +
// applyUserAdjustments) is gone; the header of core/replay-follow-camera.ts says why.

import type { ResetFraming } from '../core/core-types';
import { TouchGestureHandler } from './touch-gesture-handler';

export class CameraControls {
	private map: maplibregl.Map;

	isActive: boolean;

	// Key steps (fixed defaults; public for readability).
	KEY_BEARING_STEP: number;
	KEY_PITCH_STEP: number;
	KEY_ZOOM_STEP: number;

	// Idle loop state — runs whenever a follow is engaged.
	private _idleFrameId: number | null;
	// Stable rAF callback — `.bind(this)` per frame allocated a fresh closure 60×/s while paused.
	private readonly _boundRunIdleFrame = (): void => this._runIdleFrame();
	private _getCameraCenter: (() => maplibregl.LngLatLike) | null;
	/** Per-frame centre writer, installed by the follow seam (core/replay-follow-camera) — which owns
	 * the gate and the change detection. Null between a deactivate and the next engage. */
	private _writeCenter: ((center: maplibregl.LngLatLike) => void) | null;

	/** Set by the follow seam at engage; null until then, which makes reset() a no-op. */
	private _resetFraming: ResetFraming | null;

	// Touch gesture handler — 1-finger rotate/tilt only.
	private _touchHandler: TouchGestureHandler | null;

	private _boundHandlers: {
		keyDown: (e: KeyboardEvent) => void;
	};

	constructor(map: maplibregl.Map) {
		this.map = map;

		this.isActive = false;

		this.KEY_BEARING_STEP = 5;
		this.KEY_PITCH_STEP = 3;
		this.KEY_ZOOM_STEP = 0.1;

		this._idleFrameId = null;
		this._getCameraCenter = null;
		this._writeCenter = null;

		this._resetFraming = null;

		this._touchHandler = new TouchGestureHandler(map);

		this._boundHandlers = {
			keyDown: this._handleKeyDown.bind(this),
		};
	}

	// ==================== Lifecycle ====================

	activate(): void {
		if (this.isActive) return;
		this.isActive = true;
		document.addEventListener("keydown", this._boundHandlers.keyDown);
		this._touchHandler?.activate();
	}

	deactivate(): void {
		if (!this.isActive) return;
		this.isActive = false;
		this._stopIdleLoop();
		// Releasing the canvas drops the writer with it: the next engage installs a fresh one (fresh
		// change detector), so the first frame after a re-engage always writes (LV-05).
		this._writeCenter = null;
		document.removeEventListener("keydown", this._boundHandlers.keyDown);
		this._touchHandler?.deactivate();
	}

	// ==================== Idle loop ====================

	startIdleLoop(getCameraCenter: () => maplibregl.LngLatLike): void {
		this._getCameraCenter = getCameraCenter;
		if (this._idleFrameId) return;
		this._runIdleFrame();
	}

	/**
	 * Install the per-frame centre writer (LV-05). engageFollow installs a fresh one — with a fresh
	 * change detector, so the first frame after a re-engage always writes — before it activates.
	 */
	setFollowCenterWriter(write: ((center: maplibregl.LngLatLike) => void) | null): void {
		this._writeCenter = write;
	}

	stopIdleLoop(): void {
		this._stopIdleLoop();
	}

	private _stopIdleLoop(): void {
		if (this._idleFrameId) {
			cancelAnimationFrame(this._idleFrameId);
			this._idleFrameId = null;
		}
		this._getCameraCenter = null;
	}

	private _runIdleFrame(): void {
		if (!this.isActive) {
			this._idleFrameId = null;
			return;
		}
		const center = this._getCameraCenter ? this._getCameraCenter() : null;
		if (!center) {
			this._idleFrameId = null;
			return;
		}

		// No writer ⇒ nothing to do. activate() is only reachable through engageFollow, which installs
		// one first, so this is the deactivated-mid-frame case rather than a missing default.
		this._writeCenter?.(center);

		this._idleFrameId = requestAnimationFrame(this._boundRunIdleFrame);
	}

	// ==================== Reset framing ====================

	/** Told by the follow seam what this follow was framed with (engage-time zoom/bearing/pitch). */
	setResetFraming(framing: ResetFraming | null): void {
		this._resetFraming = framing;
	}

	/** Back to the framing the follow engaged with. No framing recorded ⇒ nothing to go back to. */
	reset(): void {
		if (!this._resetFraming) return;
		try {
			this.map.jumpTo({ ...this._resetFraming });
		} catch (_e) { /* defensive: map may be disposed */ }
	}

	// ==================== Keys ====================

	private _handleKeyDown(e: KeyboardEvent): void {
		if (!this.isActive) return;
		const target = e.target as HTMLElement | null;
		if (
			target &&
			(target.tagName === "INPUT" ||
				target.tagName === "TEXTAREA" ||
				target.tagName === "SELECT")
		)
			return;

		switch (e.key) {
			case "ArrowLeft":
				this._nudge(e, { bearing: -this.KEY_BEARING_STEP });
				break;
			case "ArrowRight":
				this._nudge(e, { bearing: this.KEY_BEARING_STEP });
				break;
			case "ArrowUp":
				this._nudge(e, { pitch: this.KEY_PITCH_STEP });
				break;
			case "ArrowDown":
				this._nudge(e, { pitch: -this.KEY_PITCH_STEP });
				break;
			case "+":
			case "=":
				this._nudge(e, { zoom: this.KEY_ZOOM_STEP });
				break;
			case "-":
			case "_":
				this._nudge(e, { zoom: -this.KEY_ZOOM_STEP });
				break;
			case "r":
			case "R":
				e.preventDefault();
				this.reset();
				break;
		}
	}

	/**
	 * Apply a relative camera nudge. Read-then-write against the live map, so the keys compose with
	 * whatever the user's gestures have already done. MapLibre's transform clamps zoom and pitch to
	 * the map's own min/max, exactly as it would outside a follow.
	 */
	private _nudge(e: KeyboardEvent, delta: { zoom?: number; bearing?: number; pitch?: number }): void {
		e.preventDefault();
		try {
			const next: { zoom?: number; bearing?: number; pitch?: number } = {};
			if (delta.zoom !== undefined) next.zoom = this.map.getZoom() + delta.zoom;
			if (delta.bearing !== undefined) next.bearing = this.map.getBearing() + delta.bearing;
			if (delta.pitch !== undefined) next.pitch = this.map.getPitch() + delta.pitch;
			this.map.jumpTo(next);
		} catch (_e) { /* defensive: map may be disposed */ }
	}
}
