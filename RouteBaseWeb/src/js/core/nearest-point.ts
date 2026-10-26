/**
 * nearest-point.ts — Fast nearest-GPS-point lookup with a spatial grid index
 * and hover hysteresis.
 *
 * Extracted from the old utils god-file (CB-01 dissolution). The spatial-index cache
 * and hysteresis state are module-level singletons keyed by array *reference* —
 * they are shared across all callers and reset when the dataset identity changes.
 */
import type { GpsPoint } from './core-types';
import { getGpsData } from './gps-data';
import { must } from './invariant';

// ── Spatial grid index for fast nearest-point lookups ────────────
// Built once per GPS dataset; turns O(n) linear scan into O(1) cell
// lookup + small neighbourhood search.

let _spatialIndex: {
	grid: Map<string, number[]>;
	cellSize: number;
	data: GpsPoint[];
} | null = null;
let _spatialIndexSource: GpsPoint[] | null = null;

/**
 * Build (or return cached) spatial grid index for the given GPS data.
 */
function _getSpatialIndex(data: GpsPoint[]): {
	grid: Map<string, number[]>;
	cellSize: number;
	data: GpsPoint[];
} {
	if (_spatialIndex && _spatialIndexSource === data) return _spatialIndex;

	// Cell size in degrees — ~100m at mid latitudes. Small enough for
	// accurate results, large enough that we only check ≤9 cells.
	const cellSize = 0.001;
	const grid: Map<string, number[]> = new Map();

	for (let i = 0; i < data.length; i++) {
		const p = must(data[i], 'i < length (loop-bounded)');
		const cx = Math.floor(p.lng / cellSize);
		const cy = Math.floor(p.lat / cellSize);
		const key = cx + "," + cy;
		let bucket = grid.get(key);
		if (!bucket) {
			bucket = [];
			grid.set(key, bucket);
		}
		bucket.push(i);
	}

	_spatialIndex = { grid, cellSize, data };
	_spatialIndexSource = data;
	return _spatialIndex;
}

// Hysteresis state — suppresses micro-oscillation when the cursor sits at
// the midpoint between two adjacent GPS points (equidistant from both).
let _lastNearestIndex = -1;
let _lastNearestDist = Infinity;
// Minimum improvement (as a fraction of the current best distance) before
// we switch to a new nearest point.  0.08 = 8%: the new point must be at
// least 8% closer than the currently locked one.  Applied ONLY when the
// two candidates are very close together (both within 2× the current dist),
// so it never traps the cursor far from the track.
const _HYSTERESIS_FRAC = 0.08;

/**
 * Find index of the nearest GPS point to the given coordinates.
 * Uses a spatial grid for fast O(1) average lookups instead of O(n) linear scan.
 * A small hysteresis band prevents oscillation when hovering exactly between
 * two adjacent points without ever trapping the result when the cursor moves
 * far away.
 * @param lat
 * @param lng
 * @param gpsArray  defaults to getGpsData()
 */
export function findNearestPointIndex(lat: number, lng: number, gpsArray?: GpsPoint[]): number {
	const data = gpsArray || getGpsData();
	if (data.length === 0) return -1;

	// Reset hysteresis state when the dataset changes
	if (data !== _spatialIndexSource) {
		_lastNearestIndex = -1;
		_lastNearestDist = Infinity;
	}

	const { index: nearestIndex, dist: minDist } = _scanNearest(data, lat, lng);

	// Hysteresis: suppress micro-oscillation when hovering exactly at the
	// midpoint between two adjacent GPS points.
	//
	// Key constraint: only apply hysteresis when the winner changed AND the
	// new distance is within 2× the locked distance.  Beyond 2× the cursor
	// has clearly moved away from the previously locked point, so we let the
	// result update immediately — this prevents the marker from freezing when
	// the user moves off the track.
	if (_lastNearestIndex !== -1 && nearestIndex !== _lastNearestIndex) {
		const threshold = _lastNearestDist * (1 + _HYSTERESIS_FRAC);
		const tooFarFromLast = minDist > _lastNearestDist * 2.0;

		if (!tooFarFromLast && minDist >= threshold) {
			// New point is close but not significantly closer — keep old one
			return _lastNearestIndex;
		}
	}

	_lastNearestIndex = nearestIndex;
	_lastNearestDist = minDist;
	return nearestIndex;
}

/**
 * Nearest-point index for a ONE-SHOT lookup — same grid, no hysteresis and no shared state.
 *
 * The band above exists for a hover stream, where it is deliberately sticky. A lookup that answers
 * "which sample is this photo standing on?" must not inherit the point a cursor happened to be locked
 * to, nor leave its own answer behind as the next hover's lock.
 */
export function findNearestPointIndexExact(lat: number, lng: number, gpsArray?: GpsPoint[]): number {
	const data = gpsArray || getGpsData();
	if (data.length === 0) return -1;
	return _scanNearest(data, lat, lng).index;
}

/** Grid lookup + sparse-data fallback. Squared degrees — comparison only, never a distance. */
function _scanNearest(data: GpsPoint[], lat: number, lng: number): { index: number; dist: number } {
	const idx = _getSpatialIndex(data);
	const cs = idx.cellSize;
	const cx = Math.floor(lng / cs);
	const cy = Math.floor(lat / cs);

	let minDist = Infinity;
	let nearestIndex = -1;

	// Search the target cell and its 8 neighbours (3×3 block)
	for (let di = -1; di <= 1; di++) {
		for (let dj = -1; dj <= 1; dj++) {
			const key = cx + di + "," + (cy + dj);
			const bucket = idx.grid.get(key);
			if (!bucket) continue;
			for (let k = 0; k < bucket.length; k++) {
				const ii = must(bucket[k], 'k < length (loop-bounded)');
				const p = must(data[ii], 'bucket holds indices into data');
				const dlat = p.lat - lat;
				const dlng = p.lng - lng;
				const d = dlat * dlat + dlng * dlng;
				if (d < minDist) {
					minDist = d;
					nearestIndex = ii;
				}
			}
		}
	}

	// Fallback for very sparse data where no neighbour cells had points
	if (nearestIndex === -1) {
		for (let i = 0; i < data.length; i++) {
			const p = must(data[i], 'i < length (loop-bounded)');
			const dlat2 = p.lat - lat;
			const dlng2 = p.lng - lng;
			const d2 = dlat2 * dlat2 + dlng2 * dlng2;
			if (d2 < minDist) {
				minDist = d2;
				nearestIndex = i;
			}
		}
	}

	return { index: nearestIndex, dist: minDist };
}
