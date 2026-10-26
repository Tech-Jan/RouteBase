/**
 * activities-overview-maplibre.ts
 *
 * MapLibre GL engine for the Activities overview map.
 * Manages MapLibre instance lifecycle, multi-track GeoJSON rendering,
 * in-place style switching, and interaction handlers.
 *
 * Exports:
 *   init(tracksData, styleUrl, savedCam, colorPalette) → map instance
 *   destroy()
 *   refresh(tracksData)
 *   switchStyle(styleUrl, cachedTracksData)
 *   getMap()
 *   getStyleUrl()
 *   attachViewListeners(handler)
 *
 */
import type { OverviewTrack } from './overview-types';
import type { MapLibreHelpersNamespace } from '../maplibre/maplibre-types';
import { isUsableLatLng } from './activities-gps-loader';
import { safeCleanupLayers } from '../core/map-utils-shared';
import { escapeHtml } from '../core/html';
import { helpers as H } from '../maplibre/maplibre-helpers';
import { installMissingImageFallback } from '../maplibre/missing-image-fallback';
import { installStyleFilterSanitizer } from '../maplibre/style-filter-sanitizer';
import { demProviderFor } from '../core/basemap-registry';

let activitiesMapLibre: maplibregl.Map | null = null;
let _activeStyleUrl: string | null = null;
let _mapLibreGeneration = 0;
// Generation token for in-place style switches. Two rapid basemap clicks each register a
// once('style.load'); without this only the latest switch's continuations run — the rest no-op
// so tracks are added exactly once and the final style is the last requested (MC-05).
let _styleSwitchSeq = 0;
type LayerEventHandler = (ev: maplibregl.MapLayerMouseEvent & Object) => void;
let _mlEventHandlers: { type: keyof maplibregl.MapLayerEventType; layer: string; fn: LayerEventHandler }[] = [];

/** Type-safe layer-event removal — internalises the correlated-union cast that TS cannot prove. */
function _removeLayerHandler(
	map: maplibregl.Map,
	h: { type: keyof maplibregl.MapLayerEventType; layer: string; fn: LayerEventHandler },
): void {
	// Safe: we only store mouseenter/mouseleave/click, all mapping to MapLayerMouseEvent.
	(map.off as (type: string, layer: string, fn: LayerEventHandler) => void)(h.type, h.layer, h.fn);
}
// Tracks handed to refresh() before the style finished loading — MapLibre rejects addSource
// pre-style.load. Stashed here and painted by the map `load` handler (MC-02) or the idle
// flush (_armPendingFlush). Cleared at the single choke point in _addTrackSourceAndLayer so
// any successful add supersedes a deferral.
let _pendingTracks: OverviewTrack[] | null = null;
// Style readiness tracked explicitly off the load / style.load events: map.isStyleLoaded()
// also reports false while tiles/glyphs/DEM are merely in flight (the post-load hillshade add
// reopens that window), which stranded refresh() stashes after the one-shot `load` handler had
// already run — tracks then stayed invisible until a manual style switch re-added them.
let _styleReady = false;
let _pendingFlushArmed = false;
let _colorPalette: string[] = [];
let _viewZoomEndListener: maplibregl.Listener = () => {};
let _viewMoveEndListener: maplibregl.Listener = () => {};

const TRACKS_LAYER_ID = "act-tracks-line";

// Shape-filter highlight: while painting, matching tracks stay bright and the rest
// fade back. null = no highlight (every track at its normal opacity).
let _highlightIds: number[] | null = null;
const BASE_OPACITY = 0.7;
const HIGHLIGHT_MATCH_OPACITY = 0.95;
const HIGHLIGHT_DIM_OPACITY = 0.22;

// A constant opacity or a MapLibre data-driven expression array (this types' maplibregl
// build doesn't export ExpressionSpecification, and setPaintProperty takes `any` anyway).
type LineOpacityValue = number | unknown[];

