// @vitest-environment node
import type { ShapeRect } from './core-types';
import { describe, it, expect } from 'vitest';
import { must } from './invariant';
import {
	computeGeoBounds,
	computeTileLayout,
	fitTransform,
	pickTileZoom,
	type TileSourceParams,
} from './track-thumbnail-geometry';

describe('track-thumbnail-geometry — computeGeoBounds', () => {
	it('finds min/max over gps points without spread', () => {
		const pts = [
			{ lat: 47.5, lng: 8.1 },
			{ lat: 48.0, lng: 7.0 },
			{ lat: 47.2, lng: 9.0 },
			{ lat: 47.9, lng: 7.5 },
		];
		expect(computeGeoBounds(pts)).toEqual({ minLat: 47.2, maxLat: 48.0, minLng: 7.0, maxLng: 9.0 });

		// Loop-property smoke: a 20k-point array returns finite bounds (a spread
		// would blow the call stack here).
		const big = Array.from({ length: 20000 }, (_, i) => ({ lat: 47 + i * 1e-5, lng: 8 - i * 1e-5 }));
		const b = computeGeoBounds(big)!;
		expect(Number.isFinite(b.minLat) && Number.isFinite(b.maxLng)).toBe(true);
		expect(b.minLat).toBeCloseTo(47);
	});

	it('uses shape rects when gps is empty', () => {
		const shapes: ShapeRect[] = [
			{ minLat: 47.0, maxLat: 47.5, minLong: 7.0, maxLong: 7.5, role: 'zone' },
			{ minLat: 47.4, maxLat: 48.0, minLong: 7.2, maxLong: 8.0, role: 'any' },
		];
		expect(computeGeoBounds([], shapes)).toEqual({
			minLat: 47.0,
			maxLat: 48.0,
			minLng: 7.0,
			maxLng: 8.0,
		});
	});

	it('returns null when both empty', () => {
		expect(computeGeoBounds([])).toBeNull();
		expect(computeGeoBounds([], [])).toBeNull();
	});
});

describe('track-thumbnail-geometry — fitTransform', () => {
	const W = 240;
	const H = 200;
	const P = 12;

	it('maps bounds corners to the padded box with north up', () => {
		const b = { minLat: 47, maxLat: 48, minLng: 7, maxLng: 9 };
		const project = fitTransform(b, W, H, P);
		const top = project(b.maxLat, b.minLng); // north-west
		const bottom = project(b.minLat, b.minLng); // south-west

		// maxLat sits at the smaller y (top); minLat lower down.
		expect(top.y).toBeLessThan(bottom.y);
		// Everything stays inside the padded box.
		for (const pt of [top, bottom, project(b.minLat, b.maxLng), project(b.maxLat, b.maxLng)]) {
			expect(pt.x).toBeGreaterThanOrEqual(P - 1e-6);
			expect(pt.x).toBeLessThanOrEqual(W - P + 1e-6);
			expect(pt.y).toBeGreaterThanOrEqual(P - 1e-6);
			expect(pt.y).toBeLessThanOrEqual(H - P + 1e-6);
		}
	});

	it('preserves geographic aspect via cos(midLat)', () => {
		// At 60°N cos = 0.5, so a lng-span twice the lat-span projects square.
		const b = { minLat: 59.5, maxLat: 60.5, minLng: 10, maxLng: 12 };
		const project = fitTransform(b, W, H, P);
		const xExtent = project(b.maxLat, b.maxLng).x - project(b.maxLat, b.minLng).x;
		const yExtent = project(b.minLat, b.minLng).y - project(b.maxLat, b.minLng).y;
		expect(xExtent).toBeCloseTo(yExtent, 5);
	});

	it('centers the short axis', () => {
		// Wide flat track: latSpan 0 → every point lands on the vertical centre.
		const b = { minLat: 47, maxLat: 47, minLng: 7, maxLng: 9 };
		const project = fitTransform(b, W, H, P);
		expect(project(47, 7).y).toBeCloseTo(H / 2, 6);
		expect(project(47, 9).y).toBeCloseTo(H / 2, 6);
	});

	it('maps a zero-extent track to the canvas center', () => {
		const b = { minLat: 47, maxLat: 47, minLng: 8, maxLng: 8 };
		const project = fitTransform(b, W, H, P);
		const pt = project(47, 8);
		expect(pt.x).toBeCloseTo(W / 2, 6);
		expect(pt.y).toBeCloseTo(H / 2, 6);
	});
});

// ── Web-Mercator tile math ────────────────────────────────────────────────────

