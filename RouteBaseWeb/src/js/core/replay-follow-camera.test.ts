import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
	FOLLOW_HANDLERS, engageFollowHandlers, releaseFollowHandlers,
	engageFollow, releaseFollow, shouldYieldToEase,
} from './replay-follow-camera';
import { CameraControls } from '../maplibre/camera-controls';

// LV-04 / LV-05 — the follow seam, after gestures went back to MapLibre.
//
// The contract these pin is NOT the old one. Follow mode used to mean "all six native handlers off,
// user offsets layered on an auto camera re-applied every frame". It now means "MapLibre owns the
// camera; the follow owns the CENTRE" — so the handler policy is selective, the framing is applied
// once at engage, and every per-frame write stands back while MapLibre is driving. The old
// six-handlers-off and offset assertions pinned a concept that no longer exists, so they were
// rewritten rather than kept green.

/** Disabled while following — all three move the centre. */
const OFF = ['dragPan', 'doubleClickZoom', 'keyboard'] as const;
/** Kept, but re-enabled anchored on the centre so they orbit the followed marker. */
const CENTRED = ['scrollZoom', 'touchZoomRotate'] as const;
/** Kept untouched — already centre-orbiting. */
const ORBIT = ['touchPitch', 'dragRotate'] as const;

function handler(): { enable: ReturnType<typeof vi.fn>; disable: ReturnType<typeof vi.fn> } {
	return { enable: vi.fn(), disable: vi.fn() };
}

/** MapLibre's LngLat, as far as this seam is concerned: a real class, constructed (lng, lat). */
class FakeLngLat {
	constructor(public lng: number, public lat: number) {}
}

interface CentreLike { lng: number; lat: number }
type CameraHook = (next: { center?: CentreLike }) => { center?: CentreLike };

class FakeMap {
	dragPan = handler();
	doubleClickZoom = handler();
	keyboard = handler();
	scrollZoom = handler();
	touchZoomRotate = handler();
	touchPitch = handler();
	dragRotate = handler();

	/** `_updatingCamera` as seen from INSIDE each jumpTo — the only moment HandlerManager reads it. */
	shieldedDuringJump: boolean[] = [];
	jumpTo = vi.fn<(cam: Record<string, unknown>) => void>(() => {
		this.shieldedDuringJump.push(this.handlers._updatingCamera);
		// Real Camera.jumpTo runs _applyUpdatedTransform, so our own write calls the hook as well.
		this.runCameraUpdate();
	});

	/** `map.isEasing()` — an inertia/flyTo frame loop is in flight. The ONE state a write must miss. */
	easing = false;
	isEasing = (): boolean => this.easing;

	/** `handlers.isActive()` — a finger/button is down. Deliberately NOT a reason to stand back. */
	gestureLive = false;
	handlers = { isActive: (): boolean => this.gestureLive, _updatingCamera: false };

	/** MapLibre's Camera.transformCameraUpdate seam; the follow installs itself here. */
	transformCameraUpdate: CameraHook | null = null;

	/** Stand in for MapLibre calling the hook from _applyUpdatedTransform. */
	runCameraUpdate(current: [number, number] = [0, 0]): { center?: CentreLike } {
		if (!this.transformCameraUpdate) return {};
		return this.transformCameraUpdate({ center: new FakeLngLat(current[0], current[1]) });
	}

	private _canvas = document.createElement('canvas');
	private _container = document.createElement('div');
	private _canvasContainer = document.createElement('div');
	getCanvas(): HTMLCanvasElement { return this._canvas; }
	getContainer(): HTMLElement { return this._container; }
	getCanvasContainer(): HTMLElement { return this._canvasContainer; }
	getCenter = vi.fn(() => ({ lng: 9, lat: 47 }));
	getZoom = vi.fn(() => 11);
	getBearing = vi.fn(() => 33);
	getPitch = vi.fn(() => 44);
}

let map: FakeMap;
let rafQueue: FrameRequestCallback[];