/**
 * Per-feature line-opacity. With a highlight active, matching ids stay bright and the
 * rest fade; otherwise everything sits at the base opacity. When a track is hovered
 * (hoverIndex given) it goes fully opaque regardless.
 */
function _trackOpacityExpr(hoverIndex?: number): LineOpacityValue {
	const base: LineOpacityValue = _highlightIds
		? ["case", ["in", ["get", "id"], ["literal", _highlightIds]], HIGHLIGHT_MATCH_OPACITY, HIGHLIGHT_DIM_OPACITY]
		: BASE_OPACITY;
	if (hoverIndex === undefined) return base;
	return ["case", ["==", ["get", "_index"], hoverIndex], 1, base];
}

// ================================================================
// TRACK FEATURE BUILDING
// ================================================================

function _buildTrackFeatures(tracksData: OverviewTrack[]): GeoJSON.Feature[] {
	const features: GeoJSON.Feature[] = [];
	tracksData.forEach((track: OverviewTrack, index: number) => {
		const color = _colorPalette[index % _colorPalette.length];
		const coords = track.points
			.filter((p) => isUsableLatLng(p))
			.map((p) => [p[1], p[0]]); // [lat,lng] wire pair → GeoJSON [lng,lat]
		if (coords.length === 0) return;
		features.push({
			type: "Feature",
			properties: {
				name: track.name,
				date: track.date,
				type: track.type,
				distance: track.distance,
				ascent: track.ascent,
				id: track.id,
				color: color,
				_index: index,
			},
			geometry: { type: "LineString", coordinates: coords },
		});
	});
	return features;
}

function _addTrackSourceAndLayer(map: maplibregl.Map, fc: GeoJSON.FeatureCollection): void {
	_pendingTracks = null; // a successful add supersedes any deferral (incl. the _switchStyle re-add)
	const layerId = TRACKS_LAYER_ID;

	// Idempotent guard: makes a double addSource ("source already exists") structurally impossible
	// if two style.load callbacks ever reach here on the same event (MC-05).
	safeCleanupLayers(map, [TRACKS_LAYER_ID], ["act-tracks"]);

	map.addSource("act-tracks", {
		type: "geojson",
		data: fc,
		tolerance: 0.5,
	});

	map.addLayer({
		id: layerId,
		type: "line",
		source: "act-tracks",
		layout: { "line-join": "round", "line-cap": "round" },
		paint: {
			"line-color": ["get", "color"],
			"line-width": 3,
		},
	});
	// Set opacity through the loosely-typed setter so it accepts the highlight expression.
	map.setPaintProperty(layerId, "line-opacity", _trackOpacityExpr());

	const enterFn = (e: maplibregl.MapLayerMouseEvent) => {
		map.getCanvas().style.cursor = "pointer";
		const hovered = e.features?.[0];
		if (hovered) {
			map.setPaintProperty(layerId, "line-width", [
				"case",
				["==", ["get", "_index"], hovered.properties._index],
				5,
				3,
			]);
			map.setPaintProperty(layerId, "line-opacity", _trackOpacityExpr(hovered.properties._index));
		}
	};
	const leaveFn = () => {
		map.getCanvas().style.cursor = "";
		map.setPaintProperty(layerId, "line-width", 3);
		map.setPaintProperty(layerId, "line-opacity", _trackOpacityExpr());
	};
	const clickFn = (e: maplibregl.MapLayerMouseEvent) => {
		const clicked = e.features?.[0];
		if (!clicked) return;
		const props = clicked.properties;
		new maplibregl.Popup({ className: "cockpit-popup" })
			.setLngLat(e.lngLat)
			.setHTML(
				'<div style="min-width:200px;"><strong>' +
					escapeHtml(String(props.name ?? '')) +
					"</strong><br/>" +
					"<small>" +
					escapeHtml(String(props.date ?? '')) +
					"</small><br/>" +
					'<span class="chip">' +
					escapeHtml(String(props.type ?? '')) +
					"</span><br/>" +
					"Distance: " +
					Number(props.distance).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) +
					" km<br/>Ascent: " +
					Math.round(Number(props.ascent)).toLocaleString() +
					" m<br/>" +
					'<a href="/Activities/Details/' +
					Number(props.id) +
					'" class="btn-cockpit btn-cockpit-primary btn-cockpit-sm" style="margin-top:6px;display:inline-flex;">View Details</a></div>',
			)
			.addTo(map);
	};

	map.on("mouseenter", layerId, enterFn);
	map.on("mouseleave", layerId, leaveFn);
	map.on("click", layerId, clickFn);

	_mlEventHandlers.push({ type: "mouseenter", layer: layerId, fn: enterFn });
	_mlEventHandlers.push({ type: "mouseleave", layer: layerId, fn: leaveFn });
	_mlEventHandlers.push({ type: "click", layer: layerId, fn: clickFn });
}

