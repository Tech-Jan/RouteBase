// map-user-gesture.ts — "the user just grabbed the map", and the popup dismissal built on it.
//
// A MapLibre Popup is anchored to a coordinate, so a replay camera drags it across the screen every
// frame and its × becomes a small moving target. This is the other way out: touch the map anywhere —
// pan, pinch, wheel, tap — and the popup goes.
//
// It listens for RAW pointer/wheel events on the canvas container rather than MapLibre's
// dragstart/zoomstart + `originalEvent`, because during a follow the gestures that matter never reach
// those: dragPan is disabled, and the one-finger rotate/tilt is our own handler calling map.jumpTo()
// (maplibre/touch-gesture-handler.ts), which is indistinguishable from a replay frame's own write.
// Raw events also can't mistake a replay's programmatic camera move for a user gesture, which is the
// whole risk here — a move-driven rule would dismiss the popup the instant it opened mid-replay.
//
// The canvas container is exactly the right scope: MapLibre appends popups to the map CONTAINER, a
// sibling of it, so clicking a popup's OWN chrome — its ×, a button, a link inside it — is not a map
// gesture and does not dismiss it out from under the click.

/** pointerdown covers mouse, touch and pen (it precedes touchstart); wheel is the one gesture without it. */
const GESTURE_EVENTS = ["pointerdown", "wheel"] as const;

// Capture so a handler that stops propagation on the canvas can't hide the gesture; passive so this
// never delays the gesture it is only observing.
const LISTEN_OPTS = { capture: true, passive: true } as const;

/** Call `onGesture` on the first pointer/wheel input on the map surface. Returns an unsubscribe. */
export function onUserMapGesture(map: maplibregl.Map, onGesture: () => void): () => void {
	let el: HTMLElement | null = null;
	try {
		el = typeof map.getCanvasContainer === "function" ? map.getCanvasContainer() : null;
	} catch (_e) {
		el = null; // disposed or faked map — nothing to listen on
	}
	if (!el) return () => {};

	const target = el;
	const handler = (): void => onGesture();
	for (const t of GESTURE_EVENTS) target.addEventListener(t, handler, LISTEN_OPTS);
	return () => {
		for (const t of GESTURE_EVENTS) target.removeEventListener(t, handler, LISTEN_OPTS);
	};
}

/**
 * Dismiss `popup` on the first user map gesture.
 *
 * The subscription dies with the popup — remove() fires 'close', and so does every other way it can
 * go (the ×, a closeOnClick, the host's own teardown), so there is no path that leaks the listener.
 */
export function dismissPopupOnMapGesture(map: maplibregl.Map, popup: maplibregl.Popup): void {
	const stop = onUserMapGesture(map, () => popup.remove());
	popup.on("close", stop);
}
