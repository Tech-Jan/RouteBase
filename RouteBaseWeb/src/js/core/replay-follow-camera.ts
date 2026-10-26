// replay-follow-camera.ts — the one place that decides who owns the map camera during a follow.
// Shared by /Activities (activity-replay), /Segments/Statistics (segment-stats-replay),
// /Races/Replay (race-replay-view — owner page and public share fork) and the live pages
// (live-recording-map, mounted by both /LiveView and the /s/{token} live share). Extracted from
// three copies that had already drifted (LV-04).
//
// THE RULE: while following, MapLibre owns the camera and the follow owns only the CENTRE.
//
// Zoom, bearing and pitch belong to MapLibre's own gesture handlers from engage onwards. That
// replaced an offset model (an auto camera plus userZoom/Bearing/PitchOffset, re-applied every
// frame) whose job was to stop a *moving* auto camera from wiping user input — but no page ever
// moved anything except the centre, so the offsets bought nothing and cost us MapLibre's 2-finger
// pitch, the one touch gesture the hand-written replacement never implemented.

import type { CameraControlsInstance } from './core-types';
import { gestureHandler, setTransformCameraUpdate, type GestureHandlerInternal } from './map-internals';
import { applyTerrainElevation, collapseMapAttribution, withHandlersShielded } from './map-utils-shared';

/** Turned OFF while following: all three move the CENTRE, the one thing the follow owns. */
const CENTRE_MOVING_HANDLERS = ['dragPan', 'doubleClickZoom', 'keyboard'] as const;

/**
 * Kept, but re-anchored. `around: 'center'` makes a pinch or a wheel zoom orbit the followed
 * marker instead of the finger midpoint / pointer, which would otherwise slide the centre off the
 * marker mid-gesture. MapLibre latches the option at enable() time, so releasing the follow has to
 * re-enable these WITHOUT it or the free camera keeps zooming about the centre.
 */
const CENTRE_ANCHORED_HANDLERS = ['scrollZoom', 'touchZoomRotate'] as const;

/** Kept as-is: both already orbit the centre (dragRotate's aroundCenter defaults to true). */
const ORBIT_HANDLERS = ['touchPitch', 'dragRotate'] as const;

/** Every handler this module touches — the set a release hands back. */
export const FOLLOW_HANDLERS = [
	...CENTRE_MOVING_HANDLERS, ...CENTRE_ANCHORED_HANDLERS, ...ORBIT_HANDLERS,
] as const;

// ─── The centre pin ─────────────────────────────────────────────────────────────────────────────
//
// Two things keep the marker centred, and between them they cover every frame:
//
//   1. `transformCameraUpdate` — MapLibre's own seam, called from `Camera._applyUpdatedTransform` on
//      every camera update, the handler-driven ones included. Forcing the centre from inside it pins
//      the marker THROUGH a pinch, a wheel zoom or a right-drag orbit, while the handler's own
//      zoom/bearing/pitch deltas survive untouched. That path calls `_stop(true)` (allowGestures), so
//      nothing installed here can reset a handler.
//   2. Our own per-frame `jumpTo`, for the frames MapLibre updates nothing at all. That is not a rare
//      case: `HandlerManager._updateMapTransform` early-returns on `!hasChange`, and `_changes` is
//      only pushed when a handler produced a delta — so a button held STILL mid-drag schedules no
//      frame and calls no hook, and the pin goes cold until the user moves again. A whole follow that
//      stood back for the duration of a gesture is exactly how that shipped as a bug.
//
// The rule at the top of this file is unchanged — the follow still owns only the centre.
//
// Writer 2 stands back for exactly one thing, an in-flight EASE; shouldYieldToEase says why.

/** Everything the follow knows about one map: where the centre belongs, and who is holding it. */
interface FollowState {
	lng: number;
	lat: number;
	/** False until the first frame reports a position — before that the hook has nothing to force. */
	set: boolean;
	/** True while the camera hook is installed — standing back for an ease rests on it. */
	hooked: boolean;
}

/** One record per followed map. Module-private: engage/release own its lifetime. */
const followStates = new WeakMap<maplibregl.Map, FollowState>();

/**
 * Tell the follow where the centre belongs. Ungated and cheap — it writes three numbers; the camera
 * hook and the gated jumpTo are what act on them. A no-op when no follow is engaged on `map`, so a
 * free-look page can call it without checking.
 */
