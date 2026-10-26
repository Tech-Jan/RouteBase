/**
 * shape-filter/map-adapter.ts — the only coupling point to the map engine.
 *
 * The rest of the shape system depends on this interface, not on MapLibre directly
 * (dependency inversion). MapLibre is the only engine; the adapter seam stays for
 * testability and so a future engine could plug in without touching layer/interactions.
 */

import type { LatLng } from './shape-model';

/** MapLibre's full set of camera-moving gesture handlers — all locked while the draw overlay is armed. */
const CAMERA_HANDLERS = [
	'dragPan', 'scrollZoom', 'boxZoom', 'doubleClickZoom',
	'dragRotate', 'touchZoomRotate', 'touchPitch', 'keyboard',
] as const;

export interface ScreenPoint {
	x: number;
	y: number;
}

export interface MapAdapter {
	/** Geographic → container pixel. */
	project(lat: number, lng: number): ScreenPoint;
	/** Container pixel → geographic. */
	unproject(x: number, y: number): LatLng;
	/** Fires whenever the view moves/zooms (so overlays can re-project). Returns an unsubscribe. */
	onViewChange(cb: () => void): () => void;
	/** The map's container element — overlays are sized/positioned against it. */
	getContainer(): HTMLElement;
	/**
	 * Enable/disable the map's own pan & zoom gesture handlers. We turn them OFF while
	 * the draw overlay is armed: pointer/touch events bubble from the overlay up to the
	 * map container, where the engine's drag handler would otherwise pan the map mid-draw
	 * (CSS `touch-action` only blocks the browser's native scroll, not the engine's JS).
	 */
	setInteractive(enabled: boolean): void;
}

export function createMapLibreAdapter(map: maplibregl.Map): MapAdapter {
	return {
		project(lat: number, lng: number): ScreenPoint {
			const p = map.project([lng, lat]);
			return { x: p.x, y: p.y };
		},
		unproject(x: number, y: number): LatLng {
			const ll = map.unproject([x, y]);
			return { lat: ll.lat, lng: ll.lng };
		},
		onViewChange(cb: () => void): () => void {
			map.on('move', cb);
			map.on('zoom', cb);
			map.on('resize', cb);
			return () => {
				map.off('move', cb);
				map.off('zoom', cb);
				map.off('resize', cb);
			};
		},
		getContainer(): HTMLElement {
			return map.getContainer();
		},
		setInteractive(enabled: boolean): void {
			// SG-03: lock EVERY camera handler while armed. The overlay's own pointer/touch events bubble
			// to the map container, and so does the wheel — MapLibre's handlers listen there on mouse /
			// wheel / dblclick events, which pointer-capture and `touch-action` do NOT intercept. Any of
			// the eight moving the camera mid-draw shifts the live unproject() and warps the shape:
			//   dragPan (drag) · scrollZoom (wheel) · boxZoom (shift-drag) · doubleClickZoom (dblclick) ·
			//   dragRotate (right/ctrl-drag bearing) · touchZoomRotate (pinch) · touchPitch (2-finger
			//   pitch) · keyboard (arrow pan, Shift+arrow pitch/bearing — no pointer release needed).
			// scrollZoom is the one a real user hits: a wheel tick while dragging out a zone.
			for (const h of CAMERA_HANDLERS) {
				if (enabled) map[h].enable();
				else map[h].disable();
			}
		},
	};
}
