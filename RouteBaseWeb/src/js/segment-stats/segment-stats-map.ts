/**
 * Segment Statistics — Map orchestrator
 *
 * Map display cycles through three modes with one button:
 *   0 = segment route only
 *   1 = segment route + selected effort tracks
 *   2 = selected effort tracks only
 * S/E markers are always drawn in every mode.
 *
 * Delegates rendering to maplibreApi (segment-stats-map-maplibre.ts).
 *
 * Lifecycle orchestration (MapLibre init, style switching via the registry
 * styleSwitcher, bfcache recovery, size controls) is handled by MapShell.
 *
 * Depends on: ctx (shared context from segment-statistics.ts)
 *             maplibreApi (imported directly)
 */
import { readData } from '../core/data-attrs';
import type { SegmentEffort } from './segment-stats-effort-types';
import type { SegmentStatsMapShared, SegmentStatsMapLibreApi } from './segment-stats-map-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { ctx } from './segment-statistics';
import { maplibreApi, bindShared as bindMaplibreShared } from './segment-stats-map-maplibre';
import { mapHoverApi as H } from './segment-stats-map-hover';
import { createMapShell } from '../core/map-shell';
import { defaultBasemapId, demProviderFor } from '../core/basemap-registry';
import { createHeatmapControls } from './heatmap-controls';
import { registerMapShared, registerRefreshEffortVisibility, getReplayApi } from './seg-stats-shared';
import { initMapLibreTerrain, destroyMapLibre } from '../maplibre/maplibre-terrain';
import { getMaplibreMap } from '../core/map-state';
import { helpers as mlHelpers } from '../maplibre/maplibre-helpers';
import { swapStyleWithTerrain, cancelPendingSwap } from '../maplibre/maplibre-style-swap';
import { setupMapToolbar } from '../core/map-toolbar';
import { must } from '../core/invariant';
import { getAccountSettings } from '../core/account-settings';

/**
 * Build the segment map orchestrator: shared map state, the view state machine, MapShell lifecycle,
 * heatmap controls, and the map→chart hover binding. Hoisted out of the module IIFE (was a top-level
 * `else` body that ran on import) so the map wiring is reachable from a test with a synthetic ctx and a
 * fake maplibre engine. Behaviour and import-time semantics are unchanged: the top-level guard at the
 * bottom passes the shared ctx exactly as before, so registerMapShared() still runs at import — after
 * this module's own imports (incl. map-hover) have loaded — exactly as it did before the hoist.
 */
