/**
 * replay-engine-base.ts — Shared Replay Engine Utilities
 *
 * Named ESM exports shared between activity-replay.ts and segment-stats-replay.ts:
 *
 *   • GeoJSON marker management (add/update/remove circle layers)
 *   • Distance-based animation loop
 *   • Time-based animation loop
 *   • Camera update helpers (jumpTo with terrain elevation)
 *   • Cumulative-time computation
 *
 * Loaded via core/core.ts (ES module entry in _Layout.cshtml).
 *
 * Sibling seam: core/replay-follow-camera.ts owns the follow handover (who holds the map canvas) and
 * the paused idle camera — the half this file's updateCamera does NOT cover.
 */
import type { GpsPoint } from './core-types';
import { must } from './invariant';
import { pinFollowCentre, shouldYieldToEase, writeFollowCentre } from './replay-follow-camera';
import {
	safeCleanupLayers,
	applyTerrainElevation,
	createCameraChangeDetector,
} from './map-utils-shared';

// ================================================================
// 1. GeoJSON Marker Manager
//
//    Manages a GeoJSON circle marker on a MapLibre map.
//    Single source + two layers per marker (border ring + fill dot).
//    Uses 'circle-pitch-alignment': 'viewport' so markers stay
//    screen-space and aren't draped into terrain.
//
//    Usage:
//      let mgr = createMarkerManager(map, {
//          sourceId: 'my-replay-marker',
//          borderLayerId: 'my-replay-marker-border',
//          fillLayerId: 'my-replay-marker-fill',
//          borderRadius: 14,
//          fillRadius: 10,
//          fillColor: '#007bff',
//          borderColor: '#ffffff'
//      });
//      mgr.init(startLng, startLat);
//      mgr.setPosition(lng, lat);  // per frame
//      mgr.remove();               // cleanup
// ================================================================

/** Public shape of the single-marker GeoJSON manager (DC-13: single source of truth,
 * replacing the drift-prone duplicate that lived in types/globals.d.ts). */
export interface ReplayMarkerManager {
	init(lng: number, lat: number): void;
	setPosition(lng: number, lat: number): void;
	remove(): void;
	isAdded(): boolean;
}

/**
 * Create a single-marker GeoJSON manager.
 *
 * @param map   MapLibre map instance
 * @param opts  Configuration
 */
