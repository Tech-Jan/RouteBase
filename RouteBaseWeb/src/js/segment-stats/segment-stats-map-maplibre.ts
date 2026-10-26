/**
 * Segment Statistics — MapLibre map engine
 *
 * Handles MapLibre/MapTiler layer management: polylines, markers, fitBounds,
 * and view restoration after style switches.
 *
 * Performance optimisations (matching the activities overview map):
 *   — Effort tracks are rendered as a single FeatureCollection with one
 *     source and one layer — one terrain draping pass, one GPU draw call.
 *   — Data-driven styling: each Feature carries its own color property.
 *   — Event listeners are tracked by reference and cleaned up before refresh.
 *   — Bulk fetch: all effort tracks are loaded in a single POST instead of
 *     N+1 individual GET requests.
 *   — Progressive quality via segment-stats-progressive.ts.
 *
 * Receives: ctx (shared context) as the initSegmentStatsMapLibre(ctx) parameter — every api method
 *           reads it through the parameter, not the module-level singleton import.
 * Depends on: progressiveApi (from segment-stats-progressive.ts)
 *             mapDataApi (from segment-stats-map-data.ts)
 *             _shared (injected via bindShared() by segment-stats-map.ts)
 */
import type { TrackPoint, ShapeRect } from '../core/core-types';
import type { SegmentEffort } from './segment-stats-effort-types';
import type { SegmentStatsMapShared, SegmentStatsMapDataApi, SegmentStatsMapLibreApi, SegmentStatsProgressiveApi } from './segment-stats-map-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { ctx } from './segment-statistics';
import { getMaplibreMap } from '../core/map-state';
import { fetchSingleTrack } from './segment-stats-api';
import { progressiveApi } from './segment-stats-progressive';
import { mapDataApi } from './segment-stats-map-data';
import { safeCleanupLayers } from '../core/map-utils-shared';
import { must } from '../core/invariant';
import { buildEffortFeatures } from './segment-stats-map-features';
import {
	polylineLayerSpec,
	combinedEffortLayerSpec,
	effortBaseLineWidth,
	effortBaseLineOpacity,
	effortHoverLineWidth,
	effortHoverLineOpacity,
} from './segment-stats-map-layer-specs';

if (!ctx) {
	throw new Error("segment-stats-map-maplibre: no ctx");
}

// Shared map state — injected by segment-stats-map.ts after it creates it
let _shared: SegmentStatsMapShared | null = null;

export function bindShared(s: SegmentStatsMapShared): void {
	_shared = s;
}

function shared(): SegmentStatsMapShared { return must(_shared, 'bound at init before any consumer runs'); }
function prog(): SegmentStatsProgressiveApi { return progressiveApi; }
function D(): SegmentStatsMapDataApi { return mapDataApi; }

const api = {} as SegmentStatsMapLibreApi;
export const maplibreApi = api;

/**
 * Populate the MapLibre engine api. Hoisted out of the module top level (the api-method assignments
 * ran bare on import) so every method that consults the shared context does so through the `ctx`
 * parameter instead of the module-level singleton import. Import-time semantics are unchanged: `api`
 * + `maplibreApi` stay module-level (consumers + bindShared still resolve at import), and the
 * top-level call at the bottom passes the shared ctx exactly as before.
 */
