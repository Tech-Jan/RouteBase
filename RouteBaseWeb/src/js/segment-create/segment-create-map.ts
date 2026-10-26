// segment-create-map.ts — MapLibre engine for the segment-create page.
// Owns the maplibregl.Map + 4 GeoJSON sources/layers (activity trace, drawn
// route, cursor preview, waypoints) and their style/engine re-init replay.
// No DOM outside the map container, no routing, no save.
// Exported as segCreateMap; consumed by segment-create.ts. Template: LR-01.
import type { SavedCamera } from '../core/core-types';
import { helpers as H } from '../maplibre/maplibre-helpers';
import { installMissingImageFallback } from '../maplibre/missing-image-fallback';
import { installStyleFilterSanitizer } from '../maplibre/style-filter-sanitizer';
import { safeRemoveLayer, safeRemoveSource } from '../core/map-utils-shared';
import { demProviderFor } from '../core/basemap-registry';

type LngLat = [number, number];
type LatLng = [number, number];

// ─── Grep-unique source / layer ids ──────────────────────────────────────
const WAYPOINTS_SRC = 'segcreate-waypoints-src';
const ROUTE_SRC = 'segcreate-route-src';
const ACTIVITY_SRC = 'segcreate-activity-src';
const PREVIEW_SRC = 'segcreate-preview-src';
const WAYPOINTS_LAYER = 'segcreate-waypoints-circle';
const ROUTE_LAYER = 'segcreate-route-line';
const ACTIVITY_LAYER = 'segcreate-activity-line';
const PREVIEW_LAYER = 'segcreate-preview-line';

const STRAIGHT_DASH: [number, number] = [1.7, 3.3]; // ≈ 5px / 10px at line-width 3
const ROUTED_DASH: [number, number] = [3.3, 1.7]; // ≈ 10px / 5px

// ─── Module display state (survives style/engine re-init; all [lng, lat]) ──
let _map: maplibregl.Map | null = null;
let _ready = false;
let _waypoints: LngLat[] = [];
let _route: LngLat[] = [];
let _activity: LngLat[] = [];
let _preview: { coords: LngLat[]; straight: boolean } | null = null;

// ─── Bound interaction handlers (stored so `off` uses the same ref) ────────
let _clickHandler: ((e: maplibregl.MapMouseEvent) => void) | null = null;
let _moveHandler: ((e: maplibregl.MapMouseEvent) => void) | null = null;

// ─── GeoJSON feature builders ─────────────────────────────────────────────

function _lineFeature(coords: LngLat[]): GeoJSON.Feature {
	return { type: 'Feature', geometry: { type: 'LineString', coordinates: coords }, properties: {} };
}
function _multiPointFeature(coords: LngLat[]): GeoJSON.Feature {
	return { type: 'Feature', geometry: { type: 'MultiPoint', coordinates: coords }, properties: {} };
}

/** Flip page-facing [lat, lng] tuples to MapLibre [lng, lat] — the only flip point. */
function _flip(pts: LatLng[]): LngLat[] {
	return pts.map(([lat, lng]) => [lng, lat] as LngLat);
}

function _getSource(id: string): maplibregl.GeoJSONSource | undefined {
	return _map?.getSource(id) as maplibregl.GeoJSONSource | undefined;
}

// ─── Idempotent layer construction (z-order: activity → route → preview → waypoints) ──

function _addAllLayers(): void {
	if (!_map) return;
	const map = _map;

	if (!map.getSource(ACTIVITY_SRC)) map.addSource(ACTIVITY_SRC, { type: 'geojson', data: _lineFeature(_activity) });
	if (!map.getLayer(ACTIVITY_LAYER)) {
		map.addLayer({
			id: ACTIVITY_LAYER, type: 'line', source: ACTIVITY_SRC,
			paint: { 'line-color': '#4CAF50', 'line-width': 4, 'line-opacity': 0.6 },
		});
	}

	if (!map.getSource(ROUTE_SRC)) map.addSource(ROUTE_SRC, { type: 'geojson', data: _lineFeature(_route) });
	if (!map.getLayer(ROUTE_LAYER)) {
		map.addLayer({
			id: ROUTE_LAYER, type: 'line', source: ROUTE_SRC,
			paint: { 'line-color': '#2196F3', 'line-width': 5, 'line-opacity': 0.8 },
		});
	}

	if (!map.getSource(PREVIEW_SRC)) {
		map.addSource(PREVIEW_SRC, { type: 'geojson', data: _lineFeature(_preview?.coords ?? []) });
	}
	if (!map.getLayer(PREVIEW_LAYER)) {
		map.addLayer({
			id: PREVIEW_LAYER, type: 'line', source: PREVIEW_SRC,
			paint: {
				'line-color': '#FF9800', 'line-width': 3,
				'line-opacity': _preview?.straight === false ? 0.6 : 0.5,
				'line-dasharray': _preview?.straight === false ? ROUTED_DASH : STRAIGHT_DASH,
			},
		});
	}

	if (!map.getSource(WAYPOINTS_SRC)) map.addSource(WAYPOINTS_SRC, { type: 'geojson', data: _multiPointFeature(_waypoints) });
	if (!map.getLayer(WAYPOINTS_LAYER)) {
		map.addLayer({
			id: WAYPOINTS_LAYER, type: 'circle', source: WAYPOINTS_SRC,
			paint: {
				'circle-radius': 6, 'circle-color': '#FF5722', 'circle-opacity': 1,
				'circle-stroke-width': 2, 'circle-stroke-color': '#FF5722',
			},
		});
	}
}

// ─── Public API ────────────────────────────────────────────────────────────

