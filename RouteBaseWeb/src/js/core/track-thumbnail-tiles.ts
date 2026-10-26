// track-thumbnail-tiles.ts — raster-basemap layer for the 2D-canvas thumbnails.
// I/O half of the tile feature: reads the keyless OSM raster source the app
// already ships (map-styles/osm-raster.style.json — no new tile provider), loads
// tile images with a per-page cache + concurrency cap, and paints them under the
// track with a contrast scrim. Pure zoom/tile math lives in the geometry module.

import type { TileLayout, TileRef, TileSourceParams } from './track-thumbnail-geometry';
import { must } from './invariant';

/** The raster style the big map uses; its tile template is our single source. */
const STYLE_URL = '/map-styles/osm-raster.style.json';
const MAX_CONCURRENT = 6; // polite cap; dashboards share tiles via the cache anyway
// Faded-basemap treatment: a translucent white scrim recedes the OSM imagery so
// the fixed-identity track colours stay readable on top (imagery is always light).
const TILE_SCRIM = 'rgba(255, 255, 255, 0.3)';

export interface TileSource extends TileSourceParams {
	urlTemplates: string[]; // one per subdomain (a/b/c) — spread the load
}

/** Minimal ctx surface the tile painter touches (real ctx or a test recorder). */
export interface TilePaintCtx {
	drawImage(img: CanvasImageSource, dx: number, dy: number, dw: number, dh: number): void;
	// The DOM union, so a real CanvasRenderingContext2D assigns with no cast (writes stay strings).
	fillStyle: string | CanvasGradient | CanvasPattern;
	fillRect(x: number, y: number, w: number, h: number): void;
}

export interface LoadedTile {
	ref: TileRef;
	img: CanvasImageSource;
}

// ── Tile source (fetched once per page, then memoised) ────────────────────────

let sourcePromise: Promise<TileSource | null> | null = null;

/**
 * Read the raster style JSON and extract the first raster source's tile
 * template(s) + zoom range. Memoised; resolves null on any failure (offline /
 * malformed) so callers fall back to the shape-only render.
 */
export function getTileSource(): Promise<TileSource | null> {
	if (sourcePromise) return sourcePromise;
	// DT-06: don't negatively cache a failed source — clear the memo so the next card retries.
	sourcePromise = fetchTileSource().then((s) => {
		if (s === null) sourcePromise = null;
		return s;
	});
	return sourcePromise;
}

async function fetchTileSource(): Promise<TileSource | null> {
	try {
		const resp = await fetch(STYLE_URL);
		if (!resp.ok) return null;
		// Third-party style JSON (openfreemap), not our API — annotated, never asserted.
		const style: {
			sources?: Record<string, { type?: string; tiles?: string[]; tileSize?: number; maxzoom?: number; minzoom?: number }>;
		} = await resp.json();
		const raster = Object.values(style.sources || {}).find(
			(s) => s.type === 'raster' && Array.isArray(s.tiles) && s.tiles.length > 0,
		);
		if (!raster || !raster.tiles) return null;
		return {
			urlTemplates: raster.tiles,
			tileSize: raster.tileSize || 256,
			minZoom: raster.minzoom ?? 0,
			maxZoom: raster.maxzoom ?? 19,
		};
	} catch {
		return null;
	}
}

/** Test seam: drop the memoised source so a fresh fetch runs. */
export function _resetTileSource(): void {
	sourcePromise = null;
}

// ── Tile image cache + concurrency-capped loader ──────────────────────────────

/** Loads a tile URL to a drawable image, or null on failure. Injectable for tests. */
export type TileLoader = (url: string) => Promise<CanvasImageSource | null>;

// Keyed z/x/y so tiles shared across cards (many dashboard thumbnails) fetch once.
const tileCache = new Map<string, Promise<CanvasImageSource | null>>();

let active = 0;
const waiters: Array<() => void> = [];

function acquireSlot(): Promise<void> {
	if (active < MAX_CONCURRENT) {
		active++;
		return Promise.resolve();
	}
	return new Promise((resolve) => waiters.push(resolve));
}

function releaseSlot(): void {
	active--;
	const next = waiters.shift();
	if (next) {
		active++;
		next();
	}
}

/** Default loader: an anonymous <img> (pixels are never read back → CORS-safe). */
const defaultLoader: TileLoader = (url) =>
	new Promise((resolve) => {
		const img = new Image();
		img.crossOrigin = 'anonymous';
		img.onload = (): void => resolve(img);
		img.onerror = (): void => resolve(null);
		img.src = url;
	});

function tileUrl(src: TileSource, ref: TileRef): string {
	const sub = must(src.urlTemplates[(ref.x + ref.y) % src.urlTemplates.length], 'in-bounds modulo of non-empty urlTemplates');
	return sub
		.replace('{z}', String(ref.z))
		.replace('{x}', String(ref.x))
		.replace('{y}', String(ref.y));
}

/**
 * Load every tile in a layout (deduplicated via the page cache, concurrency
 * capped), dropping any that fail. The caller keeps its fallback render when the
 * result is empty (offline / all failed).
 */
export async function loadLayoutTiles(
	layout: TileLayout,
	src: TileSource,
	loader: TileLoader = defaultLoader,
): Promise<LoadedTile[]> {
	const results = await Promise.all(
		layout.tiles.map(async (ref) => {
			const key = ref.z + '/' + ref.x + '/' + ref.y;
			let pending = tileCache.get(key);
			if (!pending) {
				pending = acquireSlot()
					.then(() => loader(tileUrl(src, ref)))
					.finally(releaseSlot);
				tileCache.set(key, pending);
			}
			const img = await pending.catch(() => null); // a bad tile never sinks the batch
			if (img === null) tileCache.delete(key); // DT-06: don't negatively cache a failed tile
			return img ? { ref, img } : null;
		}),
	);
	return results.filter((r): r is LoadedTile => r !== null);
}

/** Test seam: clear the shared tile image cache. */
export function _resetTileCache(): void {
	tileCache.clear();
}

// ── Painting ──────────────────────────────────────────────────────────────────

/**
 * Draw the loaded tiles at their destination rects, then a translucent scrim
 * across the whole canvas so the track reads on top. Assumes the ctx transform
 * is already set to css-px (DPR applied by the caller).
 */
export function paintTiles(ctx: TilePaintCtx, loaded: LoadedTile[], cssW: number, cssH: number): void {
	for (const t of loaded) {
		ctx.drawImage(t.img, t.ref.dx, t.ref.dy, t.ref.size, t.ref.size);
	}
	ctx.fillStyle = TILE_SCRIM;
	ctx.fillRect(0, 0, cssW, cssH);
}
