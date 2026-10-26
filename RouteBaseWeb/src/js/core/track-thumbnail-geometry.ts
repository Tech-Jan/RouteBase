// track-thumbnail-geometry.ts — pure projection math for 2D-canvas track thumbnails.
// No DOM, no canvas → unit-testable without mocks. Replaces the geographic-fit half
// of the deleted Leaflet minimap factory (CM-02 D4).
import type { GateLine, ShapeRect } from './core-types';
import { must } from './invariant';

export interface GeoBounds {
	minLat: number;
	maxLat: number;
	minLng: number;
	maxLng: number;
}

export interface ThumbPoint {
	x: number;
	y: number;
}

/**
 * Bounds over GPS points, shape rects, effort tracks, and/or gate lines. Explicit loops — never a
 * spread: `Math.min(...pts)` overflows the call stack at ~10k+ points (the property the old
 * map-shared minimap factory guarded, preserved here). Returns null when every input is empty.
 */
export function computeGeoBounds(
	gps: ReadonlyArray<{ lat: number; lng: number }>,
	shapes?: ReadonlyArray<ShapeRect>,
	effortTracks?: ReadonlyArray<ReadonlyArray<{ lat: number; lng: number }>>,
	gateLines?: ReadonlyArray<GateLine>,
): GeoBounds | null {
	let minLat = Infinity;
	let maxLat = -Infinity;
	let minLng = Infinity;
	let maxLng = -Infinity;
	let seen = false;

	const acc = (lat: number, lng: number): void => {
		if (lat < minLat) minLat = lat;
		if (lat > maxLat) maxLat = lat;
		if (lng < minLng) minLng = lng;
		if (lng > maxLng) maxLng = lng;
		seen = true;
	};

	for (let i = 0; i < gps.length; i++) {
		const p = must(gps[i], 'i < length (loop-bounded)');
		acc(p.lat, p.lng);
	}

	if (shapes) {
		for (let i = 0; i < shapes.length; i++) {
			const s = must(shapes[i], 'i < length (loop-bounded)');
			acc(s.minLat, s.minLong);
			acc(s.maxLat, s.maxLong);
		}
	}

	if (effortTracks) {
		for (let t = 0; t < effortTracks.length; t++) {
			const tr = must(effortTracks[t], 't < length (loop-bounded)');
			for (let i = 0; i < tr.length; i++) {
				const p = must(tr[i], 'i < length (loop-bounded)');
				acc(p.lat, p.lng);
			}
		}
	}

	if (gateLines) {
		for (let i = 0; i < gateLines.length; i++) {
			const g = must(gateLines[i], 'i < length (loop-bounded)');
			acc(g.lat1, g.lng1);
			acc(g.lat2, g.lng2);
		}
	}

	if (!seen) return null;
	return { minLat, maxLat, minLng, maxLng };
}

/**
 * Uniform-scale equirectangular fit. Longitude span is scaled by cos(midLat) so
 * shapes aren't east-west distorted; a single min(x,y) scale keeps aspect while
 * centering the track in (cssW × cssH) minus `padding` on every side. North is up.
 */
export function fitTransform(
	b: GeoBounds,
	cssW: number,
	cssH: number,
	padding: number,
): (lat: number, lng: number) => ThumbPoint {
	const midLat = (b.minLat + b.maxLat) / 2;
	const kx = Math.cos((midLat * Math.PI) / 180);
	const spanX = (b.maxLng - b.minLng) * kx;
	const spanY = b.maxLat - b.minLat;
	const innerW = cssW - 2 * padding;
	const innerH = cssH - 2 * padding;

	// Zero-extent guard: a single point or degenerate axis maps to that axis's
	// center (scale 0 there) instead of dividing by zero.
	let scale: number;
	if (spanX <= 0 && spanY <= 0) {
		scale = 0;
	} else if (spanX <= 0) {
		scale = innerH / spanY;
	} else if (spanY <= 0) {
		scale = innerW / spanX;
	} else {
		scale = Math.min(innerW / spanX, innerH / spanY);
	}

	const offsetX = padding + (innerW - spanX * scale) / 2;
	const offsetY = padding + (innerH - spanY * scale) / 2;

	return (lat: number, lng: number): ThumbPoint => ({
		x: offsetX + (lng - b.minLng) * kx * scale,
		y: offsetY + (b.maxLat - lat) * scale,
	});
}

// ── Web-Mercator tile math (used only when a raster basemap renders) ──────────
// The equirectangular fit above keeps its callers (offline / tiles-failed
// fallback). When tiles paint, the track must share the tiles' projection or it
// visibly slides off the roads — so tile rendering uses these Web-Mercator fns.

const MERCATOR_MAX_LAT = 85.05112878; // where the projection diverges; clamp to it

