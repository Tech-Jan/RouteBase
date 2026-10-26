/**
 * MapLibre Track Rendering
 *
 * Owns all track GeoJSON rendering on a MapLibre GL map:
 *   - Plain track line + start/end markers (addTrackToMap)
 *   - Color gradient tracks by speed/heartrate/elevation/cadence
 *   - Coordinate caching and fitBounds
 *
 * Imported by maplibre-terrain.ts (as TRK) after map creation and during style
 * switches. The public contracts (updateMaplibreTrack, updateMaplibreTrackColors)
 * delegate here from maplibre-terrain.ts.
 *
 * Depends on: maplibre-helpers.ts (H)
 *             getColorForValue
 */
import type { GpsPoint } from '../core/core-types';
import { must } from '../core/invariant';
import type { MapLibreTrackApi } from './maplibre-types';
import { helpers as H } from './maplibre-helpers';
import { buildGpsSpeedScale } from '../core/speed-scale';
import { getColorForValue } from '../core/colors';

// ====================================================================
// STATE — set via bind()
// ====================================================================

let _map: maplibregl.Map | null = null;
let _trackBoundsMap: WeakMap<object, [[number, number], [number, number]]> | null = null;
let _cachedCoords: [number, number][] | null = null;
// The gps array the cached coords + color features were built from (MS-08). A different
// array (reference identity) invalidates both — in-place mutation of the same array does
// NOT (no such mutator exists; the caller always passes a fresh array on data change).
let _coordsSource: GpsPoint[] | null = null;
const _colorGeoCache: { [mode: string]: GeoJSON.Feature | GeoJSON.FeatureCollection } = {};

// ====================================================================
// PUBLIC API
// ====================================================================

