// @vitest-environment node
import type { GpsPoint } from './core-types';
import { describe, it, expect } from 'vitest';
import { findNearestPointIndex, findNearestPointIndexExact } from './nearest-point';

// Minimal typed GPS point builder.
function pt(lat: number, lng: number): GpsPoint {
	return { lat, lng, altitude: 0, speed: 0, distance: 0 } as GpsPoint;
}

describe('findNearestPointIndex', () => {
	it('returns the nearest point in a dense grid', () => {
		const data = [pt(0, 0), pt(0, 0.001), pt(0, 0.002)];
		expect(findNearestPointIndex(0, 0.0011, data)).toBe(1);
	});

	it('falls back to a full scan for sparse data with empty neighbour cells', () => {
		const data = [pt(0, 0), pt(5, 5)];
		expect(findNearestPointIndex(2, 2, data)).toBe(0);
	});

	it('hysteresis holds the locked point at a midpoint but releases when a new point is much closer', () => {
		const data = [pt(0, 0), pt(0, 0.001)]; // two adjacent points, same array across calls
		// Lock onto index 0.
		expect(findNearestPointIndex(0, 0.0004, data)).toBe(0);
		// Just past the midpoint: index 1 is geometrically nearer, but hysteresis holds 0.
		expect(findNearestPointIndex(0, 0.00052, data)).toBe(0);
		// Right on point 1: far closer than the locked point -> releases to index 1.
		expect(findNearestPointIndex(0, 0.001, data)).toBe(1);
	});
});

// The sticky band belongs to a hover stream. A one-shot lookup ("which sample is this photo standing
// on?") must neither inherit a cursor's lock nor leave its own answer behind as the next hover's.
describe('findNearestPointIndexExact', () => {
	it('answers geometrically and leaves the hover lock untouched', () => {
		const data = [pt(0, 0), pt(0, 0.001)];
		expect(findNearestPointIndex(0, 0.0004, data)).toBe(0); // hover locks onto index 0

		expect(findNearestPointIndexExact(0, 0.00052, data)).toBe(1); // past the midpoint: no stickiness

		expect(findNearestPointIndex(0, 0.00052, data)).toBe(0); // the hover lock still holds
	});

	it('returns -1 for an empty track', () => {
		expect(findNearestPointIndexExact(0, 0, [])).toBe(-1);
	});
});
