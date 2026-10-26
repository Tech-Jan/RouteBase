import { describe, it, expect, vi, beforeEach } from 'vitest';
import { TouchGestureHandler } from './touch-gesture-handler';
import { createFakeMap, makeTouchEvent, type FakeMap as SharedFakeMap } from '../core/fake-map-harness';

// The one touch gesture the follow still implements. Everything multi-touch is MapLibre's, so the
// sharpest pin here is the NEGATIVE one: with two fingers down this handler must write nothing at
// all — a jumpTo of ours resets MapLibre's handlers and would kill the pinch/tilt in progress.

class FakeMap {
	jumpTo = vi.fn<(cam: { bearing: number; pitch: number }) => void>();
	bearing = 0;
	pitch = 30;
	getBearing = (): number => this.bearing;
	getPitch = (): number => this.pitch;
	private _canvas = document.createElement('canvas');
	getCanvas(): HTMLCanvasElement { return this._canvas; }
}

let map: FakeMap;
let handler: TouchGestureHandler;

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const asMap = (m: FakeMap): maplibregl.Map => m as any;

/** A TouchEvent jsdom will accept — jsdom has no Touch constructor, so the points are plain objects. */
function touchEvent(type: string, points: Array<{ id: number; x: number; y: number }>): TouchEvent {
	const touches = points.map((p) => ({ identifier: p.id, clientX: p.x, clientY: p.y }));
	const e = new Event(type, { bubbles: true, cancelable: true }) as unknown as {
		changedTouches: unknown; touches: unknown;
	};
	e.changedTouches = touches;
	e.touches = touches;
	return e as unknown as TouchEvent;
}

function fire(type: string, points: Array<{ id: number; x: number; y: number }>): void {
	map.getCanvas().dispatchEvent(touchEvent(type, points) as unknown as Event);
}

beforeEach(() => {
	map = new FakeMap();
	handler = new TouchGestureHandler(asMap(map));
	handler.activate();
});

