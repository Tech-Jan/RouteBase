// photo-zoom.ts — pinch/drag/double-tap zoom for the full-size photo viewers (the map+details lightbox
// and the /Photos library one). Mounted on the shown <img>; the maths live in photo-zoom-math.ts.
//
// It exists because of what the BROWSER did instead. A pinch inside the viewer was a page zoom: the
// whole document scaled, so closing the viewer left the map and its markers magnified behind it, and
// pinching back out did not reliably restore them. The viewer therefore claims the gesture — the photo
// scales, the page never does, and closing lands on exactly the page the user left.
//
// Refusing the browser's own pinch is core/claim-pinch-gesture.ts (shared with the charts) plus the
// `touch-action` on the image it documents, in photo-viewer.css.

import { claimPinchGesture } from "../core/claim-pinch-gesture";
import { must } from '../core/invariant';
import {
	IDENTITY, clampPan, distance, doubleTap, isZoomed, midpoint, pinch, transformCss,
	type PinchStart, type Point, type Size, type ZoomState,
} from "./photo-zoom-math";

/** Two taps closer together than this (and within TAP_SLOP_PX) are a double-tap. */
const DOUBLE_TAP_MS = 300;
const TAP_SLOP_PX = 24;
/** Past this a press is a drag, not a tap — and its trailing click is not a dismiss. */
const DRAG_SLOP_PX = 8;

export interface PhotoZoomOptions {
	/** The image that scales. Its inline `transform` and the `is-zoomed` class are all this writes. */
	image: HTMLImageElement;
	/** The overlay gestures are claimed on — a pinch anywhere in here zooms the photo, not the page. */
	surface: HTMLElement;
}

export interface PhotoZoom {
	/** Back to 1×. The host calls this whenever the shown photo changes — zoom never carries over. */
	reset(): void;
	isZoomed(): boolean;
	destroy(): void;
}