// ================================================================
// REFRESH / ADD TRACKS
// ================================================================

function refreshMapLibreTracks(map: maplibregl.Map, tracksData: OverviewTrack[]): void {
	const src = map.getSource("act-tracks") as maplibregl.GeoJSONSource | undefined;
	// Pre-style.load addSource throws — stash until the style is ready (`load` handler or idle
	// flush paints). Deliberately NOT map.isStyleLoaded(): see _styleReady.
	if (!src && !_styleReady) {
		_pendingTracks = tracksData;
		_armPendingFlush(map);
		return;
	}

	const features = _buildTrackFeatures(tracksData);
	const fc = { type: "FeatureCollection" as const, features };
	if (src) {
		// Always push the new data — including an empty set, so filtering to zero clears the map.
		src.setData(fc as GeoJSON.FeatureCollection);
		return;
	}
	if (features.length === 0) return; // nothing to show and no source to clear yet
	_addTrackSourceAndLayer(map, fc);
}

function addMapLibreTracks(map: maplibregl.Map, tracksData: OverviewTrack[]): void {
	const features = _buildTrackFeatures(tracksData);
	if (features.length === 0) return;
	const fc = { type: "FeatureCollection" as const, features: features };
	_addTrackSourceAndLayer(map, fc);
}

// Self-heal for a deferral stashed after the one-shot `load` handler already ran: idle fires
// after every settle (triggerRepaint guarantees one even on an already-idle map), so a stash
// can never strand. Generation-guarded; re-arms itself while the style is still not ready.
function _armPendingFlush(map: maplibregl.Map): void {
	if (_pendingFlushArmed) return;
	_pendingFlushArmed = true;
	const myGen = _mapLibreGeneration;
	map.once("idle", () => {
		_pendingFlushArmed = false;
		if (myGen !== _mapLibreGeneration) return; // map replaced/destroyed meanwhile
		if (_pendingTracks) refreshMapLibreTracks(map, _pendingTracks);
	});
	try { map.triggerRepaint(); } catch (_e) { /* teardown race — the guard above no-ops */ }
}

/**
 * Load-complete invariant (page calls this once the GPS loader finishes): data on hand must mean
 * a painted source. A stranded deferral repaints itself here; if even that fails, the map shows
 * an explicit banner instead of staying silently empty (the old failure mode of this bug class).
 */
export function assertTracksPainted(): void {
	const map = activitiesMapLibre;
	if (!map) return;
	const myGen = _mapLibreGeneration;
	map.once("idle", () => {
		if (myGen !== _mapLibreGeneration || !activitiesMapLibre) return;
		if (!_pendingTracks) return; // painted, or nothing deferred
		if (_styleReady) {
			console.error("[overview-maplibre] tracks stranded after load-complete — repainting");
			refreshMapLibreTracks(activitiesMapLibre, _pendingTracks);
		}
		// Still stashed with a ready style = repaint failed too — surface it. (Not-ready style is
		// legitimate here: the `load` handler still owns the paint and no basemap is visible anyway.)
		if (_pendingTracks && _styleReady) _showMapErrorBanner("Tracks failed to render — please reload the page.");
	});
	try { map.triggerRepaint(); } catch (_e) { /* teardown race */ }
}

