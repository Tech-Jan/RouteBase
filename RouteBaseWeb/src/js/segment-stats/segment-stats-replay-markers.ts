/**
 * Segment Statistics – Replay Markers (MapLibre)
 *
 * Owns marker creation, positioning, highlighting, and teardown for the replay
 * engine. Markers are GeoJSON circle layers (halo/border/fill) updated via a
 * single setData per frame.
 *
 * Track/polyline drawing lives in segment-stats-replay-tracks.ts.
 *
 * Exported as `replayMarkersApi`. The core replay engine (segment-stats-replay.ts)
 * calls these functions; the perf logger reads diagnostic state via the snapshot callback.
 *
 * Depends on: ctx (shared context from segment-statistics.ts)
 *             MapLibre (maplibregl)
 */
import type { ReplayEffort, SegmentStatsReplayMarkersApi, MarkerDiagnosticSnapshot } from './segment-stats-replay-types';
import { MARKER_HALO_LAYER_ID } from './segment-stats-replay-types';
import { must } from '../core/invariant';
import { ctx } from './segment-statistics';
import { safeCleanupLayers } from '../core/map-utils-shared';
if (!ctx) { throw new Error("segment-stats-replay-markers: no ctx"); }

	// ====================================================================
	// STATE
	// ====================================================================

	let map: maplibregl.Map | null = null;
	let efforts: ReplayEffort[] = [];

	// GeoJSON-backed markers
	let _markerGeoSource: string | null = null;
	interface MarkerFeature {
		type: "Feature";
		properties: { _idx: number; color: string; finished: boolean; highlighted: boolean };
		geometry: { type: "Point"; coordinates: [number, number] };
	}
	let _markerFeatures: MarkerFeature[] = [];
	let _highlightedSet: Record<string, boolean> = {};
	let _markerFeatureCollection: { type: "FeatureCollection"; features: MarkerFeature[] } | null = null;
	let _markerDirty = false;

	// ====================================================================
	// PUBLIC API
	// ====================================================================

	const api = {} as SegmentStatsReplayMarkersApi;
	export const replayMarkersApi = api;

	/**
	 * Bind this module to a map instance for the current replay session.
	 * Called by the core engine during init.
	 */
	api.bind = (mapInstance: maplibregl.Map, effortData: ReplayEffort[]): void => {
		map = mapInstance;
		efforts = effortData;
		_markerGeoSource = null;
		_markerFeatures = [];
		_highlightedSet = {};
		_markerFeatureCollection = null;
		_markerDirty = false;
	};

	/**
	 * Create all effort markers. Called after track layers have been drawn.
	 */
	api.create = (): void => {
		_createMapLibreGeoJsonMarkers();
	};

	/**
	 * Point the marker module at a new effort array WITHOUT tearing down the
	 * existing GeoJSON source/layers. Used when full-res chunks stream in (PS-03):
	 * the effort SET (count/order/colour) is unchanged, so the marker features stay
	 * valid and only the effort points (for interpolation) change. Avoids the
	 * remove/re-add flash of a full destroy()+create().
	 */
	api.rebind = (effortData: ReplayEffort[]): void => {
		efforts = effortData;
	};

	api.setMarkerPosition = (idx: number, lat: number, lng: number): void => {
		if (!_markerGeoSource) return;
		const f = _markerFeatures[idx];
		if (f) {
			f.geometry.coordinates[0] = lng;
			f.geometry.coordinates[1] = lat;
			_markerDirty = true;
		}
	};

	api.setMarkerFinished = (idx: number, finished: boolean): void => {
		if (!_markerGeoSource) return;
		const f = _markerFeatures[idx];
		if (f) {
			f.properties.finished = finished;
			_markerDirty = true;
		}
	};

	api.setMarkerHighlight = (idx: number, on: boolean): void => {
		if (idx < 0 || idx >= efforts.length || !_markerGeoSource) return;
		const f = _markerFeatures[idx];
		if (f) {
			f.properties.highlighted = on;
		}
		if (on) {
			_highlightedSet[idx] = true;
		} else {
			delete _highlightedSet[idx];
		}
		_markerDirty = true;
		_flushMarkerGeoSource();
	};

	api.flushMarkerGeoSource = (): void => {
		_flushMarkerGeoSource();
	};

	/**
	 * Reset all GeoJSON marker features to starting positions.
	 * Called by the core engine on start().
	 */
	api.resetMarkerFeatures = (highlightedEfforts: Record<string, boolean>): void => {
		_highlightedSet = {};
		for (let j = 0; j < _markerFeatures.length; j++) {
			const f = must(_markerFeatures[j], 'j < length (loop-bounded)');
			const p0 = must(must(efforts[j], 'markerFeatures lockstep with efforts').points[0], 'points.length >= 2 (build invariant)');
			f.properties.finished = false;
			f.properties.highlighted = !!highlightedEfforts[j];
			f.geometry.coordinates[0] = p0.lng ?? 0;
			f.geometry.coordinates[1] = p0.lat ?? 0;
		}
		for (const k in highlightedEfforts) {
			if (highlightedEfforts[k]) _highlightedSet[k] = true;
		}
		_markerDirty = true;
	};

	/**
	 * Remove all markers. Called by the core engine on destroy().
	 */
	api.destroyAll = (): void => {
		_removeMarkers();
		map = null;
		efforts = [];
	};

	/**
	 * Return a diagnostic snapshot of marker state for the perf logger.
	 */
	api.getDiagnosticSnapshot = (): MarkerDiagnosticSnapshot => {
		const snap: MarkerDiagnosticSnapshot = {
			markerFeatureCount: _markerFeatures ? _markerFeatures.length : 0,
			markerDirty: _markerDirty,
			mlMarkerSources: 0,
			mlMarkerLayers: 0,
		};

		if (map) {
			try {
				if (map.getSource("replay-markers")) snap.mlMarkerSources++;
				const layerIds = [
					MARKER_HALO_LAYER_ID,
					"replay-markers-border",
					"replay-markers-fill",
				];
				for (let li = 0; li < layerIds.length; li++) {
					if (map.getLayer(must(layerIds[li], 'li < length (loop-bounded)'))) snap.mlMarkerLayers++;
				}
			} catch (_e) { /* defensive: map state may not be queryable */ }
		}

		return snap;
	};

	// ====================================================================
	// INTERNAL – MARKER CREATION
	// ====================================================================

	function _createMapLibreGeoJsonMarkers(): void {
		if (!map) return;
		_markerFeatures = [];
		_highlightedSet = {};
		for (let i = 0; i < efforts.length; i++) {
			const effort = must(efforts[i], 'i < length (loop-bounded)');
			const firstPt = must(effort.points[0], 'points.length >= 2 (build invariant)');
			_markerFeatures.push({
				type: "Feature",
				properties: {
					_idx: i,
					color: effort.color,
					finished: false,
					highlighted: false,
				},
				geometry: {
					type: "Point",
					coordinates: [firstPt.lng ?? 0, firstPt.lat ?? 0],
				},
			});
		}

		_markerFeatureCollection = {
			type: "FeatureCollection",
			features: _markerFeatures,
		};
		_markerDirty = true;

		const fc = { type: "FeatureCollection" as const, features: _markerFeatures };
		try {
			map.addSource("replay-markers", { type: "geojson", data: fc });

			map.addLayer({
				id: MARKER_HALO_LAYER_ID,
				type: "circle",
				source: "replay-markers",
				paint: {
					"circle-radius": [
						"case",
						["all", ["get", "highlighted"], ["!", ["get", "finished"]]],
						28,
						0,
					],
					"circle-color": "#ffffff",
					"circle-blur": 0.6,
					"circle-opacity": [
						"case",
						["all", ["get", "highlighted"], ["!", ["get", "finished"]]],
						0.55,
						0,
					],
				},
			});

			map.addLayer({
				id: "replay-markers-border",
				type: "circle",
				source: "replay-markers",
				paint: {
					"circle-radius": ["case", ["get", "highlighted"], 19, 14],
					"circle-color": ["case", ["get", "highlighted"], "#ffe066", "#ffffff"],
					"circle-opacity": [
						"case",
						["all", ["get", "highlighted"], ["!", ["get", "finished"]]],
						1.0,
						["get", "finished"],
						0.3,
						0.75,
					],
				},
			});

			map.addLayer({
				id: "replay-markers-fill",
				type: "circle",
				source: "replay-markers",
				paint: {
					"circle-radius": ["case", ["get", "highlighted"], 13, 10],
					"circle-color": ["get", "color"],
					"circle-opacity": [
						"case",
						["all", ["get", "highlighted"], ["!", ["get", "finished"]]],
						1.0,
						["get", "finished"],
						0.35,
						0.75,
					],
					"circle-stroke-width": ["case", ["get", "highlighted"], 2, 0],
					"circle-stroke-color": "#ffffff",
				},
			});

			_markerGeoSource = "replay-markers";
		} catch (e) {
			console.warn("GeoJSON marker layer failed:", e);
			_markerGeoSource = null;
		}
	}

	// ====================================================================
	// INTERNAL – FLUSH & CLEANUP
	// ====================================================================

	function _flushMarkerGeoSource(): void {
		if (!_markerGeoSource || !map || !_markerDirty) return;
		try {
			const src = map.getSource(_markerGeoSource);
			if (src) {
				(src as maplibregl.GeoJSONSource).setData(_markerFeatureCollection as GeoJSON.GeoJSON);
				_markerDirty = false;
			}
		} catch (_e) { /* defensive: source may not be ready */ }
	}

	function _removeMarkers(): void {
		if (_markerGeoSource && map) {
			safeCleanupLayers(
				map,
				[MARKER_HALO_LAYER_ID, "replay-markers-border", "replay-markers-fill"],
				["replay-markers"],
			);
			_markerGeoSource = null;
			_markerFeatures = [];
			_markerFeatureCollection = null;
			_markerDirty = false;
			_highlightedSet = {};
		}
	}