export function pinFollowCentre(map: maplibregl.Map, lng: number, lat: number): void {
	const state = followStates.get(map);
	if (!state) return;
	state.lng = lng;
	state.lat = lat;
	state.set = true;
}

/**
 * The follow's centre write, and the ONLY safe way to make it.
 *
 * A plain `map.jumpTo()` calls `stop()` → `handlers.stop()` → `reset()` on every gesture handler,
 * and `reset()` does `delete this._lastPoint`. Since `dragMove` bails without it, one unshielded
 * write mid-drag kills the drag until the button is released and pressed again — first seen in the
 * PENDING window (between mousedown and the first move past the 1 px clickTolerance, when nothing
 * yet reports a gesture), but it applies to every frame of every drag.
 *
 * `HandlerManager.stop()` opens with `if (this._updatingCamera) return`, its guard for "this stop
 * came from a gesture update, do not undo it". Borrowing that flag makes our write equally invisible
 * to the handlers, and that is what lets the follow write straight THROUGH a live gesture rather than
 * withholding for it. If MapLibre ever renames the field the assignment is simply inert and we are
 * back to the old behaviour — not broken, just unshielded.
 *
 * The shield does NOT cover `Camera._stop`'s ease cancellation, which is the one thing
 * shouldYieldToEase stands back for.
 *
 * NOT used for the engage-time framing or the R-key reset: those are meant to interrupt.
 */
export function writeFollowCentre(map: maplibregl.Map, center: maplibregl.LngLatLike): void {
	withHandlersShielded(map, () => {
		try {
			map.jumpTo({ center });
		} catch (_e) { /* defensive: the map may be disposed mid-frame */ }
	});
}

/** MapLibre's LngLat, off the same UMD global the pages build their map from. */
function lngLatCtor(): (new (lng: number, lat: number) => maplibregl.LngLat) | null {
	const ctor = (globalThis as { maplibregl?: { LngLat?: unknown } }).maplibregl?.LngLat;
	return typeof ctor === 'function'
		// cast: the UMD global's LngLat class — typeof-function checked one line above
		? (ctor as new (lng: number, lat: number) => maplibregl.LngLat)
		: null;
}

/**
 * Register the follow state and install the camera hook if it can be.
 *
 * `hooked` records whether the hook actually went in, because standing back for an ease is granted on
 * the strength of the hook holding the centre meanwhile. Claiming that without a hook would abandon
 * the marker for the ease's duration.
 */
function installFollowState(map: maplibregl.Map): void {
	const state: FollowState = { lng: 0, lat: 0, set: false, hooked: false };
	followStates.set(map, state);

	const LngLat = lngLatCtor();
	// No LngLat ⇒ no hook. `ITransform.setCenter` STORES the object it is handed and MapLibre then
	// calls LngLat methods on it, so a plain {lng,lat} would break the map further down.
	if (!LngLat) return;

	try {
		setTransformCameraUpdate(map,
			(next): { center?: maplibregl.LngLat } => {
				if (!state.set) return {};
				const c = next?.center;
				if (c && c.lng === state.lng && c.lat === state.lat) return {};
				// A FRESH LngLat is required: `transform.center` hands back the transform's own object and
				// setCenter early-returns on value equality, so mutating it in place silently no-ops.
				return { center: new LngLat(state.lng, state.lat) };
			});
	} catch (e: unknown) {
		console.warn('[replay-follow-camera] camera hook install failed:', e);
		return;
	}
	state.hooked = true;
}

/** Drop the hook and the state — a released map is a plain interactive map again. */
function removeFollowState(map: maplibregl.Map): void {
	followStates.delete(map);
	try {
		setTransformCameraUpdate(map, null);
	} catch (e: unknown) {
		console.warn('[replay-follow-camera] camera hook removal failed:', e);
	}
}

/**
 * Ask, once per frame, whether the follow's own centre write must stand back.
 *
 * Exactly one state says yes: an in-flight EASE — the inertia after a flick-rotate, or a page's own
 * flyTo. `map.jumpTo` opens with `stop()`, and `Camera._stop` cancels `_easeFrameId`; that is the one
 * thing the handler shield does NOT cover, so writing every frame would kill the ease outright. The
 * camera hook holds the centre for its whole duration, so standing back costs nothing.
 *
 * A live GESTURE is deliberately NOT a reason to yield, which is the whole point of this predicate.
 * writeFollowCentre borrows `handlers._updatingCamera`, and `HandlerManager.stop()` early-returns on
 * it, so the write cannot reset a drag or a pinch. Yielding for one used to abandon the marker the
 * moment a held button stopped moving — no delta, no camera update, so the hook never fired either.
 *
 * No hook installed ⇒ nothing would hold the centre while we stand back ⇒ write anyway.
 */