export function mountPhotoZoom(opts: PhotoZoomOptions): PhotoZoom {
	const { image, surface } = opts;
	const ac = new AbortController();
	const { signal } = ac;

	let state: ZoomState = { ...IDENTITY };
	const pointers = new Map<number, Point>();
	let pinchStart: PinchStart | null = null;
	let panStart: { from: Point; state: ZoomState } | null = null;
	// The image's UNTRANSFORMED centre in client coords — the one point the transform never moves, so
	// every focal point is expressed relative to it. Re-read at each gesture start (the page may scroll).
	let centre: Point = { x: 0, y: 0 };
	let moved = false;
	let swallowClick = false;
	let lastTapAt = 0;
	let lastTapPoint: Point = { x: 0, y: 0 };

	image.classList.add("photo-zoomable");
	image.draggable = false; // a zoomed image is dragged to pan it, not ghost-dragged into a new tab

	// ── Geometry ────────────────────────────────────────────────────────────────

	/** The scaled image's own box, and the box it may roam in (its wrapper, else the overlay). */
	function measure(): { content: Size; view: Size } {
		const view = image.parentElement ?? surface;
		return {
			content: { width: image.offsetWidth, height: image.offsetHeight },
			view: { width: view.clientWidth, height: view.clientHeight },
		};
	}

	function refreshCentre(): void {
		const r = image.getBoundingClientRect();
		// The rect is the TRANSFORMED box; scaling holds its centre, so backing out the translation
		// leaves the untransformed one.
		centre = { x: r.left + r.width / 2 - state.x, y: r.top + r.height / 2 - state.y };
	}

	const focal = (p: Point): Point => ({ x: p.x - centre.x, y: p.y - centre.y });

	function apply(next: ZoomState): void {
		const m = measure();
		state = clampPan(next, m.content, m.view);
		image.style.transform = transformCss(state);
		image.classList.toggle("is-zoomed", isZoomed(state));
	}

	// ── Gestures ────────────────────────────────────────────────────────────────

	/** Controls and links keep their own behaviour — a press on one is never a zoom gesture. */
	function isInteractive(t: EventTarget | null): boolean {
		return t instanceof Element && t.closest("button, a, input, textarea, select") !== null;
	}

	function beginPinch(): void {
		refreshCentre();
		const pts = [...pointers.values()];
		const a = must(pts[0], 'pinch pointer 0 (size >= 2)');
		const b = must(pts[1], 'pinch pointer 1 (size >= 2)');
		pinchStart = { state, dist: distance(a, b), focus: focal(midpoint(a, b)) };
		panStart = null;
	}

	const onPointerDown = (e: PointerEvent): void => {
		// A fresh press means the previous gesture's trailing click (if any) has already fired — so clear
		// the swallow guard now, even for a control tap. A pinch/pan often produces NO trailing click, which
		// left the guard armed to eat the FIRST tap on Close/‹/› afterwards (FE-01). A double-tap re-arms it
		// in onTap, which runs after this.
		swallowClick = false;
		if (isInteractive(e.target)) return;
		pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
		if (pointers.size === 1) {
			moved = false;
			refreshCentre();
			// One finger only pans once there is something to pan — at 1× it stays a tap/dismiss.
			panStart = isZoomed(state) ? { from: { x: e.clientX, y: e.clientY }, state } : null;
		} else if (pointers.size === 2) {
			beginPinch();
		}
	};

	const onPointerMove = (e: PointerEvent): void => {
		if (!pointers.has(e.pointerId)) return;
		const p = { x: e.clientX, y: e.clientY };
		pointers.set(e.pointerId, p);

		if (pinchStart && pointers.size >= 2) {
			const pts = [...pointers.values()];
			const a = must(pts[0], 'pinch pointer 0 (size >= 2)');
			const b = must(pts[1], 'pinch pointer 1 (size >= 2)');
			moved = true;
			apply(pinch(pinchStart, distance(a, b), focal(midpoint(a, b))));
			return;
		}
		if (!panStart) return;
		const dx = p.x - panStart.from.x, dy = p.y - panStart.from.y;
		// Below the slop it is still a tap — don't start panning on the jitter of a finger press.
		if (!moved && Math.hypot(dx, dy) < DRAG_SLOP_PX) return;
		moved = true;
		apply({ scale: panStart.state.scale, x: panStart.state.x + dx, y: panStart.state.y + dy });
	};

	const onPointerUp = (e: PointerEvent): void => {
		if (!pointers.delete(e.pointerId)) return;
		if (pointers.size < 2) pinchStart = null;

		if (pointers.size === 0) {
			panStart = null;
			// A gesture that moved ends in a click the host would read as a backdrop dismiss.
			if (moved) swallowClick = true;
			else onTap({ x: e.clientX, y: e.clientY });
			return;
		}
		// A finger lifted mid-pinch: carry on as a one-finger pan from where the other one is.
		const [rest] = [...pointers.values()];
		refreshCentre();
		panStart = isZoomed(state) && rest !== undefined ? { from: rest, state } : null;
	};

	function onTap(p: Point): void {
		const now = Date.now();
		const isDouble = now - lastTapAt < DOUBLE_TAP_MS && distance(p, lastTapPoint) < TAP_SLOP_PX;
		lastTapAt = isDouble ? 0 : now; // consumed — a third tap starts a fresh pair
		lastTapPoint = p;
		if (!isDouble) return;
		refreshCentre();
		apply(doubleTap(state, focal(p)));
		swallowClick = true; // the second tap must zoom, not dismiss
	}

	// ── Native page zoom / trailing click ────────────────────────────────────────

	const onClickCapture = (e: MouseEvent): void => {
		if (!swallowClick) return;
		swallowClick = false;
		e.stopPropagation();
		e.preventDefault();
	};

	surface.addEventListener("pointerdown", onPointerDown, { signal });
	// Move/up on the window: a finger that leaves the overlay mid-drag must still be tracked, and a
	// release outside it must still end the gesture.
	window.addEventListener("pointermove", onPointerMove, { signal });
	window.addEventListener("pointerup", onPointerUp, { signal });
	window.addEventListener("pointercancel", onPointerUp, { signal });
	surface.addEventListener("click", onClickCapture, { signal, capture: true });
	claimPinchGesture(surface, { signal });

	function reset(): void {
		pointers.clear();
		pinchStart = null;
		panStart = null;
		moved = false;
		state = { ...IDENTITY };
		image.style.transform = "";
		image.classList.remove("is-zoomed");
	}

	return {
		reset,
		isZoomed: () => isZoomed(state),
		destroy(): void {
			ac.abort();
			reset();
			image.classList.remove("photo-zoomable");
			image.draggable = true;
		},
	};
}
