/**
 * geo.ts — Geographic / GPS distance helpers.
 *
 * Extracted from the old utils god-file (CB-01 dissolution). Pure functions, no I/O.
 */
import type { GpsPoint } from './core-types';
import { EARTH_RADIUS_M } from './constants';
import { must } from './invariant';

/**
 * Haversine distance between two lat/lng points in metres.
 */
export function haversineDistance(
	lat1: number,
	lng1: number,
	lat2: number,
	lng2: number,
): number {
	const R = EARTH_RADIUS_M;
	const dLat = ((lat2 - lat1) * Math.PI) / 180;
	const dLng = ((lng2 - lng1) * Math.PI) / 180;
	const a =
		Math.sin(dLat / 2) * Math.sin(dLat / 2) +
		Math.cos((lat1 * Math.PI) / 180) *
			Math.cos((lat2 * Math.PI) / 180) *
			Math.sin(dLng / 2) *
			Math.sin(dLng / 2);
	const c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
	return R * c;
}

/**
 * Total geometric track length in km — sum of haversine hops. Non-mutating
 * (unlike calculateCumulativeDistances), used to decide a track's distance unit.
 */
export function totalDistanceKm(gpsArray: GpsPoint[]): number {
	if (!gpsArray || gpsArray.length < 2) return 0;
	let total = 0;
	for (let i = 1; i < gpsArray.length; i++) {
		const a = must(gpsArray[i - 1], 'i-1 >= 0 (loop-bounded)');
		const b = must(gpsArray[i], 'i < length (loop-bounded)');
		total += haversineDistance(a.lat, a.lng, b.lat, b.lng) / 1000; // metres → km
	}
	return total;
}

/**
 * Calculate cumulative distances (in km) for an array of GPS points.
 * Mutates the array by setting `point.distance` on each element.
 */
export function calculateCumulativeDistances(gpsArray: GpsPoint[]): void {
	if (!gpsArray || gpsArray.length === 0) return;
	let cumDist = 0;
	must(gpsArray[0], 'non-empty (guarded above)').distance = 0;
	for (let i = 1; i < gpsArray.length; i++) {
		const a = must(gpsArray[i - 1], 'i-1 >= 0 (loop-bounded)');
		const b = must(gpsArray[i], 'i < length (loop-bounded)');
		cumDist += haversineDistance(a.lat, a.lng, b.lat, b.lng) / 1000; // metres → km
		b.distance = cumDist;
	}
}