export function shouldYieldToEase(map: maplibregl.Map): boolean {
	if (!followStates.get(map)?.hooked) return false;
	try {
		return typeof map.isEasing === 'function' && map.isEasing();
	} catch (_e) {
		return false; // a disposed or faked map is not easing
	}
}

/**
 * Run one handler operation. Per-handler try/catch (the most defensive of the three originals):
 * a throw in one handler must not leave the map inert by skipping the others.
 */
function withHandler(
	map: maplibregl.Map,
	name: string,
	op: (handler: GestureHandlerInternal) => void,
): void {
	try {
		op(gestureHandler(map, name));
	} catch (e: unknown) {
		console.warn(`[replay-follow-camera] ${name} toggle failed:`, e);
	}
}

/** Take the gestures the follow needs and re-anchor the ones it keeps. */
export function engageFollowHandlers(map: maplibregl.Map): void {
	for (const name of CENTRE_MOVING_HANDLERS) withHandler(map, name, (h) => h.disable());
	for (const name of CENTRE_ANCHORED_HANDLERS) {
		withHandler(map, name, (h) => h.enable({ around: 'center' }));
	}
	for (const name of ORBIT_HANDLERS) withHandler(map, name, (h) => h.enable());
	setCanvasTouchAction(map, 'none');
}

/** Hand every handler back, un-anchored — a plain interactive map again. */
export function releaseFollowHandlers(map: maplibregl.Map): void {
	for (const name of FOLLOW_HANDLERS) withHandler(map, name, (h) => h.enable());
	setCanvasTouchAction(map, '');
}

/**
 * Keep the browser's own scrolling off the canvas for the duration of a follow.
 *
 * MapLibre earns `touch-action: none` with a PAIR of classes — its stylesheet only applies it when
 * `maplibregl-touch-zoom-rotate` and `maplibregl-touch-drag-pan` are both present, and
 * `DragPanHandler.disable()` removes the second one. So turning dragPan off above (the follow owns
 * the centre, so pan must go) silently drops the container to `touch-action: pan-x pan-y`, and a
 * one-finger drag can scroll the page instead of driving the map.
 *
 * TouchGestureHandler used to paper over that with `preventDefault()` on touchstart/touchmove — but
 * cancelling those suppresses the browser's compatibility mouse events, `click` among them, so on a
 * phone nothing on the map could be TAPPED during a replay (photo pins, clusters). Declaring the
 * intent here instead fixes the cause and lets the touch handler stop cancelling events.
 *
 * An inline style outranks the stylesheet; clearing it hands the class-driven value back.
 */
function setCanvasTouchAction(map: maplibregl.Map, value: 'none' | ''): void {
	try {
		const container = typeof map.getCanvasContainer === 'function' ? map.getCanvasContainer() : null;
		if (container) container.style.touchAction = value;
	} catch (e: unknown) {
		console.warn('[replay-follow-camera] touch-action toggle failed:', e);
	}
}

/**
 * Where the follow starts. Every field is optional and an omitted one means "keep what the map
 * already has" — that is the /LiveView contract: locking on, or switching rider, must recentre
 * WITHOUT snapping zoom/bearing/pitch. The replay pages pass their own base-framing constants
 * instead, so locking on re-frames onto the target, and the map's getters are never consulted
 * (/Segments/Statistics depends on that).
 *
 * Applied ONCE, at engage. From then on zoom/bearing/pitch are MapLibre's alone.
 */
export interface FollowCameraSeed {
	center?: maplibregl.LngLatLike | undefined;
	zoom?: number | undefined;
	bearing?: number | undefined;
	pitch?: number | undefined;
}