/** Slippy-map source parameters (from the raster style JSON). */
export interface TileSourceParams {
	minZoom: number;
	maxZoom: number;
	tileSize: number;
}

/** One tile to fetch + where it lands on the canvas (css px, top-left + edge). */
export interface TileRef {
	z: number;
	x: number;
	y: number;
	dx: number;
	dy: number;
	size: number;
}

export interface TileLayout {
	z: number;
	tiles: TileRef[];
	/** Web-Mercator lat/lng → canvas css px at the chosen zoom, same origin as the tiles. */
	project: (lat: number, lng: number) => ThumbPoint;
}

/** Normalised Web-Mercator X in [0,1] (0 at −180°, 1 at +180°). */
function mercatorNormX(lng: number): number {
	return (lng + 180) / 360;
}

/** Normalised Web-Mercator Y in [0,1] (0 at the north edge, 1 at the south). */
function mercatorNormY(lat: number): number {
	const clamped = Math.max(-MERCATOR_MAX_LAT, Math.min(MERCATOR_MAX_LAT, lat));
	const rad = (clamped * Math.PI) / 180;
	return 0.5 - Math.log(Math.tan(Math.PI / 4 + rad / 2)) / (2 * Math.PI);
}

/**
 * Pick the highest slippy zoom at which the bounds (mercator span) still fit
 * inside innerW × innerH at `tileSize` px/tile, clamped to the source's range.
 * A zero-extent track (single point) has no span → the source max zoom.
 */
export function pickTileZoom(
	b: GeoBounds,
	innerW: number,
	innerH: number,
	src: TileSourceParams,
): number {
	const spanNormX = mercatorNormX(b.maxLng) - mercatorNormX(b.minLng);
	// normY grows southward, so the south (minLat) edge has the larger value.
	const spanNormY = mercatorNormY(b.minLat) - mercatorNormY(b.maxLat);

	let worldFitX = Infinity;
	let worldFitY = Infinity;
	if (spanNormX > 0) worldFitX = innerW / spanNormX;
	if (spanNormY > 0) worldFitY = innerH / spanNormY;
	const worldSize = Math.min(worldFitX, worldFitY); // px across the whole map

	if (!Number.isFinite(worldSize)) return src.maxZoom; // zero-extent → max zoom
	const z = Math.floor(Math.log2(worldSize / src.tileSize));
	return Math.max(src.minZoom, Math.min(src.maxZoom, z));
}

/**
 * Full tile layout for a raster-backed thumbnail: pick the zoom, centre the
 * bounds in the canvas, enumerate the tiles overlapping the viewport (with their
 * destination rects), and return a matching mercator projection for the track.
 * `padding` reserves the same css-px inset the fallback uses, so the track never
 * touches the card edge. Returns null when the bounds are degenerate/unusable.
 */
export function computeTileLayout(
	b: GeoBounds,
	cssW: number,
	cssH: number,
	padding: number,
	src: TileSourceParams,
): TileLayout | null {
	const innerW = cssW - 2 * padding;
	const innerH = cssH - 2 * padding;
	if (innerW <= 0 || innerH <= 0) return null;

	const z = pickTileZoom(b, innerW, innerH, src);
	const worldSize = src.tileSize * Math.pow(2, z);

	const worldX = (lng: number): number => mercatorNormX(lng) * worldSize;
	const worldY = (lat: number): number => mercatorNormY(lat) * worldSize;

	// Centre the bounds' mercator midpoint on the canvas centre.
	const centreX = (worldX(b.minLng) + worldX(b.maxLng)) / 2;
	const centreY = (worldY(b.maxLat) + worldY(b.minLat)) / 2;
	const originX = centreX - cssW / 2; // world px at canvas (0,0)
	const originY = centreY - cssH / 2;

	const maxIndex = Math.pow(2, z) - 1;
	const firstX = Math.floor(originX / src.tileSize);
	const lastX = Math.floor((originX + cssW) / src.tileSize);
	const firstY = Math.floor(originY / src.tileSize);
	const lastY = Math.floor((originY + cssH) / src.tileSize);

	const tiles: TileRef[] = [];
	for (let ty = firstY; ty <= lastY; ty++) {
		if (ty < 0 || ty > maxIndex) continue; // no vertical wrap
		for (let tx = firstX; tx <= lastX; tx++) {
			if (tx < 0 || tx > maxIndex) continue; // regional app → clamp, don't wrap
			tiles.push({
				z,
				x: tx,
				y: ty,
				dx: tx * src.tileSize - originX,
				dy: ty * src.tileSize - originY,
				size: src.tileSize,
			});
		}
	}

	const project = (lat: number, lng: number): ThumbPoint => ({
		x: worldX(lng) - originX,
		y: worldY(lat) - originY,
	});

	return { z, tiles, project };
}
