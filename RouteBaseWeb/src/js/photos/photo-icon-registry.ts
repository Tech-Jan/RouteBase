// photo-icon-registry.ts — the ONE place photo map icons are drawn and registered with MapLibre.
//
// Photo thumbnails used to be DOM `maplibregl.Marker`s. Under a replay camera that calls jumpTo()
// every frame that is pathological: a Marker binds BOTH `move` and `moveend`, so each one runs
// Marker._update twice per frame, and the `moveend` leg forces _updateOpacity past its own 100 ms
// throttle into terrain.depthAtPoint() — a synchronous gl.readPixels GPU stall, up to twice per
// marker per frame. Twelve visible thumbnails stalled the pipeline ~24× a frame.
//
// So thumbnails are icons on a symbol layer instead: the GPU draws them in the normal render pass
// and a moving camera costs nothing. This module owns every addImage/removeImage for that layer;
// photo-map-overlay.ts only declares the paint expression that names them.
//
// Rendering is injectable (`render`) because the canvas path needs a real 2D context and
// createImageBitmap — neither exists in jsdom. The scheduling/LRU/generation logic is the part
// worth testing, so it stays free of both.

import { photoImageUrl } from "./photo-image-url";
import { must } from '../core/invariant';
import type { PhotoMarker } from "./photo-map-types";

/** Registered-image key for the fallback pin (used until a photo's own icon is ready). */
export const IMG_PIN = "photo-overlay-pin";

/** The one photo accent colour — every pin, and the cluster disc that groups them. */
export const PIN_COLOR = "#0ea5e9";

// 54 CSS px at pixelRatio 2 — matches the DOM thumbnail these replaced.
const ICON_CSS_PX = 54;
const ICON_PIXEL_RATIO = 2;
const ICON_PX = ICON_CSS_PX * ICON_PIXEL_RATIO;
const ICON_RADIUS_PX = 8;
const ICON_BORDER_PX = 4;

/** Registered icons kept at once. Bounds the style's image atlas; ~160 × 108² RGBA ≈ 7 MB. */
const MAX_PHOTO_ICONS = 160;
/** Concurrent thumbnail fetches. Keeps a 200-photo activity from opening 200 sockets at once. */
const MAX_IN_FLIGHT = 6;

/**
 * The image key for one photo's thumbnail icon.
 *
 * The ONE place this key is built — call sites and the `icon` feature property both go through
 * here, so `photoIconName` is the single grep target for the whole naming scheme.
 */
export function photoIconName(photoId: number): string {
	return `photo-thumb-${photoId}`;
}

/** Source-rectangle for a centred square crop of a w×h image (CSS `object-fit: cover`). */
export interface CropRect { sx: number; sy: number; size: number; }

/** Pure: the largest centred square that fills a square icon without distorting the source. */
export function coverFitCrop(srcW: number, srcH: number): CropRect {
	const size = Math.min(srcW, srcH);
	return { sx: (srcW - size) / 2, sy: (srcH - size) / 2, size };
}

/** Produces the icon bitmap for one photo, or null when it can't be had. */
export type PhotoIconRenderer = (p: PhotoMarker) => Promise<ImageData | null>;

export interface PhotoIconRegistry {
	/** Register icons for `wanted` (idempotent) and mark them most-recently-used. */
	ensure(map: maplibregl.Map, wanted: readonly PhotoMarker[]): void;
	/** Forget all bookkeeping without touching the map — for after a setStyle dropped the images. */
	reset(): void;
	/** Unregister every icon and forget everything. */
	destroy(map: maplibregl.Map): void;
}

export function createPhotoIconRegistry(opts: {
	render?: PhotoIconRenderer;
	maxIcons?: number;
	maxInFlight?: number;
} = {}): PhotoIconRegistry {
	const render = opts.render ?? fetchAndDrawPhotoIcon;
	const maxIcons = opts.maxIcons ?? MAX_PHOTO_ICONS;
	const maxInFlight = opts.maxInFlight ?? MAX_IN_FLIGHT;

	const registered = new Map<number, number>(); // photoId → last-wanted sequence (LRU)
	const inFlight = new Set<number>();
	const failed = new Set<number>(); // don't retry a 404 on every camera move
	const queue: PhotoMarker[] = [];
	let seq = 0;
	// Bumped by reset()/destroy(); a render that resolves against a superseded style is discarded
	// rather than written into the new one under stale bookkeeping.
	let generation = 0;

	function pump(map: maplibregl.Map): void {
		while (inFlight.size < maxInFlight && queue.length > 0) {
			const p = must(queue.shift(), 'queue.length > 0 (guarded)');
			const id = p.photoId;
			if (registered.has(id) || inFlight.has(id) || failed.has(id)) continue;
			inFlight.add(id);
			const gen = generation;
			void render(p)
				.then((image) => {
					inFlight.delete(id);
					if (gen !== generation) return; // style swapped under us — this bitmap is orphaned
					if (!image) { failed.add(id); return; }
					const name = photoIconName(id);
					try {
						if (!map.hasImage(name)) {
							map.addImage(name, image, { pixelRatio: ICON_PIXEL_RATIO });
						}
					} catch (_e) {
						// The style can be torn down between the generation check and the call.
						return;
					}
					registered.set(id, ++seq);
					evict(map);
				})
				.catch(() => { inFlight.delete(id); failed.add(id); })
				// FE-04: pump UNCONDITIONALLY — even an orphaned (gen-mismatched) render must drain the
				// queue. The `.then` already discards the stale bitmap; guarding the pump too let a freed
				// slot leave later-queued icons stalled until the next camera move (a redundant re-fetch).
				// Can't spin: pump only SHIFTS the queue (never re-enqueues) and skips ids already in
				// failed/registered, so a deterministically-failing render settles the queue and stops.
				.finally(() => { pump(map); });
		}
	}

	// Drop the least-recently-wanted icons once over budget. Anything still in `wanted` was just
	// re-stamped with a fresh seq, so it sorts to the back and survives.
	function evict(map: maplibregl.Map): void {
		if (registered.size <= maxIcons) return;
		const byAge = [...registered.entries()].sort((a, b) => a[1] - b[1]);
		for (const [id] of byAge.slice(0, registered.size - maxIcons)) {
			try { map.removeImage(photoIconName(id)); } catch (_e) { /* already gone with the style */ }
			registered.delete(id);
		}
	}

	function reset(): void {
		generation++;
		registered.clear();
		inFlight.clear();
		failed.clear();
		queue.length = 0;
	}

	return {
		ensure(map: maplibregl.Map, wanted: readonly PhotoMarker[]): void {
			for (const p of wanted) {
				const id = p.photoId;
				if (registered.has(id)) { registered.set(id, ++seq); continue; }
				if (inFlight.has(id) || failed.has(id)) continue;
				if (queue.some((q) => q.photoId === id)) continue;
				queue.push(p);
			}
			pump(map);
		},
		reset,
		destroy(map: maplibregl.Map): void {
			for (const id of registered.keys()) {
				try { map.removeImage(photoIconName(id)); } catch (_e) { /* style already gone */ }
			}
			reset();
		},
	};
}