/** Run the queued idle frames n times (each frame re-queues the next). */
function pumpFrames(n: number): void {
	for (let i = 0; i < n; i++) {
		const frames = rafQueue.splice(0);
		frames.forEach((cb) => cb(0));
	}
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const asMap = (m: FakeMap): maplibregl.Map => m as any;

/** A follow engaged on a stationary point, with the engage-time framing write already accounted for. */
function engagedAt(pos: () => maplibregl.LngLatLike, seed?: Record<string, number>): CameraControls {
	const camera = new CameraControls(asMap(map));
	engageFollow(asMap(map), camera, { center: pos(), ...seed });
	map.jumpTo.mockClear(); // drop the engage-time framing jump; these assert the per-frame writes
	map.shieldedDuringJump.length = 0;
	camera.startIdleLoop(pos);
	return camera;
}

let nowMs: number;

beforeEach(() => {
	map = new FakeMap();
	rafQueue = [];
	nowMs = 1000;
	vi.spyOn(performance, 'now').mockImplementation(() => nowMs);
	vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => { rafQueue.push(cb); return rafQueue.length; });
	vi.stubGlobal('cancelAnimationFrame', () => {});
	// The follow needs a real LngLat constructor to install its camera hook — the pages get it off the
	// same UMD global they build the map from, so the tests have to supply it or they would silently
	// exercise the no-hook fallback instead of the path that ships.
	vi.stubGlobal('maplibregl', { LngLat: FakeLngLat });
});

afterEach(() => {
	vi.restoreAllMocks();
	vi.unstubAllGlobals();
});

describe('replay-follow-camera — handler policy', () => {
	it('engage disables the centre-movers, centre-anchors the zoom/rotate pair, keeps the rest', () => {
		engageFollowHandlers(asMap(map));

		for (const name of OFF) expect(map[name].disable, name).toHaveBeenCalledTimes(1);
		// around: 'center' is what makes a pinch orbit the marker instead of the finger midpoint.
		for (const name of CENTRED) expect(map[name].enable, name).toHaveBeenCalledWith({ around: 'center' });
		for (const name of ORBIT) expect(map[name].enable, name).toHaveBeenCalledWith();
	});

	it('release hands every handler back UN-anchored — MapLibre latches `around` at enable() time', () => {
		engageFollowHandlers(asMap(map));
		for (const name of FOLLOW_HANDLERS) map[name].enable.mockClear();

		releaseFollowHandlers(asMap(map));

		for (const name of FOLLOW_HANDLERS) {
			expect(map[name].enable, name).toHaveBeenCalledTimes(1);
			// No argument: a free camera must zoom about the pointer again, not the centre.
			expect(map[name].enable, name).toHaveBeenCalledWith();
		}
	});

	it('a throwing handler does not abort the others', () => {
		map.dragPan.disable.mockImplementation(() => { throw new Error('handler is gone'); });
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

		engageFollowHandlers(asMap(map));

		expect(map.doubleClickZoom.disable).toHaveBeenCalledTimes(1);
		for (const name of CENTRED) expect(map[name].enable, name).toHaveBeenCalledTimes(1);
		expect(warn).toHaveBeenCalledTimes(1);
		warn.mockRestore();
	});
});

