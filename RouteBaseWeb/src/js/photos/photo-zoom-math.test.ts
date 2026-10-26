// @vitest-environment node
import { describe, it, expect } from 'vitest';
import {
	IDENTITY, MAX_SCALE, clampPan, clampScale, doubleTap, isZoomed, panLimit, pinch, transformCss,
	zoomAbout, type ZoomState,
} from './photo-zoom-math';

// The contract these pin is "what is under your fingers stays under your fingers, and the photo can
// never be pushed out of its own viewport". Everything else in the viewer's zoom is event plumbing.

/** Where a content point `u` (px from the untransformed centre) lands on screen under `s`. */
const project = (s: ZoomState, u: number): number => u * s.scale + s.x;

describe('photo zoom scale bounds', () => {
	it('never goes below 1× (the photo is fit-to-screen at rest) or past the cap', () => {
		expect(clampScale(0.2)).toBe(1);
		expect(clampScale(99)).toBe(MAX_SCALE);
		expect(clampScale(Number.NaN)).toBe(1);
	});

	it('treats float dust from a pinch as not zoomed', () => {
		expect(isZoomed({ scale: 1.001, x: 0, y: 0 })).toBe(false);
		expect(isZoomed({ scale: 1.4, x: 0, y: 0 })).toBe(true);
	});
});

describe('photo zoom focal maths', () => {
	it('keeps the point under the focus where it was', () => {
		const before = { ...IDENTITY };
		const focus = { x: 100, y: -40 };
		const uX = (focus.x - before.x) / before.scale;

		const after = zoomAbout(before, 2.5, focus);

		expect(after.scale).toBe(2.5);
		expect(project(after, uX)).toBeCloseTo(focus.x, 6);
	});

	it('holds the focus through a second zoom from an already-zoomed, panned state', () => {
		const before: ZoomState = { scale: 2, x: -120, y: 35 };
		const focus = { x: 60, y: 10 };
		const uX = (focus.x - before.x) / before.scale;

		const after = zoomAbout(before, 4, focus);

		expect(project(after, uX)).toBeCloseTo(focus.x, 6);
	});

	it('clamps the scale but still keeps the focus put', () => {
		const focus = { x: 90, y: 0 };
		const after = zoomAbout({ scale: 5, x: 0, y: 0 }, 40, focus);

		expect(after.scale).toBe(MAX_SCALE);
		expect(project(after, (focus.x - 0) / 5)).toBeCloseTo(focus.x, 6);
	});
});

describe('pinch resolution', () => {
	const start = { state: { ...IDENTITY }, dist: 100, focus: { x: 0, y: 0 } };

	it('scales by the ratio of finger spread to where it started', () => {
		expect(pinch(start, 250, { x: 0, y: 0 }).scale).toBeCloseTo(2.5, 6);
		expect(pinch(start, 50, { x: 0, y: 0 }).scale).toBe(1); // squeezing past 1× is held at 1×
	});

	it('resolves from the gesture START, so a wobble in and back out lands exactly where it began', () => {
		const there = pinch(start, 220, { x: 12, y: -6 });
		const back = pinch(start, 100, { x: 0, y: 0 });
		expect(there.scale).toBeCloseTo(2.2, 6);
		expect(back).toEqual({ scale: 1, x: 0, y: 0 });
	});

	it('pans by the midpoint travel on top of the scale change', () => {
		const still = pinch(start, 200, { x: 0, y: 0 });
		const dragged = pinch(start, 200, { x: 30, y: -20 });
		expect(dragged.x - still.x).toBeCloseTo(30, 6);
		expect(dragged.y - still.y).toBeCloseTo(-20, 6);
	});

	it('is inert when the gesture began with the fingers on the same point', () => {
		expect(pinch({ state: { scale: 2, x: 5, y: 5 }, dist: 0, focus: { x: 0, y: 0 } }, 80, { x: 0, y: 0 }))
			.toEqual({ scale: 2, x: 5, y: 5 });
	});
});

describe('pan clamping', () => {
	it('lets the photo travel exactly its overhang and no further', () => {
		expect(panLimit(800, 400)).toBe(200);
		const s = clampPan({ scale: 2, x: 500, y: -900 }, { width: 400, height: 300 }, { width: 400, height: 300 });
		expect(s.x).toBe(200);
		expect(s.y).toBe(-150);
	});

	it('re-centres what fits — zooming back out cannot leave the photo off to one side', () => {
		const s = clampPan({ scale: 1, x: 140, y: -60 }, { width: 400, height: 300 }, { width: 400, height: 300 });
		expect(s).toEqual({ scale: 1, x: 0, y: 0 });
	});

	it('clamps each axis on its own — a letterboxed photo pans sideways only', () => {
		// 800×300 content in a 400×300 view: overhang horizontally, none vertically.
		const s = clampPan({ scale: 1, x: 999, y: 999 }, { width: 800, height: 300 }, { width: 400, height: 300 });
		expect(s.x).toBe(200);
		expect(s.y).toBe(0);
	});
});

describe('double-tap and serialisation', () => {
	it('toggles in about the tap, then all the way back out', () => {
		const zoomedIn = doubleTap(IDENTITY, { x: 50, y: 20 });
		expect(isZoomed(zoomedIn)).toBe(true);
		expect(doubleTap(zoomedIn, { x: 50, y: 20 })).toEqual({ scale: 1, x: 0, y: 0 });
	});

	it('writes no inline transform at rest, and a rounded one when zoomed', () => {
		expect(transformCss(IDENTITY)).toBe('');
		expect(transformCss({ scale: 2.00004, x: 10.00049, y: -3 })).toBe('translate(10px, -3px) scale(2)');
	});
});