// ── Canvas rendering (browser-only I/O; injected away in tests) ──────────────────────────────

/** Default renderer: fetch the thumb rendition and draw it into a rounded, bordered icon. */
async function fetchAndDrawPhotoIcon(p: PhotoMarker): Promise<ImageData | null> {
	const resp = await fetch(photoImageUrl(p, "thumb"));
	if (!resp.ok) return null;
	const bitmap = await createImageBitmap(await resp.blob());
	try {
		return drawPhotoIcon(bitmap);
	} finally {
		bitmap.close?.();
	}
}

/** Cover-crop `bitmap` into a rounded square with a white border. */
export function drawPhotoIcon(bitmap: ImageBitmap): ImageData {
	const c = document.createElement("canvas");
	c.width = ICON_PX;
	c.height = ICON_PX;
	// !-ok is not needed: a FRESH canvas's 2d context is only null in exotic environments — throw with a name.
	const g = must(c.getContext("2d"), 'fresh canvas 2d context');

	const inset = ICON_BORDER_PX / 2;
	roundRect(g, inset, inset, ICON_PX - ICON_BORDER_PX, ICON_PX - ICON_BORDER_PX, ICON_RADIUS_PX);
	g.save();
	g.clip();
	const { sx, sy, size } = coverFitCrop(bitmap.width, bitmap.height);
	g.drawImage(bitmap, sx, sy, size, size, 0, 0, ICON_PX, ICON_PX);
	g.restore();

	g.lineWidth = ICON_BORDER_PX;
	g.strokeStyle = "#ffffff";
	g.stroke();

	return g.getImageData(0, 0, ICON_PX, ICON_PX);
}

/** A round "camera coin" marker image drawn on a canvas (self-contained → CSP-safe, no sprite fetch). */
export function makePinImage(fill: string): ImageData {
	const s = 44;
	const c = document.createElement("canvas");
	c.width = s; c.height = s;
	// !-ok is not needed: a FRESH canvas's 2d context is only null in exotic environments — throw with a name.
	const g = must(c.getContext("2d"), 'fresh canvas 2d context');
	g.clearRect(0, 0, s, s);
	const cx = s / 2, cy = s / 2, r = s / 2 - 3;

	g.beginPath(); g.arc(cx, cy, r, 0, Math.PI * 2);
	g.fillStyle = fill; g.fill();
	g.lineWidth = 3; g.strokeStyle = "#ffffff"; g.stroke();

	// White camera body + viewfinder bump.
	g.fillStyle = "#ffffff";
	const bw = r * 1.0, bh = r * 0.62;
	roundRect(g, cx - bw / 2, cy - bh / 2 + 2, bw, bh, 3); g.fill();
	roundRect(g, cx - 4, cy - bh / 2 - 2, 8, 5, 1.5); g.fill();

	// Lens punched in the fill colour.
	g.fillStyle = fill;
	g.beginPath(); g.arc(cx, cy + 2, bh * 0.3, 0, Math.PI * 2); g.fill();

	return g.getImageData(0, 0, s, s);
}

/** Register the fallback pin once per style. */
export function ensurePinImages(map: maplibregl.Map): void {
	if (!map.hasImage(IMG_PIN)) map.addImage(IMG_PIN, makePinImage(PIN_COLOR), { pixelRatio: 2 });
}

function roundRect(g: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number): void {
	g.beginPath();
	g.moveTo(x + r, y);
	g.arcTo(x + w, y, x + w, y + h, r);
	g.arcTo(x + w, y + h, x, y + h, r);
	g.arcTo(x, y + h, x, y, r);
	g.arcTo(x, y, x + w, y, r);
	g.closePath();
}