describe('replay-follow-camera — engage / release', () => {
	it('engageFollow applies the policy and activates the camera; releaseFollow reverses it', () => {
		const camera = new CameraControls(asMap(map));

		engageFollow(asMap(map), camera);
		expect(camera.isActive).toBe(true);
		for (const name of OFF) expect(map[name].disable, name).toHaveBeenCalledTimes(1);

		releaseFollow(asMap(map), camera);
		expect(camera.isActive).toBe(false);
		for (const name of FOLLOW_HANDLERS) expect(map[name].enable, name).toHaveBeenCalled();
	});

	it('a seed naming every field frames the follow without reading the map (/Segments/Statistics)', () => {
		const camera = new CameraControls(asMap(map));

		engageFollow(asMap(map), camera, { center: [7, 8], zoom: 15, bearing: 0, pitch: 30 });

		expect(map.jumpTo).toHaveBeenCalledWith({ center: [7, 8], zoom: 15, bearing: 0, pitch: 30 });
		// The page frames the effort; it does not adopt whatever the viewer was looking at.
		expect(map.getZoom).not.toHaveBeenCalled();
		expect(map.getBearing).not.toHaveBeenCalled();
		expect(map.getPitch).not.toHaveBeenCalled();
	});

	it('an omitted seed field keeps what the map has — locking on must not snap (/LiveView)', () => {
		const camera = new CameraControls(asMap(map));

		engageFollow(asMap(map), camera, { center: [7, 8] });

		expect(map.jumpTo).toHaveBeenCalledWith({ center: [7, 8], zoom: 11, bearing: 33, pitch: 44 });
	});

	it('no seed at all writes no framing and touches no camera getter', () => {
		engageFollow(asMap(map), new CameraControls(asMap(map)));

		expect(map.jumpTo).not.toHaveBeenCalled();
		expect(map.getZoom).not.toHaveBeenCalled();
	});

	it('the seeded framing is what the R key resets to', () => {
		const camera = new CameraControls(asMap(map));
		engageFollow(asMap(map), camera, { center: [7, 8], zoom: 15, bearing: 0, pitch: 30 });
		map.jumpTo.mockClear();

		camera.reset();

		expect(map.jumpTo).toHaveBeenCalledWith({ zoom: 15, bearing: 0, pitch: 30 });
	});

	it('engageFollow collapses the compact attribution — the drag that used to do it is now off', () => {
		const bar = document.createElement('div');
		bar.className = 'maplibregl-ctrl-attrib maplibregl-compact maplibregl-compact-show';
		bar.setAttribute('open', '');
		map.getContainer().appendChild(bar);

		engageFollow(asMap(map), new CameraControls(asMap(map)));

		expect(bar.classList.contains('maplibregl-compact-show')).toBe(false);
		// MapLibre's own minimiser leaves `open` alone — the ⓘ button must stay clickable.
		expect(bar.hasAttribute('open')).toBe(true);
		expect(bar.classList.contains('maplibregl-compact')).toBe(true);
	});

	it('releaseFollow tolerates a null camera and still hands the handlers back', () => {
		releaseFollow(asMap(map), null);
		for (const name of FOLLOW_HANDLERS) expect(map[name].enable, name).toHaveBeenCalledTimes(1);
	});

	it('releaseFollow hands the handlers back even when deactivate() throws', () => {
		const camera = new CameraControls(asMap(map));
		engageFollow(asMap(map), camera);
		vi.spyOn(camera, 'deactivate').mockImplementation(() => { throw new Error('disposed map'); });

		expect(() => releaseFollow(asMap(map), camera)).toThrow();
		for (const name of FOLLOW_HANDLERS) expect(map[name].enable, name).toHaveBeenCalled();
	});
});

describe('replay-follow-camera — the centre writer (LV-05)', () => {
	it('writes the CENTRE and nothing else — zoom/bearing/pitch are MapLibre\'s', () => {
		engagedAt(() => [7, 8]);

		pumpFrames(1);

		expect(map.jumpTo).toHaveBeenCalledTimes(1);
		expect(map.jumpTo).toHaveBeenCalledWith({ center: [7, 8] });
	});

	it('does not re-write on an idle frame when nothing changed', () => {
		engagedAt(() => [7, 8]);

		pumpFrames(3);

		expect(map.jumpTo).toHaveBeenCalledTimes(1); // 3 frames, one stationary rider → one write
	});

	it('writes the frame the followed position moves', () => {
		let pos: [number, number] = [7, 8];
		engagedAt(() => pos);

		pumpFrames(2);
		expect(map.jumpTo).toHaveBeenCalledTimes(1);

		// A rider moves 0.08 m per frame at 5 m/s — BELOW createCameraChangeDetector's ~1 m² threshold.
		// Exact inequality is what makes this frame a write instead of a dropped one.
		pos = [7.0000007, 8];
		pumpFrames(1);
		expect(map.jumpTo).toHaveBeenCalledTimes(2);
	});

	it('writes the first frame after a re-engage', () => {
		const camera = engagedAt(() => [7, 8]);
		pumpFrames(2);
		expect(map.jumpTo).toHaveBeenCalledTimes(1);

		releaseFollow(asMap(map), camera);
		engageFollow(asMap(map), camera, { center: [7, 8] });
		map.jumpTo.mockClear();
		camera.startIdleLoop(() => [7, 8]);

		pumpFrames(1);
		expect(map.jumpTo).toHaveBeenCalledTimes(1);
	});
});