export const api: MapLibreTrackApi = {
	bind(map: maplibregl.Map, trackBoundsMap: WeakMap<object, [[number, number], [number, number]]>): void {
		_map = map;
		_trackBoundsMap = trackBoundsMap;
		_cachedCoords = null;
		_coordsSource = null;
		// Clear color cache for new map instance
		for (const key in _colorGeoCache) {
			delete _colorGeoCache[key];
		}
	},

	getCachedCoords(gpsArr: GpsPoint[]): [number, number][] {
		// MS-08: rebuild when a different gps array arrives — otherwise the cached coords
		// (and the color features indexed against them) would silently mismatch new data.
		if (!_cachedCoords || _coordsSource !== gpsArr) {
			_cachedCoords = H.coordsFromGps(gpsArr);
			_coordsSource = gpsArr;
			for (const k in _colorGeoCache) delete _colorGeoCache[k];
		}
		return _cachedCoords;
	},

	addTrackToMap(gpsPointsArr: GpsPoint[], skipFitBounds?: boolean): void {
		if (!_map) return;
		const map = _map;
		const coords = api.getCachedCoords(gpsPointsArr);
		if (!coords || coords.length === 0) {
			console.warn("No valid coordinates to draw track");
			return;
		}

		const lineGeo: GeoJSON.Feature = {
			type: "Feature",
			properties: {},
			geometry: { type: "LineString", coordinates: coords },
		};
		const startGeo: GeoJSON.FeatureCollection = {
			type: "FeatureCollection",
			features: [
				{
					type: "Feature",
					properties: { role: "start" },
					geometry: { type: "Point", coordinates: must(coords[0], 'non-empty coords (caller-guarded)') },
				},
			],
		};
		const endGeo: GeoJSON.FeatureCollection = {
			type: "FeatureCollection",
			features: [
				{
					type: "Feature",
					properties: { role: "end" },
					geometry: { type: "Point", coordinates: must(coords[coords.length - 1], 'non-empty coords (caller-guarded)') },
				},
			],
		};

		function addOrSetSource(id: string, data: GeoJSON.GeoJSON): void {
			if (!map.getSource(id)) {
				try {
					map.addSource(id, { type: "geojson", data: data });
				} catch (e) {
					console.warn("addSource " + id + " failed", e);
				}
			} else {
				try {
					(map.getSource(id) as maplibregl.GeoJSONSource)?.setData(data);
				} catch (e: unknown) { console.warn('[maplibre-track] setGeoJSONData failed:', e); }
			}
		}
		addOrSetSource("track", lineGeo);
		addOrSetSource("track-start-src", startGeo);
		addOrSetSource("track-end-src", endGeo);

		if (!map.getLayer("track-line")) {
			try {
				map.addLayer({
					id: "track-line",
					type: "line",
					source: "track",
					layout: { "line-join": "round", "line-cap": "round" },
					paint: {
						"line-color": "#dc3545",
						"line-width": 3,
						"line-opacity": 0.9,
					},
				});
			} catch (e: unknown) { console.warn('[maplibre-track] addTrackLineLayer failed:', e); }
		}
		if (!map.getLayer("track-start")) {
			try {
				map.addLayer({
					id: "track-start",
					type: "circle",
					source: "track-start-src",
					paint: {
						"circle-radius": 6,
						"circle-color": "#28a745",
						"circle-stroke-color": "white",
						"circle-stroke-width": 2,
					},
				});
			} catch (e: unknown) { console.warn('[maplibre-track] addStartMarkerLayer failed:', e); }
		}
		if (!map.getLayer("track-end")) {
			try {
				map.addLayer({
					id: "track-end",
					type: "circle",
					source: "track-end-src",
					paint: {
						"circle-radius": 6,
						"circle-color": "#dc3545",
						"circle-stroke-color": "white",
						"circle-stroke-width": 2,
					},
				});
			} catch (e: unknown) { console.warn('[maplibre-track] addEndMarkerLayer failed:', e); }
		}

		if (!skipFitBounds) {
			let minLat = Infinity,
				maxLat = -Infinity;
			let minLng = Infinity,
				maxLng = -Infinity;
			for (const [lng, lat] of coords) {
				if (lng < minLng) minLng = lng;
				if (lng > maxLng) maxLng = lng;
				if (lat < minLat) minLat = lat;
				if (lat > maxLat) maxLat = lat;
			}
			if (_trackBoundsMap) {
				_trackBoundsMap.set(map, [
					[minLng, minLat],
					[maxLng, maxLat],
				]);
			}
			try {
				map.once("idle", () => {
					try {
						const bounds = _trackBoundsMap?.get(map);
						if (bounds) map.fitBounds(bounds, { padding: 40 });
					} catch (e: unknown) { console.warn('[maplibre-track] fitBounds failed:', e); }
				});
			} catch (e: unknown) { console.warn('[maplibre-track] registerIdleForFitBounds failed:', e); }
		}
	},

	updateTrackColors(colorMode: string, gpsArr?: GpsPoint[]): void {
		try {
			if (!_map) return;
			const map = _map;
			if (!gpsArr || !Array.isArray(gpsArr) || gpsArr.length === 0) return;

			// Only replace the track-line layer and its source — start/end markers
			// are static (first/last GPS point) and never need to change on a
			// color-mode switch.
			H.safeRemoveLayer(map, "track-line");
			H.safeRemoveSource(map, "track");

			// Ensure start/end markers exist
			const coords = api.getCachedCoords(gpsArr);
			if (coords.length === 0) return;

			if (!map.getSource("track-start-src")) {
				const startGeo = {
					type: "FeatureCollection",
					features: [
						{
							type: "Feature",
							properties: { role: "start" },
							geometry: { type: "Point", coordinates: coords[0] },
						},
					],
				};
				try {
					map.addSource("track-start-src", {
						type: "geojson",
						data: startGeo as GeoJSON.FeatureCollection,
					});
				} catch (e: unknown) { console.warn('[maplibre-track] addStartSource failed:', e); }
				try {
					map.addLayer({
						id: "track-start",
						type: "circle",
						source: "track-start-src",
						paint: {
							"circle-radius": 6,
							"circle-color": "#28a745",
							"circle-stroke-color": "white",
							"circle-stroke-width": 2,
						},
					});
				} catch (e: unknown) { console.warn('[maplibre-track] addStartLayer failed:', e); }
			}
			if (!map.getSource("track-end-src")) {
				const endGeo = {
					type: "FeatureCollection",
					features: [
						{
							type: "Feature",
							properties: { role: "end" },
							geometry: {
								type: "Point",
								coordinates: coords[coords.length - 1],
							},
						},
					],
				};
				try {
					map.addSource("track-end-src", { type: "geojson", data: endGeo as GeoJSON.FeatureCollection });
				} catch (e: unknown) { console.warn('[maplibre-track] addEndSource failed:', e); }
				try {
					map.addLayer({
						id: "track-end",
						type: "circle",
						source: "track-end-src",
						paint: {
							"circle-radius": 6,
							"circle-color": "#dc3545",
							"circle-stroke-color": "white",
							"circle-stroke-width": 2,
						},
					});
				} catch (e: unknown) { console.warn('[maplibre-track] addEndLayer failed:', e); }
			}

			if (colorMode === "none") {
				if (!_colorGeoCache["none"]) {
					_colorGeoCache["none"] = {
						type: "Feature",
						properties: {},
						geometry: { type: "LineString", coordinates: coords },
					};
				}
				try {
					map.addSource("track", {
						type: "geojson",
						data: _colorGeoCache["none"],
					});
				} catch (e: unknown) { console.warn('[maplibre-track] addTrackSource failed:', e); }
				try {
					map.addLayer({
						id: "track-line",
						type: "line",
						source: "track",
						layout: { "line-join": "round", "line-cap": "round" },
						paint: {
							"line-color": "#dc3545",
							"line-width": 3,
							"line-opacity": 0.9,
						},
					});
				} catch (e: unknown) { console.warn('[maplibre-track] addTrackLineLayer failed:', e); }
				return;
			}

			if (_colorGeoCache[colorMode]) {
				try {
					map.addSource("track", {
						type: "geojson",
						data: _colorGeoCache[colorMode],
						tolerance: 0,
					});
				} catch (e: unknown) { console.warn('[maplibre-track] addCachedColorSource failed:', e); }
				try {
					map.addLayer({
						id: "track-line",
						type: "line",
						source: "track",
						layout: { "line-join": "round", "line-cap": "round" },
						paint: {
							"line-color": ["get", "color"],
							"line-width": 3,
							"line-opacity": 0.9,
						},
						minzoom: 0,
						maxzoom: 24,
					});
				} catch (e: unknown) { console.warn('[maplibre-track] addCachedColorLayer failed:', e); }
				return;
			}

			// values must stay 1:1 with `coords`, which coordsFromGps NaN-filters — building them from
			// the RAW array meant one invalid point shifted every later segment's colour by one. Apply
			// the same keep-predicate before deriving values.
			const alignedGps = gpsArr.filter((p) => {
				const lat = typeof p.lat === "number" ? p.lat : p.latitude;
				const lng = typeof p.lng === "number" ? p.lng : p.longitude;
				return !isNaN(lng as number) && !isNaN(lat as number);
			});

			let values: number[];
			let colorScheme: string;
			switch (colorMode) {
				case "speed": {
					// Map speed through the fixed time-weighted CDF (see speed-scale.ts)
					// so colour resolution concentrates where the ride spends its time,
					// rather than stretching linearly across a rare top speed.
					const scale = buildGpsSpeedScale(alignedGps);
					values = scale.isEmpty
						? alignedGps.map((p) => p.speed || 0)
						: alignedGps.map((p) => scale.fill(p.speed || 0));
					colorScheme = "speed";
					break;
				}
				case "heartrate":
					values = alignedGps.map((p) => p.heartRate || 0);
					colorScheme = "heartrate";
					break;
				case "elevation":
					values = alignedGps.map((p) => p.altitude || 0);
					colorScheme = "elevation";
					break;
				case "cadence":
					values = alignedGps.map((p) => p.cadence || 0);
					colorScheme = "cadence";
					break;
				default:
					return;
			}

			const validValues = values.filter((v) => v > 0);
			if (validValues.length === 0) {
				if (!_colorGeoCache["none"]) {
					_colorGeoCache["none"] = {
						type: "Feature",
						properties: {},
						geometry: { type: "LineString", coordinates: coords },
					};
				}
				try {
					map.addSource("track", {
						type: "geojson",
						data: _colorGeoCache["none"],
					});
				} catch (e: unknown) { console.warn('[maplibre-track] addFallbackTrackSource failed:', e); }
				try {
					map.addLayer({
						id: "track-line",
						type: "line",
						source: "track",
						layout: { "line-join": "round", "line-cap": "round" },
						paint: {
							"line-color": "#dc3545",
							"line-width": 3,
							"line-opacity": 0.9,
						},
					});
				} catch (e: unknown) { console.warn('[maplibre-track] addFallbackTrackLayer failed:', e); }
				return;
			}

			let min = must(validValues[0], 'non-empty validValues (guarded above)'),
				max = min;
			for (const v of validValues) {
				if (v < min) min = v;
				if (v > max) max = v;
			}

			const features: GeoJSON.Feature[] = [];
			for (let i = 0; i < coords.length - 1; i++) {
				const value = values[i] || min;
				features.push({
					type: "Feature",
					properties: {
						color: getColorForValue(value, min, max, colorScheme),
						value: value,
					},
					geometry: {
						type: "LineString",
						coordinates: [must(coords[i], 'i < length-1 (loop-bounded)'), must(coords[i + 1], 'i+1 <= length-1 (loop-bounded)')],
					},
				});
			}

			const fc: GeoJSON.FeatureCollection = { type: "FeatureCollection", features: features };
			_colorGeoCache[colorMode] = fc;

			try {
				map.addSource("track", { type: "geojson", data: fc as GeoJSON.FeatureCollection, tolerance: 0 });
			} catch (e: unknown) { console.warn('[maplibre-track] addColorGradientSource failed:', e); }
			try {
				map.addLayer({
					id: "track-line",
					type: "line",
					source: "track",
					layout: { "line-join": "round", "line-cap": "round" },
					paint: {
						"line-color": ["get", "color"],
						"line-width": 3,
						"line-opacity": 0.9,
					},
					minzoom: 0,
					maxzoom: 24,
				});
			} catch (e: unknown) { console.warn('[maplibre-track] addColorGradientLayer failed:', e); }
		} catch (e) {
			console.warn("updateMaplibreTrackColors error", e);
		}
	},

	destroy(): void {
		_map = null;
		_trackBoundsMap = null;
		_cachedCoords = null;
		_coordsSource = null;
		for (const key in _colorGeoCache) {
			delete _colorGeoCache[key];
		}
	},
};