export function initSegmentStatsMap(ctx: SegmentStatisticsContext): void {

	const config = ctx.config;
	// Polyline segments have segmentCoords; shape-defined segments have painted rectangles (shapes) instead.
	const segmentCoords = config.segmentCoords ?? [];
	const efforts = config.efforts ?? [];
	const shapes = config.shapes ?? [];
	const hasSegmentLine = segmentCoords.length > 0;
	const hasShapes = shapes.length > 0;

	// ====================================================================
	// SHARED MAP STATE
	// ====================================================================

	const shared: SegmentStatsMapShared = {
		segmentCoords: segmentCoords,
		efforts: efforts,
		effortGpsArrays: [],
		shapes: shapes,
		getActiveEngine: () => ({ engine: "", styleUrl: null }),
	};
	// Inject into the map engine (avoids circular import) and register in the shared
	// slot module for map-hover.ts, which reads it lazily to avoid a dep cycle.
	bindMaplibreShared(shared);
	registerMapShared(shared);

	// ====================================================================
	// MAP MODE
	// ====================================================================

	// The map shows one mutually-exclusive view at a time, chosen from the "Show" menu. Heatmap is a
	// view (not a separate toggle): it owns the raster and suppresses tracks; the others draw the
	// segment/effort lines. Default = Heatmap when tiles are ready (set after heatmapControls below),
	// else Seg. + Eff., else Efforts (a segment with neither a line nor shapes).
	type MapView = "heatmap" | "heateff" | "segeff" | "segment" | "efforts";
	interface ViewSpec { showSegment: boolean; showEfforts: boolean; heatmap: boolean; label: string }

	// The camera is framed once on open and never re-framed by a view switch (see setMapView), so a
	// view carries no fitBounds intent — it only declares which layers it draws.
	const VIEWS: Record<MapView, ViewSpec> = {
		heatmap: { showSegment: true,  showEfforts: false, heatmap: true,  label: "Heatmap" },
		heateff: { showSegment: false, showEfforts: true,  heatmap: true,  label: "Heatmap + Eff." },
		segeff:  { showSegment: true,  showEfforts: true,  heatmap: false, label: "Seg. + Eff." },
		segment: { showSegment: true,  showEfforts: false, heatmap: false, label: "Segment" },
		efforts: { showSegment: false, showEfforts: true,  heatmap: false, label: "Efforts" },
	};

	let mapView: MapView = (hasSegmentLine || hasShapes) ? "segeff" : "efforts";

	// ====================================================================
	// IN-PLACE MAPLIBRE STYLE SWITCHING (page-owned; MapShell delegates here)
	// ====================================================================

	// Tracks the currently active MapLibre style URL so we can guard no-op switches
	// and drive the camera restore after style.load fires.
	let _activeMapLibreStyleUrl: string | null = null;

	// Generation counter — a slow style fetch from an earlier click must not clobber a newer switch.
	// The pending style.load/idle listeners themselves are now owned by the shared swapStyleWithTerrain.
	let _styleGeneration = 0;

	function _switchSegMapLibreStyle(ml: maplibregl.Map, styleUrl: string, basemapId?: string | null): void {
		if (!ml || styleUrl === _activeMapLibreStyleUrl) return;

		// MH-05: resolve the target style BEFORE any destructive step. buildInlineTerrainStyle returns
		// null when no MapTiler key is configured; the old order wiped every overlay + committed the new
		// URL first, so this bail left a bare basemap with both no-op guards armed (MS-06-class). Hoisting
		// the resolution keeps the current view intact on an unbuildable style.
		const newStyle: maplibregl.StyleSpecification | string | null =
			styleUrl === "terrain" ? mlHelpers.buildInlineTerrainStyle(1.0) : styleUrl;
		if (!newStyle) { console.warn('[seg-stats-map] style unresolvable, keeping current:', styleUrl); return; }

		const gen = ++_styleGeneration;
		const savedCam = shell.captureCamera();
		_activeMapLibreStyleUrl = styleUrl;
		maplibre().clearAll();

		const generationOk = (): boolean => gen === _styleGeneration;

		// Deferred to the first idle after the swap (terrain wants settled tiles): restore the camera,
		// re-apply 3D for the switched basemap, and redraw the segment/effort layers.
		const onIdle = (): void => {
			if (savedCam) {
				try {
					ml.jumpTo({
						center: savedCam.center as [number, number],
						zoom: savedCam.zoom,
						pitch: savedCam.pitch,
						bearing: savedCam.bearing,
					});
				} catch (e: unknown) { console.warn('[seg-stats-map] jumpTo after style load failed:', e); }
			}
			// Route DEM by basemap id (keyless → terrarium, MapTiler entries → maptiler).
			// No key gate: keyless styles get terrain via the terrarium DEM.
			if (styleUrl !== "terrain") {
				try { mlHelpers.addHillshadeAndLight(ml, 1.0, true, demProviderFor(basemapId ?? "")); } catch (e: unknown) {
					console.warn('[seg-stats-map] addHillshadeAndLight failed:', e);
				}
			}
			restoreViewOnMapLibre();
		};

		if (typeof newStyle === "object") {
			swapStyleWithTerrain(ml, newStyle, { generationOk, onIdle });
			return;
		}

		mlHelpers.fetchStyleJson(newStyle)
			.then((json: Parameters<maplibregl.Map["setStyle"]>[0]) => {
				if (!generationOk()) return;
				swapStyleWithTerrain(ml, json as maplibregl.StyleSpecification, { generationOk, onIdle });
			})
			.catch((_err: unknown) => {
				if (!generationOk()) return;
				swapStyleWithTerrain(ml, newStyle, { generationOk, onIdle }); // fallback: let maplibre fetch the URL
			});
	}

	// ====================================================================
	// ENGINE / ACCESSOR HELPERS
	// ====================================================================

	function maplibre(): SegmentStatsMapLibreApi { return maplibreApi; }

	/** Midpoint of the segment (polyline) or centre of the painted shapes — MapLibre initial center. */
	function _getCenterPt(): [number, number] | null {
		if (segmentCoords && segmentCoords.length > 0) {
			const mid = must(segmentCoords[Math.floor(segmentCoords.length / 2)], 'mid index of non-empty coords');
			return [mid[1] ?? NaN, mid[0] ?? NaN];
		}
		if (hasShapes) {
			let minLat = Infinity, maxLat = -Infinity, minLng = Infinity, maxLng = -Infinity;
			for (const s of shapes) {
				if (s.minLat < minLat) minLat = s.minLat;
				if (s.maxLat > maxLat) maxLat = s.maxLat;
				if (s.minLong < minLng) minLng = s.minLong;
				if (s.maxLong > maxLng) maxLng = s.maxLong;
			}
			return [(minLng + maxLng) / 2, (minLat + maxLat) / 2];
		}
		return null;
	}

	// ====================================================================
	// LAYER RENDERER
	// ====================================================================

	// —— Optional heatmap raster overlay ——————————————————————————————————————
	// A shapes segment can carry a heatmap raster underneath its line layers. heatmap-controls owns
	// the chip/generate UI + render polling; here we just re-attach the raster on every redraw so it
	// sits below the freshly-drawn efforts/segment. MapLibre stacks later-added layers on top, so the
	// raster is attached BEFORE the lines.
	async function _applyLayers(fitBounds: boolean): Promise<void> {
		ctx.hideMapHighlight();
		// Heatmap is a mutually-exclusive view: attach the raster only in "Heatmap" view (which then
		// suppresses tracks), and detach it for every other view so the segment/effort lines stand alone.
		const view = VIEWS[mapView];
		const m = getMaplibreMap();
		if (m) heatmapControls.applyOverlay(m, view.heatmap, false);
		await _applyLayersMapLibre(view, fitBounds);
		_syncButton();
	}

	async function _applyLayersMapLibre(mode: { showSegment: boolean; showEfforts: boolean }, fitBounds: boolean): Promise<void> {
		maplibre().clearAll();
		_drawEndpointMarkersMapLibre();

		if (mode.showSegment && !mode.showEfforts) {
			if (hasSegmentLine) maplibre().showSegmentRoute(fitBounds);  // calls clearAll + draws polyline/markers
			if (hasShapes) maplibre().showShapes(fitBounds);    // additive — drawn after, fits to shapes
		} else if (!mode.showSegment && mode.showEfforts) {
			_drawSegmentUnderlayMapLibre();                     // no-op for shape segments
			const idx = ctx.getVisibleEffortIndices().filter((i: number) => i < efforts.length);
			// Efforts-only view. The one time we fit here is a pure-efforts segment's initial framing
			// (fitBounds set by onMapLibreReady) — it has no static geometry to center on.
			await maplibre().showEfforts(idx, fitBounds);
		} else {
			// seg+eff: draw the segment AFTER the efforts so the dashed reference line / shape
			// outlines layer on top (MapLibre stacks later-added layers above earlier ones).
			// Segment geometry is present, so never fit to the efforts (their whole-ride bounds differ).
			const idx = ctx.getVisibleEffortIndices().filter((i: number) => i < efforts.length);
			await maplibre().showEfforts(idx, false);
			_drawSegmentUnderlayMapLibre();                     // no-op for shape segments
			if (hasShapes) maplibre().showShapes(fitBounds);
		}
	}

	// —— View dropdown sync ————————————————————————————————————————————————
	// Summary + active item reflect the CURRENT view (the dropdown picks a view directly).
	function _syncButton(): void {
		const value = document.getElementById("mapModeValue");
		if (value) {
			value.textContent = VIEWS[mapView]?.label ?? "";
			// Carry the heatmap tint onto the collapsed summary too (Heatmap + Heatmap + Eff.).
			value.classList.toggle("is-heatmap", VIEWS[mapView]?.heatmap === true);
		}
		document.querySelectorAll<HTMLElement>("[data-map-view]").forEach((b) => {
			b.classList.toggle("active", readData(b, 'mapView') === mapView);
		});
		_updateFilterPill();
	}

	// Reflect heatmap render status on its "Show" menu items (Heatmap + Heatmap + Eff.): hidden until a
	// render exists, disabled until tiles are viewable. The solo Heatmap item's label mirrors progress.
	// Owned here because the items live in this menu.
	function _syncHeatmapMenuItem(status: string): void {
		const ready = status === "done" || status === "stale";
		document.querySelectorAll<HTMLButtonElement>('[data-heatmap-view]').forEach((item) => {
			item.hidden = status === "none";
			item.disabled = !ready;
		});
		const solo = document.querySelector<HTMLButtonElement>('[data-map-view="heatmap"]');
		if (solo) solo.textContent = (status === "pending" || status === "running") ? "Heatmap…"
			: status === "failed" ? "Heatmap failed"
			: "Heatmap";
	}

	function _drawEndpointMarkersMapLibre(): void {
		const c = segmentCoords;
		if (!c || c.length === 0) return;
		const first = must(c[0], 'non-empty (guarded above)');
		const last = must(c[c.length - 1], 'non-empty (guarded above)');
		maplibre().addCircleMarker("seg-start", [first[1] ?? NaN, first[0] ?? NaN], "#4CAF50", 15, "S");
		maplibre().addCircleMarker(
			"seg-end",
			[last[1] ?? NaN, last[0] ?? NaN],
			"#f44336",
			15,
			"E",
		);
	}

	function _drawSegmentUnderlayMapLibre(): void {
		if (!segmentCoords || segmentCoords.length === 0) return;
		maplibre().addPolyline("seg-bg", segmentCoords, "#0e84ff", 4, 0.8, [2, 2]);
	}

	// Re-apply the current view after a basemap-style switch wiped every source/layer. Route through
	// _applyLayers (not _applyLayersMapLibre directly) so the heatmap raster re-attaches before the line
	// layers redraw above. fitBounds=false: setStyle preserves the camera (and afterStyleLoaded restores
	// it via jumpTo), so a style switch must never re-frame.
	function restoreViewOnMapLibre(): void {
		void _applyLayers(false);
	}

	// Map → chart proximity hover (segment-stats-map-hover.ts). Re-bound on every
	// onMapLibreReady: that callback fires with a fresh map instance (initial init +
	// bfcache re-init), and the old map + its listeners are destroyed with it, so a
	// full re-bind neither stacks listeners nor leaves a stale map captured in project().
	function _bindMapHover(ml: maplibregl.Map): void {
		H.bind({
			project: (lat: number, lng: number): { x: number; y: number } | null => {
				try { const p = ml.project([lng, lat]); return { x: p.x, y: p.y }; }
				catch { return null; }
			},
			onDragStart: (cb: () => void): void => { ml.on("dragstart", cb); },
			onDragEnd:   (cb: () => void): void => { ml.on("dragend", cb); },
		});
		ml.on("mousemove", (e: maplibregl.MapMouseEvent) => H.handleMouseMove(e.lngLat));
		ml.on("mouseout", () => { if (typeof ctx.onChartLeave === "function") ctx.onChartLeave(); });
	}

	// ====================================================================
	// EFFORT INIT
	// ====================================================================

	efforts.forEach((effort: SegmentEffort, i: number) => {
		effort.color = ctx.effortColors[effort.colorIndex];
		ctx.effortVisibility.add(i);
	});

	// ====================================================================
	// PUBLIC API
	// ====================================================================

	// Select a view directly (driven by the "Show" dropdown).
	const setMapView = (view: string): void => {
		if (!(view in VIEWS)) return;
		mapView = view as MapView;
		// A view switch only swaps layers — never re-frames the camera (the map was framed once on
		// open). fitBounds=false everywhere; efforts re-attach from cache, no refetch.
		void _applyLayers(false);
	};

	// —— Filtered-effort cue + debounced map refresh ————————————————————————
	// Visibility toggles/grid-filters change the visible effort set (checked ∩ filtered). Redraw
	// the map's effort tracks from the in-memory GPS cache (no refetch) and keep a small "filtered"
	// pill in sync. A short coalesce batches a burst of checkbox clicks into one redraw.
	const EFFORT_REFRESH_DEBOUNCE_MS = 150;
	let _effortRefreshTimer: ReturnType<typeof setTimeout> | undefined;

	function _updateFilterPill(): void {
		const pill = document.getElementById("segMapFilterPill");
		if (!pill) return;
		const total = efforts.length;
		const visible = ctx.getVisibleEffortIndices().filter((i: number) => i < total).length;
		// Only meaningful when efforts are actually drawn and some are hidden.
		if (VIEWS[mapView].showEfforts && visible < total) {
			pill.textContent = visible + "/" + total;
			pill.hidden = false;
		} else {
			pill.hidden = true;
		}
	}

	registerRefreshEffortVisibility((): void => {
		_updateFilterPill();
		// Only the map track redraw is debounced; pill updates immediately above.
		// Skip when the current view hides efforts (Heatmap/Segment) — nothing to repaint.
		if (!VIEWS[mapView].showEfforts) return;
		if (_effortRefreshTimer) clearTimeout(_effortRefreshTimer);
		_effortRefreshTimer = setTimeout(() => {
			_effortRefreshTimer = undefined;
			void maplibre().refreshVisibleEfforts();
		}, EFFORT_REFRESH_DEBOUNCE_MS);
	});

	// ====================================================================
	// MAP SHELL — lifecycle orchestration
	// ====================================================================

	const shell = createMapShell({
		containerId: "map",
		// Registry-driven style picker: one button per availableBasemaps() (keyed or keyless).
		styleSwitcher: { containerId: "segMapStyleSwitcher", btnClass: "map-dd-item" },
		sizeControls: {
			mapDivId:         "segHero",
			defaultHeight:    700,
			growBtnId:        "segMapGrowBtn",
			shrinkBtnId:      "segMapShrinkBtn",
			resetBtnId:       "segMapResetBtn",
			fullscreenBtnId:  "segMapExpandBtn",
			fullscreenCardId: "segHero",
		},
		// Always defer init so shell is assigned before any callback fires.
		// We manually call shell.switchToBasemap() below.
		deferInit: true,

		createMapLibre: (containerId, styleUrl, savedCam, basemapId) => {
			initMapLibreTerrain(
				containerId,
				_getCenterPt(),
				savedCam ? savedCam.zoom : 13,
				getAccountSettings().map.terrainExaggeration,
				styleUrl,
				basemapId,
			);
			return getMaplibreMap();
		},

		onMapLibreReady: (_map, styleUrl, _savedCam) => {
			_activeMapLibreStyleUrl = styleUrl;
			_bindMapHover(_map);
			// One-time initial framing. Line/shape segments keep their center+zoom initial camera
			// (fitBounds=false); a pure-efforts segment has no static geometry to center on, so fit to
			// its efforts once. Every later view/style switch passes false and leaves the camera alone.
			void _applyLayers(!hasSegmentLine && !hasShapes);
		},

		switchMapLibreStyle: (map, styleUrl, basemapId) => {
			_switchSegMapLibreStyle(map, styleUrl, basemapId);
		},

		destroyMapLibre: (map) => {
			// Invalidate any pending style-load/idle callbacks before the GL context goes away; the
			// generation bump makes any that still fire inert.
			cancelPendingSwap(map);
			++_styleGeneration;
			_activeMapLibreStyleUrl = null;
			maplibre().clearAll();
			destroyMapLibre();
			shared.effortGpsArrays.length = 0;
		},

		onBfcacheRestore: () => {
			// Stop any running replay loop (it holds event listeners on the old map)
			const replayApi = getReplayApi();
			if (replayApi) {
				try { replayApi.destroy(); } catch (_ex) { /* teardown */ }
			}
			if (ctx.stopReplay) {
				try { ctx.stopReplay(); } catch (_ex) { /* teardown */ }
			}
		},
	});

	// Wire shared engine-state accessor through the shell (MapLibre-only).
	shared.getActiveEngine = (): { engine: string; styleUrl: string | null } =>
		({ engine: 'maplibre', styleUrl: shell.getStyleUrl() });

	// Heatmap controls (data lifecycle + owner Generate button). The heatmap is a "Show" view, so this
	// orchestrator owns selection; the controls only report status and attach/detach the raster.
	const heatmapControls = createHeatmapControls({
		redraw: () => { void _applyLayers(false); },
		onStatusChange: (status: string) => _syncHeatmapMenuItem(status),
		onGenerated: () => setMapView("heatmap"),
	});

	// Expose the live heatmap-view state so the replay engine suppresses its track polylines (showing
	// only the markers over the raster) when the Heatmap view is active.
	ctx.isHeatmapShown = (): boolean => mapView === "heatmap";

	// Heatmap ready at load → default to the Heatmap view (raster only); reveal/label its menu item.
	_syncHeatmapMenuItem(heatmapControls.status());
	if (heatmapControls.tilesReady()) mapView = "heatmap";

	// Initialise MapLibre on the default basemap. Always consult the profile default
	// (streets/satellite/terrain or any keyless id) when it resolves, else liberty.
	// Persist/pass ids, never resolved URLs. Read from the seam, not the page config: the basemap is an
	// ACCOUNT setting, and the page island used to carry its own copy of it.
	const defaultId = defaultBasemapId(getAccountSettings().map.defaultStyle);
	shell.switchToBasemap(defaultId);
	shell.setActiveBasemap(defaultId);

	_setupToolbarDropdowns();

	// —— Toolbar dropdown shells ————————————————————————————————————————————
	// The <details> shell (value sync, close-on-choose, outside-click) lives in core/map-toolbar.ts —
	// the race replay uses the same one. Option buttons keep their IDs so MapShell still wires style +
	// size; only the segment-specific view picker is handled here.
	function _setupToolbarDropdowns(): void {
		setupMapToolbar();

		// Show-menu items select a view directly (a disabled Heatmap item — tiles not ready — is inert).
		document.querySelectorAll<HTMLElement>("[data-map-view]").forEach((btn) => {
			btn.addEventListener("click", () => {
				if (btn.hasAttribute("disabled")) return;
				setMapView(readData(btn, 'mapView') ?? "");
			});
		});

	}

} // end initSegmentStatsMap

if (!ctx || !ctx.config.hasSegment ||
	(!ctx.config.hasGpsData && !ctx.config.hasEfforts &&
	 !(ctx.config.shapes && ctx.config.shapes.length > 0) && !ctx.config.hasHeatmap)) {
	// Page has no segment, and neither a polyline, any efforts, painted shapes, nor a heatmap — skip init.
} else {
	initSegmentStatsMap(ctx);
}