describe('track-thumbnail-geometry — pickTileZoom', () => {
	const W = 240;
	const H = 200;
	const P = 12;
	const innerW = W - 2 * P;
	const innerH = H - 2 * P;
	const src: TileSourceParams = { minZoom: 0, maxZoom: 19, tileSize: 256 };

	it('clamps a world-scale track to the source min zoom and a tiny track to max zoom', () => {
		const world = pickTileZoom({ minLat: -80, maxLat: 80, minLng: -179, maxLng: 179 }, innerW, innerH, src);
		expect(world).toBe(0); // whole globe never fits at z>0 → floored below 0, clamped up

		const tiny = pickTileZoom({ minLat: 47.0, maxLat: 47.00002, minLng: 8.0, maxLng: 8.00002 }, innerW, innerH, src);
		expect(tiny).toBe(19); // sub-metre span → beyond max, clamped down
	});

	it('returns the source max zoom for a zero-extent track (single point)', () => {
		expect(pickTileZoom({ minLat: 47, maxLat: 47, minLng: 8, maxLng: 8 }, innerW, innerH, src)).toBe(19);
	});

	it('picks a lower zoom for a larger track (monotonic in extent)', () => {
		const small = pickTileZoom({ minLat: 47.0, maxLat: 47.02, minLng: 8.0, maxLng: 8.02 }, innerW, innerH, src);
		const big = pickTileZoom({ minLat: 47.0, maxLat: 47.6, minLng: 8.0, maxLng: 8.6 }, innerW, innerH, src);
		expect(big).toBeLessThan(small);
	});
});

// Inverse Web-Mercator: tile (z,x,y) top-left → lat/lng, to check the projection.
function tileCornerLatLng(z: number, x: number, y: number, tileSize: number): { lat: number; lng: number } {
	const worldSize = tileSize * Math.pow(2, z);
	const lng = (x * tileSize) / worldSize * 360 - 180;
	const nY = (y * tileSize) / worldSize;
	const rad = 2 * (Math.atan(Math.exp((0.5 - nY) * 2 * Math.PI)) - Math.PI / 4);
	return { lat: (rad * 180) / Math.PI, lng };
}

describe('track-thumbnail-geometry — computeTileLayout', () => {
	const W = 240;
	const H = 200;
	const P = 12;
	const src: TileSourceParams = { minZoom: 0, maxZoom: 19, tileSize: 256 };
	const b = { minLat: 47.35, maxLat: 47.42, minLng: 8.5, maxLng: 8.6 };

	it('tiles the whole canvas with 256-px destination rects and valid indices', () => {
		const layout = computeTileLayout(b, W, H, P, src)!;
		expect(layout).not.toBeNull();
		expect(layout.tiles.length).toBeGreaterThan(0);

		const maxIndex = Math.pow(2, layout.z) - 1;
		for (const t of layout.tiles) {
			expect(t.size).toBe(256);
			expect(t.x).toBeGreaterThanOrEqual(0);
			expect(t.x).toBeLessThanOrEqual(maxIndex);
			expect(t.y).toBeGreaterThanOrEqual(0);
			expect(t.y).toBeLessThanOrEqual(maxIndex);
		}
		// Destination rects must cover the full canvas (left/top ≤ 0, right/bottom ≥ edges).
		const minDx = Math.min(...layout.tiles.map((t) => t.dx));
		const maxDx = Math.max(...layout.tiles.map((t) => t.dx + t.size));
		const minDy = Math.min(...layout.tiles.map((t) => t.dy));
		const maxDy = Math.max(...layout.tiles.map((t) => t.dy + t.size));
		expect(minDx).toBeLessThanOrEqual(0);
		expect(maxDx).toBeGreaterThanOrEqual(W);
		expect(minDy).toBeLessThanOrEqual(0);
		expect(maxDy).toBeGreaterThanOrEqual(H);
	});

	it('projects a tile-corner lat/lng onto that tile pixel (track ↔ tiles aligned)', () => {
		const layout = computeTileLayout(b, W, H, P, src)!;
		const t = must(layout.tiles[0], 'first tile');
		const corner = tileCornerLatLng(t.z, t.x, t.y, t.size);
		const p = layout.project(corner.lat, corner.lng);
		expect(p.x).toBeCloseTo(t.dx, 4);
		expect(p.y).toBeCloseTo(t.dy, 4);
	});

	it('centres the track: the bounds mid-longitude maps to the canvas horizontal centre', () => {
		const layout = computeTileLayout(b, W, H, P, src)!;
		const midLng = (b.minLng + b.maxLng) / 2;
		// worldX is linear in lng, so the mid-longitude lands on the canvas centre.
		expect(layout.project(47.4, midLng).x).toBeCloseTo(W / 2, 4);
	});
});
