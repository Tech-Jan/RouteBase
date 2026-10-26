// @vitest-environment node
import type { GpsPoint } from './core-types';
import { describe, it, expect } from 'vitest';
import { haversineDistance, calculateCumulativeDistances } from './geo';
import { must } from './invariant';

// Minimal typed GPS point builder.
function pt(lat: number, lng: number): GpsPoint {
	return { lat, lng, altitude: 0, speed: 0, distance: 0 } as GpsPoint;
}

describe('haversineDistance', () => {
	it('matches a known fixture (0.01deg lat at the equator ~= 1111.9 m)', () => {
		const d = haversineDistance(0, 0, 0.01, 0);
		expect(d).toBeGreaterThan(1111.5);
		expect(d).toBeLessThan(1112.5);
		expect(d).toBeCloseTo(1111.95, 0); // pins EARTH_RADIUS_M = 6371000
	});
});

describe('calculateCumulativeDistances', () => {
	it('sets .distance in km cumulatively with [0] = 0', () => {
		const track = [pt(0, 0), pt(0.01, 0), pt(0.02, 0)];
		calculateCumulativeDistances(track);
		expect(must(track[0], 'fixture point 0').distance).toBe(0);
		expect(must(track[1], 'fixture point 1').distance).toBeCloseTo(1.11195, 3); // ~1.112 km per 0.01deg
		expect(must(track[2], 'fixture point 2').distance).toBeCloseTo(2.2239, 3); // cumulative
	});
});