function _showMapErrorBanner(msg: string): void {
	const host = document.getElementById("activitiesMap");
	if (!host || host.querySelector(".map-error-banner")) return;
	const b = document.createElement("div");
	b.className = "map-error-banner";
	b.textContent = msg;
	b.style.cssText =
		"position:absolute;top:8px;left:50%;transform:translateX(-50%);z-index:10;" +
		"background:#b3261e;color:#fff;padding:6px 12px;border-radius:6px;font-size:13px;";
	host.appendChild(b);
}

// ================================================================
// SHAPE-FILTER HIGHLIGHT
// ================================================================

/**
 * Dim every track except the given matching ids. Pass null to clear the highlight.
 * Updates only the layer's line-opacity paint expression — no source/data reload —
 * so it stays cheap even with a few hundred tracks on screen.
 */
export function setTrackHighlight(ids: number[] | null): void {
	// [] is a real value (highlight active, nothing matches): the "in" expr is false for every
	// feature → all tracks dim. Only null clears the highlight back to base opacity.
	_highlightIds = ids ? ids : null;
	if (activitiesMapLibre && activitiesMapLibre.getLayer(TRACKS_LAYER_ID)) {
		activitiesMapLibre.setPaintProperty(TRACKS_LAYER_ID, "line-opacity", _trackOpacityExpr());
	}
}

// ================================================================
// DESTROY
// ================================================================

export function destroy(): void {
	_mapLibreGeneration++;
	_styleSwitchSeq++; // a pending fetch/style.load after teardown must no-op (map is gone)
	_pendingTracks = null;
	_styleReady = false;
	_pendingFlushArmed = false;
	_highlightIds = null;
	_mlEventHandlers.forEach((h) => {
		try {
			if (activitiesMapLibre) _removeLayerHandler(activitiesMapLibre, h);
		} catch (_e) { /* teardown: may already be removed */ }
	});
	_mlEventHandlers = [];
	if (activitiesMapLibre) {
		try {
			activitiesMapLibre.off("zoomend", _viewZoomEndListener);
			activitiesMapLibre.off("moveend", _viewMoveEndListener);
		} catch (_e) { /* teardown: may already be removed */ }
		try {
			if (activitiesMapLibre.getLayer("act-tracks-line"))
				activitiesMapLibre.removeLayer("act-tracks-line");
			if (activitiesMapLibre.getSource("act-tracks"))
				activitiesMapLibre.removeSource("act-tracks");
		} catch (_e) { /* teardown: may already be removed */ }
		try {
			activitiesMapLibre.remove();
		} catch (_e) { /* teardown: may already be removed */ }
		activitiesMapLibre = null;
	}
	_activeStyleUrl = null;
}

// ================================================================
// IN-PLACE STYLE SWITCH
// ================================================================

