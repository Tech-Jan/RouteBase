// photo-zoom-math.ts — the zoom/pan state maths for the full-size photo viewers. Pure: no DOM, no
// events, no measurement. photo-zoom.ts owns the pointers and writes the transform; this owns what
// the transform should be.
//
// State maps to `transform: translate(x, y) scale(s)` with a centred transform-origin, so a content
// point `u` (CSS px from the image's untransformed centre) lands at `u * s + t` — every formula here
// is that one relation solved for something.

export interface ZoomState { scale: number; x: number; y: number; }
export interface Size { width: number; height: number; }
export interface Point { x: number; y: number; }

export const IDENTITY: ZoomState = { scale: 1, x: 0, y: 0 };
export const MIN_SCALE = 1;
export const MAX_SCALE = 6;
/** Where a double-tap lands when it zooms in. */
export const DOUBLE_TAP_SCALE = 2.5;

/** Below this the state is 1× for every purpose — float dust from a pinch must not count as zoomed. */
const SCALE_EPSILON = 0.005;

export function isZoomed(s: ZoomState): boolean {
	return s.scale > MIN_SCALE + SCALE_EPSILON;
}

export function clampScale(scale: number): number {
	if (!Number.isFinite(scale)) return MIN_SCALE;
	return Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));
}

/**
 * Rescale about a focal point, keeping whatever sits under it under it.
 *
 * `focus` is in CSS px relative to the image's UNTRANSFORMED centre — the caller converts from client
 * coordinates once per gesture, because that centre is the one thing the transform never moves.
 */
export function zoomAbout(s: ZoomState, nextScale: number, focus: Point): ZoomState {
	const scale = clampScale(nextScale);
	const k = scale / s.scale;
	return { scale, x: focus.x - (focus.x - s.x) * k, y: focus.y - (focus.y - s.y) * k };
}

/** How far the scaled image may travel before its own edge would cross into the viewport. */
export function panLimit(contentPx: number, viewPx: number): number {
	return Math.max(0, (contentPx - viewPx) / 2);
}

/** Hold the image against its own edges: it can never be dragged away from the viewport. */
export function clampPan(s: ZoomState, content: Size, view: Size): ZoomState {
	const maxX = panLimit(content.width * s.scale, view.width);
	const maxY = panLimit(content.height * s.scale, view.height);
	return { scale: s.scale, x: clampTo(s.x, maxX), y: clampTo(s.y, maxY) };
}

function clampTo(v: number, limit: number): number {
	const clamped = Math.min(limit, Math.max(-limit, v));
	return clamped === 0 ? 0 : clamped; // never hand back -0: "re-centred" is one value, not two
}

/** What a pinch was when it began. Every frame resolves from this — per-frame deltas accumulate drift. */
export interface PinchStart { state: ZoomState; dist: number; focus: Point; }

/** One pinch frame: the finger spread scales, and the midpoint travelling pans on top of it. */
export function pinch(start: PinchStart, dist: number, focus: Point): ZoomState {
	if (start.dist <= 0) return start.state;
	const scaled = zoomAbout(start.state, start.state.scale * (dist / start.dist), start.focus);
	return {
		scale: scaled.scale,
		x: scaled.x + (focus.x - start.focus.x),
		y: scaled.y + (focus.y - start.focus.y),
	};
}

/** A double-tap toggles: in about the tapped point, or all the way back out to 1×. */
export function doubleTap(s: ZoomState, focus: Point): ZoomState {
	return isZoomed(s) ? { ...IDENTITY } : zoomAbout(s, DOUBLE_TAP_SCALE, focus);
}

export function distance(a: Point, b: Point): number {
	return Math.hypot(a.x - b.x, a.y - b.y);
}

export function midpoint(a: Point, b: Point): Point {
	return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}

/** The inline transform for a state — empty at rest, so an untouched image carries no inline style. */
export function transformCss(s: ZoomState): string {
	if (!isZoomed(s) && s.x === 0 && s.y === 0) return "";
	return `translate(${round(s.x)}px, ${round(s.y)}px) scale(${round(s.scale)})`;
}

/** Trim float noise — a transform string is compared in tests and diffed by the compositor. */
function round(v: number): number {
	return Math.round(v * 1000) / 1000;
}