describe('TouchGestureHandler — 1-finger rotate + tilt', () => {
	it('a horizontal drag rotates, a vertical drag tilts, both relative to the live camera', () => {
		fire('touchstart', [{ id: 1, x: 100, y: 100 }]);
		fire('touchmove', [{ id: 1, x: 120, y: 80 }]);

		// bearing += dx * 0.35 ; pitch -= dy * 0.25 (drag up ⇒ more tilt, as on the 2-finger gesture)
		expect(map.jumpTo).toHaveBeenCalledWith({ bearing: 20 * 0.35, pitch: 30 + 20 * 0.25 });
	});

	it('reads the map each move, so it composes with whatever gestures already did', () => {
		fire('touchstart', [{ id: 1, x: 100, y: 100 }]);
		map.bearing = 90; // e.g. the user just twisted with two fingers

		fire('touchmove', [{ id: 1, x: 110, y: 100 }]);

		expect(map.jumpTo).toHaveBeenCalledWith({ bearing: 90 + 10 * 0.35, pitch: 30 });
	});

	it('writes nothing for a move that did not move', () => {
		fire('touchstart', [{ id: 1, x: 100, y: 100 }]);
		fire('touchmove', [{ id: 1, x: 100, y: 100 }]);

		expect(map.jumpTo).not.toHaveBeenCalled();
	});

	// Cancelling a touch event also suppresses the browser's compatibility mouse events, `click`
	// among them — and photo pins and clusters are map LAYERS, reached through map.on('click', …).
	// That is why nothing on the map could be tapped during a replay on a phone, while desktop (real
	// mouse events) was fine. Scrolling is held off by `touch-action: none` on the canvas container
	// instead — see core/replay-follow-camera.setCanvasTouchAction.
	it('cancels nothing, so a tap still reaches the map as a click', () => {
		const start = touchEvent('touchstart', [{ id: 1, x: 100, y: 100 }]);
		map.getCanvas().dispatchEvent(start as unknown as Event);
		const move = touchEvent('touchmove', [{ id: 1, x: 140, y: 100 }]);
		map.getCanvas().dispatchEvent(move as unknown as Event);

		expect(start.defaultPrevented).toBe(false);
		expect(move.defaultPrevented).toBe(false);
	});

	it('a tap that wobbles inside the tolerance does not nudge the camera', () => {
		fire('touchstart', [{ id: 1, x: 100, y: 100 }]);
		fire('touchmove', [{ id: 1, x: 102, y: 101 }]);   // ~2.2 px — a finger resting, not a drag
		fire('touchend', [{ id: 1, x: 102, y: 101 }]);

		expect(map.jumpTo).not.toHaveBeenCalled();
	});

	it('measures from where the finger LANDED, so clearing the tolerance loses no rotation', () => {
		fire('touchstart', [{ id: 1, x: 100, y: 100 }]);
		fire('touchmove', [{ id: 1, x: 102, y: 100 }]);   // under tolerance: ignored, anchor holds
		fire('touchmove', [{ id: 1, x: 110, y: 100 }]);   // clears it — the delta is the full 10, not 8

		expect(map.jumpTo).toHaveBeenCalledTimes(1);
		expect(map.jumpTo).toHaveBeenCalledWith({ bearing: 10 * 0.35, pitch: 30 });
	});

	it('stays out of a 2-finger gesture entirely — that is MapLibre\'s', () => {
		fire('touchstart', [{ id: 1, x: 100, y: 100 }]);
		fire('touchstart', [{ id: 2, x: 200, y: 100 }]);

		// A parallel 2-finger up-drag: MapLibre's touchPitch gesture, not ours.
		fire('touchmove', [{ id: 1, x: 100, y: 60 }, { id: 2, x: 200, y: 60 }]);

		expect(map.jumpTo).not.toHaveBeenCalled();
	});

	it('does not grab the tail of a pinch when one finger lifts — waits for a clean release', () => {
		fire('touchstart', [{ id: 1, x: 100, y: 100 }]);
		fire('touchstart', [{ id: 2, x: 200, y: 100 }]);
		fire('touchend', [{ id: 2, x: 200, y: 100 }]);

		fire('touchmove', [{ id: 1, x: 150, y: 100 }]);
		expect(map.jumpTo).not.toHaveBeenCalled();

		// All fingers up, then a fresh 1-finger drag → ours again.
		fire('touchend', [{ id: 1, x: 150, y: 100 }]);
		fire('touchstart', [{ id: 1, x: 100, y: 100 }]);
		fire('touchmove', [{ id: 1, x: 110, y: 100 }]);
		expect(map.jumpTo).toHaveBeenCalledTimes(1);
	});

	it('deactivate() detaches — a later drag writes nothing', () => {
		handler.deactivate();

		fire('touchstart', [{ id: 1, x: 100, y: 100 }]);
		fire('touchmove', [{ id: 1, x: 150, y: 50 }]);

		expect(map.jumpTo).not.toHaveBeenCalled();
	});

	it('survives a disposed map mid-gesture', () => {
		map.jumpTo.mockImplementation(() => { throw new Error('map is gone'); });

		fire('touchstart', [{ id: 1, x: 100, y: 100 }]);
		expect(() => fire('touchmove', [{ id: 1, x: 150, y: 50 }])).not.toThrow();
	});
});

// Driven through the SHARED fake map (core/fake-map-harness) — the only fake that carries the handler
// manager and its `_updatingCamera` flag, so it can see whether the rotate write was shielded.
describe('TouchGestureHandler — the rotate write is shielded (harness)', () => {
	let fake: SharedFakeMap;
	let h: TouchGestureHandler;

	function fireOn(type: string, points: Array<{ id: number; x: number; y: number }>): void {
		const pts = points.map((p) => ({ id: p.id, x: p.x, y: p.y }));
		fake.map.getCanvas().dispatchEvent(makeTouchEvent(type, pts) as unknown as Event);
	}

	beforeEach(() => {
		fake = createFakeMap({ bearing: 0, pitch: 30 });
		h = new TouchGestureHandler(fake.map);
		h.activate();
	});

	// The reason: a bare jumpTo runs handlers.stop() → reset() on every handler, which would wipe a pinch
	// forming under the finger. writeFollowCentre / applyTerrainElevation shield every OTHER per-frame
	// camera write in the follow path for exactly this; the 1-finger rotate must not be the one that doesn't.
	it('runs its jumpTo with handlers._updatingCamera set, so a forming gesture survives', () => {
		fireOn('touchstart', [{ id: 1, x: 100, y: 100 }]);
		fireOn('touchmove', [{ id: 1, x: 120, y: 80 }]); // clears the 3 px tolerance

		expect(fake.jumpTo).toHaveBeenCalledTimes(1);
		// _updatingCamera was true at the instant of the jumpTo, and restored to false afterwards.
		expect(fake.shieldedDuringJump).toEqual([true]);
		expect((fake.map as unknown as { handlers: { _updatingCamera: boolean } }).handlers._updatingCamera)
			.toBe(false);
	});
});