function _switchStyle(styleUrl: string, cachedTracksData: OverviewTrack[], basemapId?: string | null): void {
	if (!activitiesMapLibre) return;
	const map = activitiesMapLibre;
	const mySwitch = ++_styleSwitchSeq;
	_styleReady = false; // the incoming style must fire style.load before any addSource

	const savedCam = {
		center: map.getCenter().toArray(),
		zoom: map.getZoom(),
		pitch: map.getPitch(),
		bearing: map.getBearing(),
	};

	_mlEventHandlers.forEach((h) => {
		try {
			_removeLayerHandler(map, h);
		} catch (_e) { /* teardown: may already be removed */ }
	});
	_mlEventHandlers = [];
	safeCleanupLayers(map, ["act-tracks-line"], ["act-tracks"]);

	function afterStyleLoaded() {
		if (mySwitch !== _styleSwitchSeq) return; // superseded by a newer switch (or teardown)
		_styleReady = true;
		if (savedCam) {
			try {
				map.jumpTo({
					center: savedCam.center,
					zoom: savedCam.zoom,
					pitch: savedCam.pitch,
					bearing: savedCam.bearing,
				});
			} catch (e: unknown) { console.warn('[overview-maplibre] jumpTo failed:', e); }
		}
		if (styleUrl !== "terrain") {
			// Route DEM by basemap id — keyless styles get terrain + 3D toggle via terrarium (BT-06.3).
			try {
				H.addHillshadeAndLight(map, 1.0, true, demProviderFor(basemapId ?? ""));
			} catch (e: unknown) { console.warn('[overview-maplibre] addHillshadeAndLight failed:', e); }
		} else if (styleUrl === "terrain") {
			try {
				H.applySky(map);
			} catch (e: unknown) { console.warn('[overview-maplibre] applySky failed:', e); }
			try {
				map.setLight({
					anchor: "viewport",
					position: [1.15, 15.0, 65.0],
					color: "white",
					intensity: 0.6,
				});
			} catch (e: unknown) { console.warn('[overview-maplibre] setLight failed:', e); }
		}
		if (cachedTracksData) addMapLibreTracks(map, cachedTracksData);
	}

	_activeStyleUrl = styleUrl;
	if (styleUrl === "terrain") {
		const terrainStyle = H.buildInlineTerrainStyle(1.0);
		if (!terrainStyle) {
			console.error("_switchStyle: cannot build terrain style");
			return;
		}
		try {
			map.setTerrain(null);
		} catch (_e) { /* teardown: may already be removed */ }
		map.setStyle(terrainStyle, { diff: false });
		map.once("style.load", afterStyleLoaded);
	} else {
		try {
			map.setTerrain(null);
		} catch (_e) { /* teardown: may already be removed */ }
		H.fetchStyleJson(styleUrl)
			.then((json: Parameters<maplibregl.Map["setStyle"]>[0]) => {
				if (mySwitch !== _styleSwitchSeq) return; // a newer switch already owns the map
				map.setStyle(json, { diff: false });
				map.once("style.load", afterStyleLoaded);
			})
			.catch(() => {
				if (mySwitch !== _styleSwitchSeq) return; // a newer switch already owns the map
				map.setStyle(styleUrl);
				map.once("style.load", afterStyleLoaded);
			});
	}
}

// ================================================================
// INIT
// ================================================================

/** Lat/lng box used to frame the camera up front. */
interface InitBounds { minLat: number; maxLat: number; minLng: number; maxLng: number; }

