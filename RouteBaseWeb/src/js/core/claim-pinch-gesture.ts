// claim-pinch-gesture.ts — refuse the BROWSER's page zoom on one element, so a component that
// implements its own pinch (the photo viewer, the x-zoomable charts) actually receives the gesture.
//
// Claiming it takes three separate refusals because no single one covers both engines:
//   1. `touch-action` on the element (CSS, or inline) denies the pinch on Chrome/Android. Left to the
//      caller — the right value differs per surface (`pan-y` to keep one-finger scroll, `none` when
//      already zoomed) and is not something this helper can pick.
//   2. A `preventDefault` on any multi-touch `touchmove`.
//   3. The non-standard `gesture*` events — iOS Safari ignores `touch-action` for page zoom, and these
//      are its only opt-out.
//
// Miss (3) and the surface still works on Android while a pinch on iOS zooms the whole page instead:
// that was the photo viewer's original bug, and the charts carried the same gap until this was shared.

export interface ClaimPinchOptions {
	/** Aborting this releases the listeners — the surface hands the gesture back to the browser. */
	signal?: AbortSignal;
}

/**
 * Deny the browser's pinch-zoom on `surface`. Returns a teardown; passing `signal` tears down too,
 * for callers that already own an AbortController.
 */
export function claimPinchGesture(surface: HTMLElement, opts: ClaimPinchOptions = {}): () => void {
	const ac = new AbortController();
	const { signal } = ac;

	const suppress = (e: Event): void => e.preventDefault();
	const onTouchMove = (e: TouchEvent): void => { if (e.touches.length > 1) e.preventDefault(); };

	surface.addEventListener("touchmove", onTouchMove, { signal, passive: false });
	// iOS Safari only: non-standard, and the sole way to refuse ITS page pinch.
	surface.addEventListener("gesturestart", suppress, { signal, passive: false });
	surface.addEventListener("gesturechange", suppress, { signal, passive: false });
	surface.addEventListener("gestureend", suppress, { signal, passive: false });

	if (opts.signal?.aborted) ac.abort();
	else opts.signal?.addEventListener("abort", () => ac.abort(), { once: true });

	return () => ac.abort();
}