/** Take the canvas: handler policy applied, framing set, the centre writer installed. */
export function engageFollow(
	map: maplibregl.Map,
	camera: CameraControlsInstance,
	seed?: FollowCameraSeed,
): void {
	engageFollowHandlers(map);

	// Taking dragPan away takes MapLibre's only attribution auto-collapse trigger with it — so do
	// that collapse here, at the moment the gesture that used to do it stops being possible.
	collapseMapAttribution(map);

	// Before the framing write, so the hook is live from the very first camera update. It stays
	// inert until the first pinFollowCentre, so it cannot fight the seed below.
	installFollowState(map);

	applyFollowFraming(map, camera, seed);

	// Fresh writer per engage ⇒ fresh detector ⇒ the first frame after a re-engage always writes.
	camera.setFollowCenterWriter(createFollowCenterWriter(map));
	camera.activate();
}

/** Release the canvas: CameraControls deactivated (which stops its idle loop), handlers back on. */
export function releaseFollow(map: maplibregl.Map, camera: CameraControlsInstance | null): void {
	// finally, not a shared try (RS2-07): if deactivate() throws — e.g. getCanvas() on a disposed map —
	// the handlers must still come back, or pan/zoom stays frozen on a still-live map.
	try {
		camera?.deactivate();
	} finally {
		removeFollowState(map);
		releaseFollowHandlers(map);
	}
}

/**
 * Put the camera where the follow starts, once, and remember it as what the R key resets to.
 * Only the fields the caller left out are read back from the map.
 */
function applyFollowFraming(
	map: maplibregl.Map,
	camera: CameraControlsInstance,
	seed?: FollowCameraSeed,
): void {
	if (!seed) return; // no seed, no framing change — and no camera getters touched

	const framing = {
		zoom: seed.zoom ?? map.getZoom(),
		bearing: seed.bearing ?? map.getBearing(),
		pitch: seed.pitch ?? map.getPitch(),
	};
	camera.setResetFraming(framing);
	try {
		map.jumpTo(seed.center ? { ...framing, center: seed.center } : framing);
	} catch (e: unknown) {
		console.warn('[replay-follow-camera] framing jumpTo failed:', e);
	}
}

/**
 * The follow's per-frame job, and its only write to the map: put the followed point back in the
 * centre. Zoom, bearing and pitch are never touched — they are MapLibre's.
 *
 * Guarded by shouldYieldToEase above, and by a change check: exact inequality, deliberately NOT
 * createCameraChangeDetector. That detector's ~1e-10 deg² (≈1 m²) threshold is LARGER than the
 * 0.08 m a 5 m/s rider covers in one 16 ms frame, so reusing it here would step the camera roughly
 * every 12th frame — visible stutter. The followed position only moves when a new fix or a new
 * interpolated frame lands, so an exact compare kills the no-op frames (at LiveView's 1 Hz fix
 * rate, ~59 of every 60) with no motion threshold at all.
 */
function createFollowCenterWriter(map: maplibregl.Map): (center: maplibregl.LngLatLike) => void {
	let seen = false;
	let lng = 0, lat = 0;
	const elevState = { lastTime: 0 }; // throttle for the terrain-elevation re-clamp below

	return (center: maplibregl.LngLatLike): void => {
		const [x, y] = lngLatOf(center);

		// Ungated, and FIRST: the frames we stand back on are exactly the frames the camera hook is
		// doing the following, and it can only do that if it knows where the marker is.
		pinFollowCentre(map, x, y);

		if (shouldYieldToEase(map)) return;

		if (!(seen && x === lng && y === lat)) {
			seen = true;
			lng = x; lat = y;
			writeFollowCentre(map, center);
		}

		// Re-clamp the focal point onto the terrain EVERY frame (throttled to ~5fps internally), not
		// only on a position change: when the followed point is stationary the jumpTo above is
		// suppressed, yet DEM tiles keep loading — without this the camera stays aimed at the stale
		// (often sea-level) elevation and the marker floats above centre. minPitch:1 so the recorder's
		// gentle 10° tilt is corrected (replay follows at 30°, already well above the default gate).
		// It sits BELOW the ease guard on purpose: setCenterElevation is `jumpTo({elevation})`, so an
		// unguarded re-clamp would cancel an in-flight ease ~5×/s on a 3D basemap.
		applyTerrainElevation(map, x, y, elevState, undefined, 1);
	};
}

/** LngLatLike → [lng, lat]. The follow pages feed all of tuple, LngLat and {lng,lat}. */
function lngLatOf(c: maplibregl.LngLatLike): [number, number] {
	if (Array.isArray(c)) return [c[0], c[1]];
	const o = c as { lng?: number; lon?: number; lat: number };
	return [o.lng ?? o.lon ?? 0, o.lat];
}