export function init(
	tracksData: OverviewTrack[],
	styleUrl: string,
	savedCam: { center: number[]; zoom: number; pitch: number; bearing: number } | null,
	colorPalette: string[],
	initialBounds?: InitBounds | null,
	basemapId?: string | null,
): maplibregl.Map | null {
	_colorPalette = colorPalette || [];
	const mapContainer = document.getElementById("activitiesMap");
	if (!mapContainer || typeof maplibregl === "undefined") {
		console.error("MapLibre GL not loaded");
		return null;
	}

	// Frame from the up-front viewport (computed before GPS load) so the camera is set once and
	// never jumps as tracks stream in. tracksData may be empty here — the source fills in later.
	const center: [number, number] = savedCam
		? (savedCam.center as [number, number])
		: initialBounds
			? [(initialBounds.minLng + initialBounds.maxLng) / 2, (initialBounds.minLat + initialBounds.maxLat) / 2]
			: [0, 0];
	const initZoom = savedCam ? savedCam.zoom : initialBounds ? 10 : 2;
	const initPitch = savedCam ? savedCam.pitch : styleUrl === "terrain" ? 10 : 0;
	const initBearing = savedCam ? savedCam.bearing : 0;

	let initialStyle: maplibregl.StyleSpecification | string = styleUrl;
	if (styleUrl === "terrain") {
		const terrainStyle = H.buildInlineTerrainStyle(1.0);
		if (!terrainStyle) {
			console.error("initMapLibreMap: cannot build terrain style");
			return null;
		}
		initialStyle = terrainStyle;
	}

	const defaultOpts = H.defaultMapOptions || ({} as Partial<MapLibreHelpersNamespace["defaultMapOptions"]>);
	const map = new maplibregl.Map({
		container: mapContainer,
		style: initialStyle,
		center: center,
		zoom: initZoom,
		pitch: initPitch,
		bearing: initBearing,
		maxPitch: defaultOpts.maxPitch || 85,
		maxZoom: defaultOpts.maxZoom || 18,
		fadeDuration: defaultOpts.fadeDuration != null ? defaultOpts.fadeDuration : 0,
		refreshExpiredTiles:
			defaultOpts.refreshExpiredTiles != null ? defaultOpts.refreshExpiredTiles : false,
		renderWorldCopies:
			defaultOpts.renderWorldCopies != null ? defaultOpts.renderWorldCopies : false,
		attributionControl: defaultOpts.attributionControl || { compact: true },
	});

	if (H.applyMapBackground) H.applyMapBackground(map);
	installMissingImageFallback(map); // transparent placeholder for missing POI sprite icons (silence warnings)
	installStyleFilterSanitizer(map); // null-guard basemap numeric filters (poi rank / boundary admin_level)

	activitiesMapLibre = map;
	_activeStyleUrl = styleUrl;
	_styleReady = false; // fresh map: no addSource until its load fires

	const myGen = ++_mapLibreGeneration;
	// Nav + Terrain controls are added per-map by addHillshadeAndLight on style load
	// (BT-06.3) so the 3D toggle appears; no separate NavigationControl here.

	map.on("load", () => {
		if (myGen !== _mapLibreGeneration) {
			return;
		}
		_styleReady = true; // style object is loaded; addSource is legal from here on

		if (styleUrl !== "terrain") {
			// Route DEM by basemap id — keyless styles get terrain + 3D toggle via terrarium (BT-06.3).
			try {
				H.addHillshadeAndLight(map, 1.0, true, demProviderFor(basemapId ?? ""));
			} catch (e) {
				console.warn("terrain setup:", e);
			}
		} else if (styleUrl === "terrain") {
			try {
				H.applySky(map);
			} catch (e: unknown) { console.warn('[overview-maplibre] applySky failed:', e); }
			try {
				map.setLight({
					anchor: "viewport",
					position: [1.15, 15.0, 65.0],
					color: "white",
					intensity: 0.6,
				});
			} catch (e: unknown) { console.warn('[overview-maplibre] setLight failed:', e); }
		}

		addMapLibreTracks(map, _pendingTracks ?? tracksData);

		// Frame once to the up-front viewport. On an engine switch / reload savedCam carries the
		// camera, so skip the fit then. We never re-fit as later chunks arrive → no flicker.
		if (!savedCam && initialBounds) {
			map.fitBounds(
				[
					[initialBounds.minLng, initialBounds.minLat],
					[initialBounds.maxLng, initialBounds.maxLat],
				],
				{ padding: 50, duration: 0 },
			);
		}

	});

	return map;
}

// ================================================================
// PUBLIC EXPORTS
// ================================================================

export function refresh(tracksData: OverviewTrack[]): void {
	if (activitiesMapLibre) refreshMapLibreTracks(activitiesMapLibre, tracksData);
}

export function switchStyle(styleUrl: string, cachedTracksData: OverviewTrack[], basemapId?: string | null): void {
	if (activitiesMapLibre) _switchStyle(styleUrl, cachedTracksData, basemapId);
}

export function getMap(): maplibregl.Map | null {
	return activitiesMapLibre;
}

export function getStyleUrl(): string | null {
	return _activeStyleUrl;
}

export function attachViewListeners(handler: () => void): void {
	if (activitiesMapLibre) {
		_viewZoomEndListener = handler;
		_viewMoveEndListener = handler;
		activitiesMapLibre.on("zoomend", _viewZoomEndListener);
		activitiesMapLibre.on("moveend", _viewMoveEndListener);
	}
}