export function createMarkerManager(
	map: maplibregl.Map,
	opts: {
		sourceId: string;
		borderLayerId: string;
		fillLayerId: string;
		borderRadius?: number;
		fillRadius?: number;
		fillColor?: string;
		borderColor?: string;
		borderOpacity?: number;
		fillOpacity?: number;
	},
): ReplayMarkerManager {
	const sourceId = opts.sourceId;
	const borderLayerId = opts.borderLayerId;
	const fillLayerId = opts.fillLayerId;
	const borderRadius = opts.borderRadius || 14;
	const fillRadius = opts.fillRadius || 10;
	const fillColor = opts.fillColor || "#007bff";
	const borderColor = opts.borderColor || "#ffffff";
	const borderOpacity = opts.borderOpacity !== undefined ? opts.borderOpacity : 0.9;
	const fillOpacity = opts.fillOpacity !== undefined ? opts.fillOpacity : 1.0;

	let _feature: GeoJSON.Feature<GeoJSON.Point> | null = null;
	let _added = false;
	// RP-03: the idle-fallback listener, kept so remove() can cancel it before it fires.
	let _pendingIdle: (() => void) | null = null;

	function _addLayers(): void {
		try {
			if (map.getSource(sourceId)) {
				_added = true;
				_pendingIdle = null;
				return;
			}

			map.addSource(sourceId, {
				type: "geojson",
				data: { type: "FeatureCollection", features: _feature ? [_feature] : [] },
			});

			map.addLayer({
				id: borderLayerId,
				type: "circle",
				source: sourceId,
				paint: {
					"circle-radius": borderRadius,
					"circle-color": borderColor,
					"circle-opacity": borderOpacity,
					"circle-pitch-alignment": "viewport",
				},
			});

			map.addLayer({
				id: fillLayerId,
				type: "circle",
				source: sourceId,
				paint: {
					"circle-radius": fillRadius,
					"circle-color": fillColor,
					"circle-opacity": fillOpacity,
					"circle-stroke-width": 0,
					"circle-pitch-alignment": "viewport",
				},
			});

			_added = true;
			_pendingIdle = null;
		} catch (e) {
			console.warn("MarkerManager: layer setup failed:", e);
		}
	}

	return {
		/**
		 * Initialize marker at the given position.
		 * Tries synchronously first, falls back to 'idle' event.
		 */
		init: (lng: number, lat: number): void => {
			_feature = {
				type: "Feature",
				properties: {},
				geometry: { type: "Point", coordinates: [lng, lat] },
			};

			try {
				_addLayers();
			} catch (_e) { /* teardown: may already be removed */ }

			if (!_added) {
				// RP-03: wrap so the ref clears itself when it fires and remove() can cancel it.
				_pendingIdle = () => { _pendingIdle = null; _addLayers(); };
				map.once("idle", _pendingIdle);
			}
		},

		/**
		 * Update marker position. Retries layer creation if not yet added.
		 */
		setPosition: (lng: number, lat: number): void => {
			if (!_feature) return;
			_feature.geometry.coordinates[0] = lng;
			_feature.geometry.coordinates[1] = lat;

			if (!_added) {
				try {
					_addLayers();
				} catch (_e) { /* teardown: may already be removed */ }
			}

			try {
				const src = map.getSource(sourceId) as maplibregl.GeoJSONSource | undefined;
				if (src && _feature) src.setData({ type: "FeatureCollection" as const, features: [_feature] });
			} catch (_e) { /* teardown: may already be removed */ }
		},

		/** Remove marker layers and source from the map. */
		remove: (): void => {
			// RP-03: always cancel a pending idle-fallback add + clear the feature, even when
			// nothing was added yet — otherwise a late idle would re-add a stuck marker onto
			// the normal (non-replay) map after a fast exit.
			if (_pendingIdle) {
				try { map.off("idle", _pendingIdle); } catch (_e) { /* map may already be gone */ }
				_pendingIdle = null;
			}
			if (_added) safeCleanupLayers(map, [fillLayerId, borderLayerId], [sourceId]);
			_added = false;
			_feature = null;
		},

		/** Whether the layers have been successfully added. */
		isAdded: (): boolean => _added,
	};
}

// ================================================================
// 2. Animation Loop Helpers
//
//    Shared frame-timing logic: delta-time computation,
//    requestAnimationFrame scheduling, and the two standard
//    advance modes (distance-based, time-based).
//
//    These are functional helpers — they take state objects and
//    return next-state, keeping them framework-agnostic.
// ================================================================

/**
 * Compute distance-based index advancement for a single frame.
 *
 * Given a track with cumulative `.distance` (km), the current fractional
 * index, and a distance to advance (km), returns the new fractional index.
 *
 * @param track         GPS points with `.distance` (km)
 * @param currentIndex  Current fractional index
 * @param advanceKm     Distance to advance this frame (km)
 */
export function advanceByDistance(
	track: GpsPoint[],
	currentIndex: number,
	advanceKm: number,
): { index: number; finished: boolean } {
	const ci = Math.floor(currentIndex);
	const frac = currentIndex - ci;
	const cp = track[ci];
	const np = ci < track.length - 1 ? track[ci + 1] : cp;

	if (!cp || typeof cp.distance !== "number") {
		const next = ci + 1;
		return { index: next, finished: next >= track.length - 1 };
	}

	let curDist = cp.distance;
	if (frac > 0 && np !== undefined && np !== cp && typeof np.distance === "number") {
		curDist += (np.distance - cp.distance) * frac;
	}
	const targetDist = curDist + advanceKm;

	for (let i = ci + 1; i < track.length; i++) {
		const pt = must(track[i], 'i < length (loop-bounded)');
		if (pt.distance >= targetDist) {
			const pd = track[i - 1]?.distance;
			// RP-07: an undefined predecessor distance makes (targetDist - pd)/r → NaN, which
			// silently freezes the rAF loop. Fall back to integer stepping to this index.
			if (typeof pd !== "number") return { index: i, finished: i >= track.length - 1 };
			const nd = pt.distance;
			const r = nd - pd;
			const newIdx = r > 0 ? i - 1 + (targetDist - pd) / r : i;
			return { index: newIdx, finished: newIdx >= track.length - 1 };
		}
	}

	// Didn't find — either at end or advance by 1
	if (ci >= track.length - 1) {
		return { index: track.length - 1, finished: true };
	}
	return { index: ci + 1, finished: ci + 1 >= track.length - 1 };
}