describe('replay-follow-camera — the camera hook', () => {
	// jumpTo() calls stop(), which calls reset() on EVERY gesture handler — so the follow cannot write
	// the centre and let a gesture run at the same time. transformCameraUpdate is the way out: MapLibre
	// calls it from its own camera update (a path that spares the handlers), so the centre can be
	// forced from INSIDE a pinch or an orbit instead of in competition with it.
	it('engage installs it; release takes it back off', () => {
		const camera = new CameraControls(asMap(map));

		engageFollow(asMap(map), camera, { center: [7, 8] });
		expect(typeof map.transformCameraUpdate).toBe('function');

		releaseFollow(asMap(map), camera);
		expect(map.transformCameraUpdate).toBeNull();
	});

	it('stays inert until a frame reports a position, so it cannot fight the engage-time framing', () => {
		engageFollow(asMap(map), new CameraControls(asMap(map)), { center: [7, 8] });

		expect(map.runCameraUpdate([7, 8])).toEqual({});
	});

	it('forces the followed point onto a camera update that is somewhere else', () => {
		engagedAt(() => [10, 20]);
		pumpFrames(1);

		expect(map.runCameraUpdate([7, 8]).center).toMatchObject({ lng: 10, lat: 20 });
	});

	it('returns a FRESH LngLat — setCenter no-ops when handed back the object it already holds', () => {
		engagedAt(() => [10, 20]);
		pumpFrames(1);

		const presented = new FakeLngLat(7, 8);
		const out = map.transformCameraUpdate!({ center: presented });

		expect(out.center).not.toBe(presented);
		expect(out.center).toBeInstanceOf(FakeLngLat);
	});

	it('leaves an already-correct centre alone', () => {
		engagedAt(() => [10, 20]);
		pumpFrames(1);

		expect(map.runCameraUpdate([10, 20])).toEqual({});
	});
});

describe('replay-follow-camera — writing through a gesture, standing back for an ease', () => {
	// The reported bug, and the reason a live gesture is no longer a reason to stand back.
	// `HandlerManager._updateMapTransform` early-returns on `!hasChange` and `_changes` is only pushed
	// when a handler produced a delta — so a button held down but NOT MOVING schedules no frame and
	// calls no hook, while `isActive()` still reports a live gesture. A follow that withheld for that
	// state left the marker walking off screen until the user moved the mouse again or let go.
	it('keeps following while a live gesture is HELD STILL — every frame, not every other one', () => {
		let pos: [number, number] = [7, 8];
		engagedAt(() => pos);
		pumpFrames(1);
		map.jumpTo.mockClear();

		map.gestureLive = true; // button down, and it never moves again
		pos = [10, 20];
		pumpFrames(1);
		expect(map.jumpTo).toHaveBeenLastCalledWith({ center: [10, 20] });

		pos = [11, 21];
		pumpFrames(1);
		expect(map.jumpTo).toHaveBeenLastCalledWith({ center: [11, 21] });

		// All of it shielded, so the drag the user is still holding is never reset.
		expect(map.shieldedDuringJump.every(Boolean)).toBe(true);
	});

	it('writes through a LONG gesture without ever unshielding', () => {
		let pos: [number, number] = [7, 8];
		engagedAt(() => pos);
		map.jumpTo.mockClear(); // startIdleLoop writes its first frame synchronously
		map.gestureLive = true;

		for (let i = 0; i < 600; i++) { // 10 s at 60 Hz
			pos = [10 + i / 1000, 20];
			pumpFrames(1);
		}

		expect(map.jumpTo).toHaveBeenCalledTimes(600);
		expect(map.shieldedDuringJump.every(Boolean)).toBe(true);
	});

	// The one state a write must miss: jumpTo → stop() → Camera._stop() cancels _easeFrameId, which
	// the handler shield does NOT cover. Writing here would kill flick-rotate inertia outright.
	it('stands back for an ease, and the hook keeps the marker centred meanwhile', () => {
		let pos: [number, number] = [7, 8];
		engagedAt(() => pos);
		pumpFrames(1);
		map.jumpTo.mockClear();

		map.easing = true; // the inertia after a flick-rotate
		pos = [10, 20];
		pumpFrames(10);

		expect(map.jumpTo).not.toHaveBeenCalled();
		expect(map.runCameraUpdate([7, 8]).center).toMatchObject({ lng: 10, lat: 20 });
	});

	it('re-locks on the followed point as soon as the ease ends', () => {
		let pos: [number, number] = [7, 8];
		engagedAt(() => pos);
		pumpFrames(1);
		map.easing = true;
		pos = [10, 20];
		pumpFrames(5);
		map.jumpTo.mockClear();

		map.easing = false;
		pumpFrames(1);

		expect(map.jumpTo).toHaveBeenLastCalledWith({ center: [10, 20] });
	});
});