export function initSegmentStatsMapLibre(ctx: SegmentStatisticsContext): void {

	// MapLibre source/layer tracking (non-effort items like segment route, markers)
	let mlSourceIds: string[] = [];
	let mlLayerIds: string[] = [];
	let mlMarkers: maplibregl.Marker[] = [];

	// Event handler tracking for proper cleanup
	type LayerEventHandler = (ev: maplibregl.MapLayerMouseEvent & Object) => void;
	let _mlEventHandlers: { type: keyof maplibregl.MapLayerEventType; layer: string; fn: LayerEventHandler }[] = [];

	// Progressive paint: MapLibre has one combined source, so each loaded/upgraded chunk rebuilds it
	// via setData. Coalesce a burst of chunks into ~one rebuild per interval instead of one per chunk.
	const EFFORT_REFRESH_THROTTLE_MS = 100;
	let _effortRefreshTimer: ReturnType<typeof setTimeout> | null = null;
	// MH-02: bumped on every clearAll (each view switch clears before drawing). A showEfforts load
	// captures the gen at its start; every async resumption point bails if the gen has moved on, so a
	// slow chunked load can't re-add the effort layer over a view the user has since switched to.
	let _effortsGen = 0;

	/** Type-safe layer-event removal — internalises the correlated-union cast that TS cannot prove. */
	function _removeLayerHandler(
		map: maplibregl.Map,
		h: { type: keyof maplibregl.MapLayerEventType; layer: string; fn: LayerEventHandler },
	): void {
		(map.off as (type: string, layer: string, fn: LayerEventHandler) => void)(h.type, h.layer, h.fn);
	}

	// ====================================================================
	// PUBLIC API
	// ====================================================================

	// ---- Low-level helpers ------------------------------------------------

	api.clearAll = (): void => {
		// MH-02: supersede any in-flight showEfforts load + cancel its pending throttled rebuild so a
		// stale chunk/timer can't re-add the effort layer after this clear.
		++_effortsGen;
		if (_effortRefreshTimer != null) { clearTimeout(_effortRefreshTimer); _effortRefreshTimer = null; }

		const map = getMaplibreMap();
		if (!map) return;

		// Remove event handlers first
		_mlEventHandlers.forEach((h) => {
			try {
				_removeLayerHandler(map, h);
			} catch (_e) { /* teardown: may already be removed */ }
		});
		_mlEventHandlers = [];

		// Remove the combined efforts source/layer
		safeCleanupLayers(map, ["seg-efforts-line"], ["seg-efforts"]);

		// Remove individually tracked sources/layers (segment route, backgrounds)
		safeCleanupLayers(map, mlLayerIds, mlSourceIds);
		mlMarkers.forEach((m: maplibregl.Marker) => {
			try {
				m.remove();
			} catch (_e) { /* teardown: may already be removed */ }
		});

		// (Legacy per-effort "effort-N" cleanup loop removed: those ids are never created anywhere —
		// it was efforts.length + 10 no-op style queries on every view/style switch and replay toggle.)

		mlLayerIds = [];
		mlSourceIds = [];
		mlMarkers = [];
		// NOTE: do NOT reset prog() here. GPS data already fetched is still valid
		// after a view switch or style switch — it is only stale after a full
		// map teardown, which resets prog() explicitly.
	};

	api.addPolyline = (
		id: string,
		coords: number[][],
		color: string,
		width: number,
		opacity: number,
		dashArray?: number[],
		beforeId?: string,
	): void => {
		const map = getMaplibreMap();
		if (!map) {
			console.warn("[seg-stats-maplibre] addPolyline: no map instance");
			return;
		}
		const srcId = id + "-src";
		const geo = {
			type: "Feature",
			properties: {},
			geometry: {
				type: "LineString",
				coordinates: coords.map((c: number[]) => [c[1], c[0]]),
			},
		};
		try {
			map.addSource(srcId, { type: "geojson", data: geo as GeoJSON.Feature, tolerance: 2.0 });
			mlSourceIds.push(srcId);
			const layerDef = polylineLayerSpec(id, srcId, color, width, opacity, dashArray);
			if (beforeId && map.getLayer(beforeId)) {
				map.addLayer(layerDef, beforeId);
			} else {
				map.addLayer(layerDef);
			}
			mlLayerIds.push(id);
		} catch (e) {
			console.warn("[seg-stats-maplibre] mlAddPolyline error:", id, e);
		}
	};

	api.addCircleMarker = (
		id: string,
		lngLat: number[],
		color: string,
		radius: number,
		label?: string,
	): maplibregl.Marker | undefined => {
		const map = getMaplibreMap();
		if (!map) return;
		const el = document.createElement("div");
		el.style.width = radius * 2 + "px";
		el.style.height = radius * 2 + "px";
		el.style.borderRadius = "50%";
		el.style.backgroundColor = color;
		el.style.border = "2px solid white";
		el.style.boxShadow = "0 0 4px rgba(0,0,0,0.4)";
		el.style.cursor = "pointer";
		if (label) {
			el.style.display = "flex";
			el.style.alignItems = "center";
			el.style.justifyContent = "center";
			el.style.color = "white";
			el.style.fontWeight = "bold";
			el.style.fontSize = "14px";
			el.textContent = label;
			el.style.width = "30px";
			el.style.height = "30px";
		}
		const marker = new maplibregl.Marker({ element: el })
			.setLngLat(lngLat as [number, number])
			.addTo(map);
		mlMarkers.push(marker);
		return marker;
	};

	api.fitBounds = (coords: number[][]): void => {
		const map = getMaplibreMap();
		if (!map || !coords || coords.length === 0) return;
		// Use loop-based min/max to avoid call stack overflow with large GPS arrays
		let minLat = Infinity,
			maxLat = -Infinity,
			minLng = Infinity,
			maxLng = -Infinity;
		// ?? NaN: loop-bounded [lat,lng] pair reads; NaN fails every comparison and leaves the
		// bound unchanged, exactly as undefined did.
		for (const c of coords) {
			const clat = c[0] ?? NaN, clng = c[1] ?? NaN;
			if (clat < minLat) minLat = clat;
			if (clat > maxLat) maxLat = clat;
			if (clng < minLng) minLng = clng;
			if (clng > maxLng) maxLng = clng;
		}
		try {
			map.fitBounds(
				[
					[minLng, minLat],
					[maxLng, maxLat],
				],
				{ padding: 40 },
			);
		} catch (e) {
			console.warn("mlFitBounds error:", e);
		}
	};

	// ---- High-level view rendering ----------------------------------------

	api.showSegmentRoute = (fitBounds: boolean): void => {
		const segmentCoords = shared().segmentCoords;
		api.clearAll();
		api.addPolyline("seg-route", segmentCoords, "#2196F3", 5, 0.8);
		const startCoord = segmentCoords[0];
		const endCoord = segmentCoords.at(-1);
		if (startCoord && endCoord) {
			api.addCircleMarker("seg-start", [startCoord[1] ?? NaN, startCoord[0] ?? NaN], "#4CAF50", 15, "S");
			api.addCircleMarker("seg-end", [endCoord[1] ?? NaN, endCoord[0] ?? NaN], "#f44336", 15, "E");
		}
		if (fitBounds) api.fitBounds(segmentCoords);
	};

	/**
	 * Draw the painted rectangles of a shape-defined segment (additive — does not clear efforts).
	 * Role-coded by color: mandatory = blue, "any" = amber. Fill + outline share one source.
	 */
	api.showShapes = (fitBounds: boolean): void => {
		const map = getMaplibreMap();
		const shapes = shared().shapes;
		if (!map || !shapes || shapes.length === 0) return;

		const srcId = "seg-shapes-src";
		const fillId = "seg-shapes-fill";
		const lineId = "seg-shapes-line";

		const features: GeoJSON.Feature[] = shapes.map((s: ShapeRect) => ({
			type: "Feature",
			properties: { role: (s.role || "").toLowerCase() },
			geometry: {
				type: "Polygon",
				coordinates: [[
					[s.minLong, s.minLat],
					[s.maxLong, s.minLat],
					[s.maxLong, s.maxLat],
					[s.minLong, s.maxLat],
					[s.minLong, s.minLat],
				]],
			},
		}));

		try {
			// Remove any prior shape layers/source before re-adding
			safeCleanupLayers(map, [fillId, lineId], [srcId]);
			map.addSource(srcId, { type: "geojson", data: { type: "FeatureCollection", features: features } });
			mlSourceIds.push(srcId);

			// Role-coded color inline so MapLibre infers the data-driven expression tuple
			// (mandatory = blue, "any" = amber).
			const fillLayer: maplibregl.AddLayerObject = {
				id: fillId,
				type: "fill",
				source: srcId,
				paint: {
					"fill-color": ["case", ["==", ["get", "role"], "any"], "#ff9800", "#2196F3"],
					"fill-opacity": 0.12,
				},
			};
			map.addLayer(fillLayer);
			mlLayerIds.push(fillId);

			const lineLayer: maplibregl.AddLayerObject = {
				id: lineId,
				type: "line",
				source: srcId,
				layout: { "line-join": "round" },
				paint: {
					"line-color": ["case", ["==", ["get", "role"], "any"], "#ff9800", "#2196F3"],
					"line-width": 2,
					"line-opacity": 0.9,
				},
			};
			map.addLayer(lineLayer);
			mlLayerIds.push(lineId);
		} catch (e) {
			console.warn("[seg-stats-maplibre] showShapes error:", e);
		}

		if (fitBounds) {
			const coords: number[][] = [];
			shapes.forEach((s: ShapeRect) => {
				coords.push([s.minLat, s.minLong]);
				coords.push([s.maxLat, s.maxLong]);
			});
			api.fitBounds(coords);
		}
	};

	/**
	 * Load and display effort tracks using bulk fetch + single FeatureCollection.
	 */
	api.showEfforts = async (indices: number[], fitBoundsToFirst: boolean): Promise<void> => {
		// MH-02: captured AFTER the caller's clearAll (_applyLayersMapLibre clears before drawing), so a
		// later view switch (which bumps _effortsGen via clearAll) invalidates this whole load.
		const gen = _effortsGen;
		const map = getMaplibreMap();
		if (!map) return;
		const efforts = shared().efforts;
		const maxPoints = prog() ? prog().getMaxPoints() : 0;

		// Collect unique track IDs for the requested effort indices
		const { validIndices, trackIds } = D().collectUniqueTracks(indices, efforts);
		if (trackIds.length === 0) return;

		// Fast path — a view/style switch re-runs this after clearAll wiped the layers, but the GPS is
		// still cached (clearAll deliberately preserves shared().effortGpsArrays). Rebuild the combined
		// source straight from cache with no network round-trip. _rebuildVisibleEffortSource re-creates
		// the layer and returns true only when every visible effort is cached; a partial cache (first
		// load / a newly-added effort) falls through to the bulk fetch below.
		if (_rebuildVisibleEffortSource(map)) {
			if (fitBoundsToFirst) {
				// Only reached by a pure-efforts segment's initial framing (rare — cache is normally
				// empty then). Fit to the union of all visible tracks, not one arrival-order "first".
				// fitBounds only min/maxes its input, so feed it the two extreme corners instead of
				// materializing every cached point into a throwaway array.
				let minLat = Infinity, maxLat = -Infinity, minLng = Infinity, maxLng = -Infinity;
				for (const vidx of validIndices) {
					const gps = shared().effortGpsArrays[vidx];
					if (gps) for (const p of gps) {
						if (p.lat < minLat) minLat = p.lat;
						if (p.lat > maxLat) maxLat = p.lat;
						if (p.lng < minLng) minLng = p.lng;
						if (p.lng > maxLng) maxLng = p.lng;
					}
				}
				if (minLat !== Infinity) api.fitBounds([[minLat, minLng], [maxLat, maxLng]]);
			}
			return;
		}

		// trackId → effort indices that use it, so each arriving chunk caches exactly its efforts.
		const indicesByTrack = new Map<number, number[]>();
		for (const vidx of validIndices) {
			const tid = must(efforts[vidx], 'validIndices index into efforts').trackId;
			const arr = indicesByTrack.get(tid);
			if (arr) arr.push(vidx);
			else indicesByTrack.set(tid, [vidx]);
		}
		let firstCoords: number[][] | null = null;

		// Cache each chunk's tracks and rebuild the combined source (throttled) as they land —
		// progressive paint rather than waiting for the whole bulk fetch.
		const cacheChunk = (chunk: Record<string, TrackPoint[]>): void => {
			if (gen !== _effortsGen) return; // superseded by a view switch — stop caching + rebuilding
			const m = getMaplibreMap();
			if (!m) return; // map destroyed mid-fetch
			for (const tidStr of Object.keys(chunk)) {
				const data = chunk[tidStr];
				if (!data || data.length === 0) continue;
				const idxs = indicesByTrack.get(Number(tidStr));
				if (!idxs) continue;
				for (const idx of idxs) {
					shared().effortGpsArrays[idx] = data;
					if (!firstCoords) firstCoords = data.map((p: TrackPoint) => [p.lat, p.lng]);
				}
			}
			_scheduleEffortRefresh();
		};

		try {
			await prog().fetchBulkTracks(trackIds, maxPoints, { onChunk: cacheChunk });
		} catch (err) {
			if (gen !== _effortsGen) return; // superseded during the fetch
			console.warn("Bulk track fetch failed, falling back to individual fetches:", err);
			await _fallbackShowEfforts(validIndices, fitBoundsToFirst, gen);
			return;
		}

		if (gen !== _effortsGen) return; // superseded during the fetch — don't paint/yank the camera

		// Paint the final state immediately (cancel any pending throttled rebuild).
		const m2 = getMaplibreMap();
		if (m2) _flushEffortRefresh(m2);

		// Effort endpoint markers removed — full-track GPS endpoints are not the
		// segment entry/exit points and appear as stale out-of-place markers.
		// The segment route view already shows S/E markers for the segment itself.

		if (fitBoundsToFirst && firstCoords) {
			api.fitBounds(firstCoords);
		}
	};

	/** Fallback: individual fetches if bulk endpoint fails */
	async function _fallbackShowEfforts(indices: number[], fitBounds: boolean, gen: number): Promise<void> {
		const efforts = shared().efforts;
		const maxPoints = prog() ? prog().getMaxPoints() : 0;
		const features: GeoJSON.Feature[] = [];
		let firstCoords: number[][] | null = null;
		const map = getMaplibreMap();
		if (!map) return;

		for (let vi = 0; vi < indices.length; vi++) {
			const i = indices[vi];
			// undefined >= n was false and fell through — the new arm is dead at runtime (bucket B).
			if (i === undefined || i >= efforts.length) continue;
			const effort = must(efforts[i], 'i < length (guarded above)');
			try {
				// Through the api module: it owns the per-route URL AND the response shape (the share
				// route returns {trackId, points}, the signed-in route a bare array).
				const data = await fetchSingleTrack(effort.trackId, maxPoints);
				if (gen !== _effortsGen) return; // superseded by a view switch mid-loop
				if (!data || data.length === 0) continue;

				shared().effortGpsArrays[i] = data;

				if (!firstCoords) firstCoords = data.map((p: TrackPoint) => [p.lat, p.lng]);

				features.push(..._buildEffortFeatures(data, effort, i));
			} catch (error) {
				console.error("Error loading effort GPS for MapLibre:", error);
			}
		}

		if (gen !== _effortsGen) return; // superseded — don't re-add the layer over the new view
		if (features.length > 0) {
			_addCombinedEffortLayer(map, features);
		}
		if (fitBounds && firstCoords) api.fitBounds(firstCoords);
	}

	/**
	 * Add all effort tracks as a single combined source + layer.
	 * One source + one layer = one terrain draping pass + one GPU draw call.
	 */
	function _addCombinedEffortLayer(map: maplibregl.Map, features: GeoJSON.Feature[]): void {
		// Clean up any existing combined effort layer first
		_mlEventHandlers.forEach((h) => {
			try {
				_removeLayerHandler(map, h);
			} catch (_e) { /* teardown: may already be removed */ }
		});
		_mlEventHandlers = [];
		safeCleanupLayers(map, ["seg-efforts-line"], ["seg-efforts"]);

		const layerId = "seg-efforts-line";

		map.addSource("seg-efforts", {
			type: "geojson",
			data: { type: "FeatureCollection", features: features },
			tolerance: 2.0,
		});

		// Insert efforts BEFORE the segment underlay (seg-bg) so the dashed
		// segment line renders on top of the effort tracks.
		// Data-driven styling: on-segment portions (_onSeg=1) are full opacity/weight,
		// off-segment portions (_onSeg=0) are dimmed.
		const layerDef = combinedEffortLayerSpec(layerId, "seg-efforts");
		try {
			if (map.getLayer("seg-bg")) {
				map.addLayer(layerDef, "seg-bg");
			} else {
				map.addLayer(layerDef);
			}
		} catch (e) {
			console.warn("[seg-stats-maplibre] _addCombinedEffortLayer error:", e);
		}

		// Single hover handler for all efforts (no per-track listener accumulation)
		const enterFn = (e: maplibregl.MapLayerMouseEvent) => {
			map.getCanvas().style.cursor = "pointer";
			const hovered = e.features?.[0];
			if (hovered) {
				const hoveredIdx = hovered.properties._effortIndex;
				map.setPaintProperty(layerId, "line-width", effortHoverLineWidth(hoveredIdx));
				map.setPaintProperty(layerId, "line-opacity", effortHoverLineOpacity(hoveredIdx));
			}
		};
		const leaveFn = () => {
			map.getCanvas().style.cursor = "";
			map.setPaintProperty(layerId, "line-width", effortBaseLineWidth());
			map.setPaintProperty(layerId, "line-opacity", effortBaseLineOpacity());
		};
		const clickFn = (e: maplibregl.MapLayerMouseEvent) => {
			const clicked = e.features?.[0];
			if (!clicked) return;
			const effortIndex = clicked.properties._effortIndex;
			const eff = shared().efforts[effortIndex];
			if (!eff) return;
			new maplibregl.Popup()
				.setLngLat(e.lngLat)
				.setHTML(D().buildEffortPopupHtml(eff))
				.addTo(map);
		};

		map.on("mouseenter", layerId, enterFn);
		map.on("mouseleave", layerId, leaveFn);
		map.on("click", layerId, clickFn);

		_mlEventHandlers.push({ type: "mouseenter", layer: layerId, fn: enterFn });
		_mlEventHandlers.push({ type: "mouseleave", layer: layerId, fn: leaveFn });
		_mlEventHandlers.push({ type: "click", layer: layerId, fn: clickFn });
	}

	api.showSelectedEfforts = async (): Promise<void> => {
		const segmentCoords = shared().segmentCoords;
		api.clearAll();
		api.addPolyline("seg-bg", segmentCoords, "#5aabff", 3, 0.6, [8, 6]);
		// Always show S/E markers so the segment start/end remain visible
		const startCoord = segmentCoords[0];
		const endCoord = segmentCoords.at(-1);
		if (startCoord && endCoord) {
			api.addCircleMarker("seg-start", [startCoord[1] ?? NaN, startCoord[0] ?? NaN], "#4CAF50", 15, "S");
			api.addCircleMarker("seg-end", [endCoord[1] ?? NaN, endCoord[0] ?? NaN], "#f44336", 15, "E");
		}
		const visible = ctx.getVisibleEffortIndices();
		const efforts = shared().efforts;
		const validIndices = visible.filter((i: number) => i < efforts.length);
		await api.showEfforts(validIndices, false);
		api.fitBounds(segmentCoords);
	};

	// Redraw the displayed effort tracks for the current visible set from the in-memory GPS cache
	// (no refetch, no camera change). Used when efforts are toggled/filtered in the grid. If a
	// newly-visible effort has no cached GPS yet, fall back to the bulk-fetch showEfforts path.
	api.refreshVisibleEfforts = async (): Promise<void> => {
		const map = getMaplibreMap();
		if (!map || !map.getSource("seg-efforts")) return; // no efforts displayed (segment-only)
		const allCached = _rebuildVisibleEffortSource(map);
		if (!allCached) {
			const efforts = shared().efforts;
			const idx = ctx.getVisibleEffortIndices().filter((i: number) => i < efforts.length);
			await api.showEfforts(idx, false);
		}
	};

	api.removeDefaultTrack = (): void => {
		const ml = getMaplibreMap();
		if (!ml) return;
		safeCleanupLayers(
			ml,
			["track-line", "track-start", "track-end"],
			["track", "track-start-src", "track-end-src"],
		);
	};

	// ====================================================================
	// REPLAY LAYER HIDING — static layers are redundant during replay
	// because the replay engine draws its own tracks + markers.
	// Hide them on start, restore on exit.
	// ====================================================================

	/** Remove all static layers/sources from the MapLibre map. */
	api.hideStaticLayers = (): void => {
		const map = getMaplibreMap();
		if (!map) return;

		// Hide the combined effort layer
		try {
			if (map.getLayer("seg-efforts-line"))
				map.setLayoutProperty("seg-efforts-line", "visibility", "none");
		} catch (e: unknown) { console.warn("[seg-stats-maplibre] hideEffortLayer failed:", e); }

		// Hide individually tracked layers (seg-route, seg-bg, etc.)
		mlLayerIds.forEach((id: string) => {
			try {
				if (map.getLayer(id)) map.setLayoutProperty(id, "visibility", "none");
			} catch (e: unknown) { console.warn("[seg-stats-maplibre] hideLayer failed:", e); }
		});

		// Hide DOM markers
		mlMarkers.forEach((m: maplibregl.Marker) => {
			try {
				m.getElement().style.display = "none";
			} catch (e: unknown) { console.warn("[seg-stats-maplibre] hideMarker failed:", e); }
		});
	};

	/** Show all static layers again after replay exits. */
	api.showStaticLayers = (): void => {
		const map = getMaplibreMap();
		if (!map) return;

		try {
			if (map.getLayer("seg-efforts-line"))
				map.setLayoutProperty("seg-efforts-line", "visibility", "visible");
		} catch (e: unknown) { console.warn("[seg-stats-maplibre] showEffortLayer failed:", e); }

		mlLayerIds.forEach((id: string) => {
			try {
				if (map.getLayer(id)) map.setLayoutProperty(id, "visibility", "visible");
			} catch (e: unknown) { console.warn("[seg-stats-maplibre] showLayer failed:", e); }
		});

		mlMarkers.forEach((m: maplibregl.Marker) => {
			try {
				m.getElement().style.display = "";
			} catch (e: unknown) { console.warn("[seg-stats-maplibre] showMarker failed:", e); }
		});
	};

	/**
	 * Rebuild the combined `seg-efforts` source IN-PLACE from the cached GPS arrays for the current
	 * visible set (source.setData — no layer churn, no camera disruption). Returns false when the
	 * source is absent (no efforts displayed) or a visible effort has no cached GPS yet, so callers
	 * can decide to fall back to a fetch.
	 */
	function _rebuildVisibleEffortSource(map: maplibregl.Map): boolean {
		const efforts = shared().efforts;
		const visible = ctx.getVisibleEffortIndices().filter((idx: number) => idx < efforts.length);
		const features: GeoJSON.Feature[] = [];
		let allCached = true;
		for (const aIdx of visible) {
			const eff = must(efforts[aIdx], 'visible filtered to < length');
			const gps = shared().effortGpsArrays[aIdx];
			if (!gps || gps.length === 0) {
				allCached = false;
				continue;
			}
			features.push(..._buildEffortFeatures(gps, eff, aIdx));
		}
		const source = map.getSource("seg-efforts") as maplibregl.GeoJSONSource | undefined;
		if (source && typeof source.setData === "function") {
			// Source exists (efforts already shown / upgrade) → update in place, no layer churn.
			source.setData({ type: "FeatureCollection" as const, features: features });
		} else if (features.length > 0) {
			// First chunk of a fresh display → create the combined source + layer + hover handlers.
			_addCombinedEffortLayer(map, features);
		}
		return allCached;
	}

	/** Coalesce per-chunk source rebuilds during a chunked load/upgrade (one setData per interval). */
	function _scheduleEffortRefresh(): void {
		if (_effortRefreshTimer != null) return;
		const gen = _effortsGen; // MH-02: belt-and-braces — a late timer after a view switch is a no-op
		_effortRefreshTimer = setTimeout(() => {
			_effortRefreshTimer = null;
			if (gen !== _effortsGen) return;
			const m = getMaplibreMap();
			if (m) _rebuildVisibleEffortSource(m);
		}, EFFORT_REFRESH_THROTTLE_MS);
	}

	/** Paint the final state now, cancelling any pending throttled rebuild. */
	function _flushEffortRefresh(map: maplibregl.Map): void {
		if (_effortRefreshTimer != null) {
			clearTimeout(_effortRefreshTimer);
			_effortRefreshTimer = null;
		}
		_rebuildVisibleEffortSource(map);
	}

	/**
	 * Compute the on-segment split for one effort (the impure step — reads the injected data api +
	 * shared segment coords), then delegate to the pure builder in segment-stats-map-features.ts.
	 */
	function _buildEffortFeatures(gps: TrackPoint[], effort: SegmentEffort, idx: number): GeoJSON.Feature[] {
		const split = D().splitSegmentIndices(gps, effort, shared().segmentCoords);
		return buildEffortFeatures(gps, effort, idx, split);
	}
} // end initSegmentStatsMapLibre

// Top-level call — populates `api` at import, exactly as the bare assignments did before the hoist.
// The guard above already threw if ctx is absent; the singleton ctx is passed as the received parameter.
initSegmentStatsMapLibre(ctx);