/**
 * Compute time-based index advancement for a single frame.
 *
 * Given a track with `._cumulativeTime`, the current time (seconds),
 * and a starting search index, returns the new fractional index.
 *
 * @param track       GPS points with `._cumulativeTime`
 * @param currentTime Current elapsed time in seconds
 * @param startIndex  Index to start searching from (optimization)
 */
export function advanceByTime(
	track: GpsPoint[],
	currentTime: number,
	startIndex: number,
): { index: number; finished: boolean } {
	let lo = Math.max(0, Math.floor(startIndex));

	// Fast-forward from the last known index
	while (
		lo < track.length - 2 &&
		(must(track[lo + 1], 'lo+1 < length-1 (loop-bounded)')._cumulativeTime || 0) <= currentTime
	) {
		lo++;
	}

	if (lo < track.length - 1) {
		const t1 = must(track[lo], 'lo < length-1 (guarded above)')._cumulativeTime || 0;
		const t2 = must(track[lo + 1], 'lo+1 <= length-1 (guarded above)')._cumulativeTime || 0;
		const seg = t2 - t1;
		const newIdx = seg > 0 ? lo + Math.max(0, Math.min(1, (currentTime - t1) / seg)) : lo;
		return { index: newIdx, finished: false };
	}

	return { index: track.length - 1, finished: true };
}

/**
 * Interpolate lat/lng between two track points at a fractional index.
 *
 * @param track     GPS points with `.lat` and `.lng`
 * @param fracIndex Fractional index (e.g. 42.7)
 */
export function interpolatePosition(
	track: GpsPoint[],
	fracIndex: number,
): { lat: number; lng: number } | null {
	const floorIdx = Math.floor(fracIndex);
	if (floorIdx < 0 || floorIdx >= track.length) return null;

	const p1 = track[floorIdx];
	if (!p1 || typeof p1.lng !== "number") return null;

	const fraction = fracIndex - floorIdx;
	if (fraction <= 0 || floorIdx >= track.length - 1) {
		return { lat: p1.lat, lng: p1.lng };
	}

	const p2 = must(track[floorIdx + 1], 'floorIdx < length-1 (guarded above)');
	return {
		lat: p1.lat + (p2.lat - p1.lat) * fraction,
		lng: p1.lng + (p2.lng - p1.lng) * fraction,
	};
}

// ================================================================
// 3. Camera Update Helper
//
//    Combines jumpTo + terrain elevation fix into one call.
//    Used identically by both activity-replay and segment-stats-replay.
// ================================================================

/**
 * Update the MapLibre camera with change detection and terrain elevation.
 *
 * @param map              MapLibre map instance
 * @param cameraParams     { center, zoom, bearing, pitch }
 * @param changeDetector   From createCameraChangeDetector()
 * @param terrainElevState Mutable { lastTime } for throttling
 */