const api = {
	// styleUrl is always a resolved URL — this page's registry is keyless (no terrain sentinel).
	init(containerId: string, styleUrl: string, cam?: SavedCamera | null, basemapId?: string | null): void {
		const map = new maplibregl.Map({
			container: containerId,
			style: styleUrl,
			center: cam?.center ?? [7.6, 47.5], // Basel; Leaflet 8 ≡ MapLibre 7 (512-px tiles)
			zoom: cam?.zoom ?? 7,
			...H.defaultMapOptions,
		});
		_map = map;
		H.applyMapBackground(map);
		installMissingImageFallback(map);
		installStyleFilterSanitizer(map); // null-guard basemap numeric filters (poi rank / boundary admin_level)
		map.on('load', () => {
			_ready = true;
			_addAllLayers();
			// Keyless drawing map still gets the 3D toggle via the routed DEM provider (BT-06.4).
			H.addHillshadeAndLight(map, H.terrainExaggeration(), true, demProviderFor(basemapId ?? ''));
		});
	},

	setStyle(styleUrl: string, basemapId?: string | null): void {
		if (!_map) return;
		const map = _map; // narrowed rebind — the closures below outlive the guard (G3)
		const cam = {
			center: map.getCenter(),
			zoom: map.getZoom(),
			bearing: map.getBearing(),
			pitch: map.getPitch(),
		};
		const applyStyle = (style: maplibregl.StyleSpecification | string): void => {
			map.setStyle(style as maplibregl.StyleSpecification);
			map.once('style.load', () => {
				map.jumpTo(cam);
				_addAllLayers();
				H.addHillshadeAndLight(map, H.terrainExaggeration(), true, demProviderFor(basemapId ?? ''));
			});
		};
		H.fetchStyleJson(styleUrl)
			.then((style) => applyStyle(style))
			.catch(() => applyStyle(styleUrl)); // fallback: let maplibre fetch directly
	},

	setWaypoints(pts: LatLng[]): void {
		_waypoints = _flip(pts);
		if (_ready) _getSource(WAYPOINTS_SRC)?.setData(_multiPointFeature(_waypoints));
	},

	setRoute(pts: LatLng[]): void {
		_route = _flip(pts);
		if (_ready) _getSource(ROUTE_SRC)?.setData(_lineFeature(_route));
	},

	setActivity(pts: LatLng[]): void {
		_activity = _flip(pts);
		if (_ready) _getSource(ACTIVITY_SRC)?.setData(_lineFeature(_activity));
	},

	fitToActivity(): void {
		if (!_map || _activity.length === 0) return;
		const bounds = new maplibregl.LngLatBounds();
		_activity.forEach((c) => bounds.extend(c));
		_map.fitBounds(bounds, { padding: 0 });
	},

	setPreview(pts: LatLng[], straight: boolean): void {
		_preview = { coords: _flip(pts), straight };
		if (!_ready || !_map) return;
		_getSource(PREVIEW_SRC)?.setData(_lineFeature(_preview.coords));
		_map.setPaintProperty(PREVIEW_LAYER, 'line-dasharray', straight ? STRAIGHT_DASH : ROUTED_DASH);
		_map.setPaintProperty(PREVIEW_LAYER, 'line-opacity', straight ? 0.5 : 0.6);
	},

	clearPreview(): void {
		_preview = null;
		if (_ready) _getSource(PREVIEW_SRC)?.setData(_lineFeature([]));
	},

	// Clear waypoints + route + preview; activity trace kept (matches old clear-all).
	clearAll(): void {
		_waypoints = [];
		_route = [];
		_preview = null;
		if (!_ready) return;
		_getSource(WAYPOINTS_SRC)?.setData(_multiPointFeature([]));
		_getSource(ROUTE_SRC)?.setData(_lineFeature([]));
		_getSource(PREVIEW_SRC)?.setData(_lineFeature([]));
	},

	onClick(cb: (lat: number, lng: number) => void): void {
		if (!_map) return;
		if (_clickHandler) _map.off('click', _clickHandler);
		_clickHandler = (e) => cb(e.lngLat.lat, e.lngLat.lng);
		_map.on('click', _clickHandler);
	},

	offClick(): void {
		if (_map && _clickHandler) _map.off('click', _clickHandler);
		_clickHandler = null;
	},

	onMouseMove(cb: (lat: number, lng: number) => void): void {
		if (!_map) return;
		if (_moveHandler) _map.off('mousemove', _moveHandler);
		_moveHandler = (e) => cb(e.lngLat.lat, e.lngLat.lng);
		_map.on('mousemove', _moveHandler);
	},

	offMouseMove(): void {
		if (_map && _moveHandler) _map.off('mousemove', _moveHandler);
		_moveHandler = null;
	},

	setDrawingCursor(on: boolean): void {
		if (!_map) return;
		_map.getCanvas().style.cursor = on ? 'crosshair' : '';
	},

	getMap(): maplibregl.Map | null {
		return _map;
	},

	isReady(): boolean {
		return _ready;
	},

	// Display state intentionally kept — bfcache re-init replays it via _addAllLayers.
	destroy(): void {
		if (!_map) return;
		const map = _map; // narrowed rebind for the forEach closures (G3)
		[ACTIVITY_LAYER, ROUTE_LAYER, PREVIEW_LAYER, WAYPOINTS_LAYER].forEach((id) => safeRemoveLayer(map, id));
		[ACTIVITY_SRC, ROUTE_SRC, PREVIEW_SRC, WAYPOINTS_SRC].forEach((id) => safeRemoveSource(map, id));
		_clickHandler = null;
		_moveHandler = null;
		try { _map.remove(); } catch { /* ignore teardown */ }
		_map = null;
		_ready = false;
	},
};

export const segCreateMap = api;
