import { describe, it, expect } from 'vitest';
import { gateForward, shapeScreenRect } from './shape-layer';
import { gateFromPoints, zoneFromBounds } from './shape-model';
import type { MapAdapter } from './map-adapter';

// SG-02: shapeScreenRect must bound all four projected corners, not just nw/se. Under the app's
// default terrain pitch (10°) or any bearing rotation the two-corner shortcut mis-bounds the tilted
// quad. These fakes stand in for a real MapLibre map (analysis-shapefilter-geom.md SG-02).

function adapterFrom(project: (lat: number, lng: number) => { x: number; y: number }): MapAdapter {
	return {
		project,
		unproject: () => ({ lat: 0, lng: 0 }),
		onViewChange: () => () => {},
		getContainer: () => document.createElement('div'),
		setInteractive: () => {},
	};
}

const zone = zoneFromBounds({ south: 10, west: 30, north: 20, east: 40 });

describe('shapeScreenRect', () => {
	it('reproduces the two-corner formula under a north-up (unrotated) projection', () => {
		const project = (lat: number, lng: number) => ({ x: lng, y: -lat });
		const adapter = adapterFrom(project);
		const a = project(zone.north, zone.west);
		const b = project(zone.south, zone.east);
		expect(shapeScreenRect(zone, adapter)).toEqual({
			x: Math.min(a.x, b.x),
			y: Math.min(a.y, b.y),
			w: Math.abs(b.x - a.x),
			h: Math.abs(b.y - a.y),
		});
	});

	it('bounds all four true corners under a rotated projection', () => {
		// 45° rotation of the (lng,lat) point — a stand-in for a bearing-rotated camera.
		const t = Math.PI / 4, cos = Math.cos(t), sin = Math.sin(t);
		const project = (lat: number, lng: number) => ({ x: lng * cos - lat * sin, y: lng * sin + lat * cos });
		const adapter = adapterFrom(project);

		const corners = [
			project(zone.north, zone.west),
			project(zone.north, zone.east),
			project(zone.south, zone.west),
			project(zone.south, zone.east),
		];
		const r = shapeScreenRect(zone, adapter);
		// Every real corner lies inside the returned box.
		for (const c of corners) {
			expect(c.x).toBeGreaterThanOrEqual(r.x - 1e-9);
			expect(c.x).toBeLessThanOrEqual(r.x + r.w + 1e-9);
			expect(c.y).toBeGreaterThanOrEqual(r.y - 1e-9);
			expect(c.y).toBeLessThanOrEqual(r.y + r.h + 1e-9);
		}
		// The old nw/se-only box mis-bounds this rotated quad: at least one true corner escapes it.
		const a = project(zone.north, zone.west), b = project(zone.south, zone.east);
		const oldBox = { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), w: Math.abs(b.x - a.x), h: Math.abs(b.y - a.y) };
		const anyOutside = corners.some(
			(c) => c.x < oldBox.x - 1e-9 || c.x > oldBox.x + oldBox.w + 1e-9 || c.y < oldBox.y - 1e-9 || c.y > oldBox.y + oldBox.h + 1e-9,
		);
		expect(anyOutside).toBe(true);
	});
});

describe('gateForward', () => {
	// Equator gates, so cos(refLat) = 1 and the frame conversion is the identity — what is under test
	// is the normal's direction, not the latitude scaling.
	const eastWest = gateFromPoints({ lat: 0, lng: 0 }, { lat: 0, lng: 1 }, 'start');
	const southNorth = gateFromPoints({ lat: 0, lng: 0 }, { lat: 1, lng: 0 }, 'start');

	it('reproduces the GeoLine normal under a north-up camera', () => {
		// GeoLine rotates the gate vector +90° in (x = lng·cosLat, y = lat): a west→east gate's normal
		// points north, a south→north gate's points west. North-up ⇒ north is screen-up (−y) and west
		// is screen-left (−x). Independent ground truth — not read back from the implementation.
		const adapter = adapterFrom((lat: number, lng: number) => ({ x: lng, y: -lat }));

		const ew = gateForward(eastWest, adapter);
		expect(ew.x).toBeCloseTo(0, 9);
		expect(ew.y).toBeCloseTo(-1, 9);

		const sn = gateForward(southNorth, adapter);
		expect(sn.x).toBeCloseTo(-1, 9);
		expect(sn.y).toBeCloseTo(0, 9);
	});

	it('follows a non-conformal camera instead of assuming a screen right angle', () => {
		// A shear stands in for a pitched camera: linear (so the true projected normal is exact and
		// computable) but it does not preserve right angles — exactly what breaks the screen rotation.
		const sheared = (lat: number, lng: number) => ({ x: lng + lat * 0.5, y: -lat * 0.4 });
		const adapter = adapterFrom(sheared);

		// This gate's forward is due north, so project a northward step to get the truth.
		const base = sheared(0, 0.5), north = sheared(1, 0.5);
		const dx = north.x - base.x, dy = north.y - base.y;
		const len = Math.hypot(dx, dy);

		const fwd = gateForward(eastWest, adapter);
		expect(fwd.x).toBeCloseTo(dx / len, 9);
		expect(fwd.y).toBeCloseTo(dy / len, 9);
		// Rotating the DRAWN line 90° answers (0,−1) here — ~38° off the direction it claims to show.
		expect(fwd.y).not.toBeCloseTo(-1, 2);
	});

	it('falls back to the screen rotation when the projection collapses the probe', () => {
		// Extreme pitch can put the probe above the horizon; the arrow must still render something sane.
		const adapter = adapterFrom((lat: number, lng: number) =>
			lat === 0 ? { x: lng, y: 0 } : { x: NaN, y: NaN });
		expect(gateForward(eastWest, adapter)).toEqual({ x: 0, y: -1 });
	});
});