export function updateCamera(
	map: maplibregl.Map,
	cameraParams: { center: [number, number] },
	changeDetector: ReturnType<typeof createCameraChangeDetector>,
	terrainElevState: { lastTime?: number },
): void {
	const [lng, lat] = cameraParams.center;

	// Ungated, and first: while MapLibre is driving, the centre is held by the camera hook in
	// core/replay-follow-camera, and the hook can only do that if every frame tells it where the
	// marker is — including the frames this function goes on to skip.
	pinFollowCentre(map, lng, lat);

	// The playing camera writes as often as the idle one, so it stands back for the same one thing:
	// an in-flight ease, which our jumpTo would cancel. Gestures are safe — writeFollowCentre is
	// shielded — and standing back for them is what used to strand the marker *while playing*.
	if (shouldYieldToEase(map)) return;

	// Zoom/bearing/pitch are MapLibre's from engage onwards, so they are deliberately excluded from
	// the comparison — passing the live camera would make the user's own pinch read as "changed" and
	// trigger a jumpTo that fights it. The detector reduces to its ~1 m² distance threshold.
	if (changeDetector.hasChanged(lat, lng, 0, 0, 0)) {
		writeFollowCentre(map, cameraParams.center);
	}

	applyTerrainElevation(map, lng, lat, terrainElevState);
}

// ================================================================
// 4. Cumulative Time Computation
//
//    Shared between activity-replay.ts (which computes _cumulativeTime
//    from time strings or linear interpolation) and any future replay
//    engine that needs time-based playback.
// ================================================================

/**
 * Compute `_cumulativeTime` on each point of a track.
 *
 * Supports three modes:
 *   1. Points have `.time` as "HH:MM:SS" strings → parse and compute offsets
 *   2. Points have no time strings but totalTimeSeconds > 0 → linear interpolation
 *   3. Neither → index-based (point 0 = 0s, point N = Ns)
 *
 * @param track            GPS points (mutated in place)
 * @param totalTimeSeconds Known total duration (0 if unknown)
 * @returns The effective totalTimeSeconds after computation
 */
export function computeCumulativeTime(track: GpsPoint[], totalTimeSeconds: number): number {
	if (!track || track.length === 0) return totalTimeSeconds || 0;

	const p0 = must(track[0], 'non-empty (guarded above)');
	let hasTimeStrings = false;
	if (p0.time && typeof p0.time === "string" && p0.time.indexOf(":") !== -1) {
		hasTimeStrings = true;
		const parseTime = (s: string): number => {
			const p = s.split(":").map(Number);
			// = NaN defaults are dead under the length checks; `+x` ≡ Number(x) so map(Number) is bit-identical.
			const [h = NaN, m = NaN, sec = NaN] = p;
			return p.length === 3 ? h * 3600 + m * 60 + sec : p.length === 2 ? h * 60 + m : 0;
		};
		const start = parseTime(p0.time);
		for (let i = 0; i < track.length; i++) {
			const pt = must(track[i], 'i < length (loop-bounded)');
			if (pt.time && typeof pt.time === "string") {
				let e = parseTime(pt.time) - start;
				if (e < 0) e += 86400;
				pt._cumulativeTime = e;
			} else {
				pt._cumulativeTime = i > 0 ? must(track[i - 1], 'i-1 >= 0 (loop-bounded)')._cumulativeTime : 0;
			}
		}
		// ?? 0 keeps the old `undefined > 0` semantics (false → guard short-circuits).
		const last = must(track.at(-1), 'non-empty (guarded above)')._cumulativeTime ?? 0;
		if (
			last > 0 &&
			(!totalTimeSeconds || Math.abs(last - totalTimeSeconds) < totalTimeSeconds * 0.1)
		) {
			totalTimeSeconds = last;
		}
	}

	if (!hasTimeStrings && totalTimeSeconds > 0) {
		for (let j = 0; j < track.length; j++) {
			const pt = must(track[j], 'j < length (loop-bounded)');
			pt._cumulativeTime = (j / Math.max(1, track.length - 1)) * totalTimeSeconds; // RP-09: single-point guard
		}
	} else if (!hasTimeStrings) {
		for (let k = 0; k < track.length; k++) {
			const pt = must(track[k], 'k < length (loop-bounded)');
			pt._cumulativeTime = k;
		}
		totalTimeSeconds = track.length;
	}

	return totalTimeSeconds;
}