describe('replay-follow-camera — writing without disturbing a gesture', () => {
	// The bug this exists for: an UNSHIELDED write runs handlers.stop() → DragHandler.reset() →
	// `delete this._lastPoint`, and dragMove bails without it, so the drag is dead until the next
	// mousedown. It bit hardest in the PENDING window — between mousedown and the first move past
	// MapLibre's 1 px clickTolerance, where nothing reports a gesture on any channel — because there
	// the follow has no reason to hold back at all. Playing, we write most frames, so most right-drags
	// never grabbed; paused, the stationary marker suppresses the write and they always did.
	it('still writes in the pending window — the follow is never withheld', () => {
		let pos: [number, number] = [7, 8];
		engagedAt(() => pos);
		pumpFrames(1);

		expect(map.gestureLive).toBe(false); // nothing reports a gesture yet
		pos = [10, 20];
		pumpFrames(1);

		expect(map.jumpTo).toHaveBeenLastCalledWith({ center: [10, 20] });
	});

	it('shields that write, so it cannot reset a forming gesture', () => {
		engagedAt(() => [10, 20]);
		pumpFrames(1);

		expect(map.shieldedDuringJump).toEqual([true]);
		// Handed straight back — nothing after the write inherits the shield.
		expect(map.handlers._updatingCamera).toBe(false);
	});

	it('restores the shield even when jumpTo throws', () => {
		engagedAt(() => [10, 20]);
		map.jumpTo.mockImplementation(() => { throw new Error('disposed map'); });

		expect(() => pumpFrames(1)).not.toThrow();
		expect(map.handlers._updatingCamera).toBe(false);
	});

	it('restores a shield that was ALREADY up rather than clearing it', () => {
		engagedAt(() => [10, 20]);
		map.handlers._updatingCamera = true;

		pumpFrames(1);

		expect(map.handlers._updatingCamera).toBe(true);
	});

	it('does NOT shield the engage-time framing jump — that one is meant to interrupt', () => {
		engageFollow(asMap(map), new CameraControls(asMap(map)), { center: [7, 8] });

		expect(map.shieldedDuringJump).toEqual([false]);
	});
});

describe('replay-follow-camera — touch-action', () => {
	// dragPan.disable() strips `maplibregl-touch-drag-pan`, and MapLibre's stylesheet only grants
	// touch-action: none while that class AND maplibregl-touch-zoom-rotate are both present. Losing it
	// lets the browser scroll the page under a one-finger drag — which TouchGestureHandler used to
	// fight with preventDefault, and that suppressed the synthetic click, so photo pins could not be
	// tapped on a phone during a replay.
	it('engage pins touch-action to none on the canvas container', () => {
		engageFollowHandlers(asMap(map));

		expect(map.getCanvasContainer().style.touchAction).toBe('none');
	});

	it('release hands it back to the stylesheet', () => {
		engageFollowHandlers(asMap(map));
		releaseFollowHandlers(asMap(map));

		expect(map.getCanvasContainer().style.touchAction).toBe('');
	});
});

describe('replay-follow-camera — shouldYieldToEase', () => {
	it('never yields on a still map', () => {
		engagedAt(() => [10, 20]);
		expect(shouldYieldToEase(asMap(map))).toBe(false);
	});

	// The regression, pinned at the predicate: a gesture is safe to write through, because
	// writeFollowCentre is shielded from handlers.stop(). Standing back for one stranded the marker.
	it('does NOT yield for a live gesture, however long it is held', () => {
		engagedAt(() => [10, 20]);
		map.gestureLive = true;

		expect(shouldYieldToEase(asMap(map))).toBe(false);
	});

	it('yields while an ease is in flight — our jumpTo would cancel it', () => {
		engagedAt(() => [10, 20]);
		map.easing = true;

		expect(shouldYieldToEase(asMap(map))).toBe(true);
	});

	// Standing back is only safe because the camera hook holds the centre meanwhile. No hook, no
	// stand-back: an abandoned marker is worse than an interrupted ease.
	it('does NOT yield to an ease when the camera hook could not be installed', () => {
		vi.stubGlobal('maplibregl', {}); // no LngLat ⇒ engageFollow installs no hook
		engagedAt(() => [10, 20]);
		map.easing = true;

		expect(shouldYieldToEase(asMap(map))).toBe(false);
	});

	it('treats a map with no follow engaged, or no isEasing(), as never easing', () => {
		expect(shouldYieldToEase(asMap(map))).toBe(false);   // engaged on nothing
		expect(shouldYieldToEase({} as maplibregl.Map)).toBe(false);
	});
});
