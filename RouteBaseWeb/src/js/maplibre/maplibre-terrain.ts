// maplibre-terrain.ts — MapLibre GL map initialization, terrain, style switching
// Depends on: maplibre-helpers.ts (H), maplibre-track.ts (TRK), MapLibreAdapter + mapDispatcher (core)
import type { GpsPoint } from '../core/core-types';
import { helpers as H } from './maplibre-helpers';
import { swapStyleWithTerrain } from './maplibre-style-swap';
import { installMissingImageFallback } from './missing-image-fallback';
import { installStyleFilterSanitizer } from './style-filter-sanitizer';
import { api as TRK } from './maplibre-track';
import { MapLibreAdapter } from '../core/map-utils-maplibre';
import { mapDispatcher, getCurrentColorMode } from '../core/map-dispatcher';
import { demProviderFor } from '../core/basemap-registry';
import { getLockedSplit } from '../core/split-state';
import {
	getMaplibreMap, setMaplibreMap,
	isBlueMarkerVisible, isReplayActive,
} from '../core/map-state';

type StyleSpec = import("maplibre-gl").StyleSpecification;

/**
 * Page-provided load hooks. Details wires hover tracking + track hover/leave + the
 * post-load replay-button reveal; seg-stats/overview pass nothing and get the fallback
 * (dispatcher proximity hover).
 */
export interface MapLibreHooks {
	setupHoverTracking?: () => void;
	onTrackHover?: (index: number, source: string) => void;
	onTrackLeave?: () => void;
	onMapReady?: (map: maplibregl.Map) => void;
}

/** Camera state saved before a style switch so we can restore it after. */
interface SavedCamera {
	center: [number, number];
	zoom: number;
	pitch: number;
	bearing: number;
}

/** Union type for the centerOrGps parameter of initMapLibreTerrain. */
type CenterOrGpsInput = [number, number] | GpsPoint[] | { lat?: number; latitude?: number; lng?: number; longitude?: number } | null | undefined;

const _trackBoundsMap = new WeakMap<object, [[number, number], [number, number]]>();

// Per-init closures over the current map, published for the stable module-level delegators
// (setMaplibreStyle / updateMaplibreTrackColors). Null when no map is live → delegators no-op,
// same as the former absent-window-fn behavior.
let _active: {
	setStyle: (styleKeyOrUrl: string, basemapId?: string | null) => Promise<void>;
	updateTrackColors: (colorMode: string, gpsArr?: GpsPoint[]) => void;
} | null = null;

/** Switch the active map's basemap style (async — fetches style JSON first). */
export function setMaplibreStyle(styleKeyOrUrl: string, basemapId?: string | null): Promise<void> {
	return _active?.setStyle(styleKeyOrUrl, basemapId) ?? Promise.resolve();
}

/** Recolor the active map's track by mode (speed/heartrate/elevation/cadence/none). */
export function updateMaplibreTrackColors(colorMode: string, gpsArr?: GpsPoint[]): void {
	_active?.updateTrackColors(colorMode, gpsArr);
}

// Active DEM provider for the current basemap. Default 'terrarium' (keyless AWS)
// so 3D works without a MapTiler key. The init/switch paths call the setter with
// the last-selected basemap id; C5/BT-06 feed the real id via MapShell.
let _currentDemProvider: 'terrarium' | 'maptiler' = 'terrarium';

/** Set the active DEM provider from a basemap registry id (default terrarium). */
export function setMaplibreDemProvider(basemapId?: string | null): void {
	_currentDemProvider = basemapId ? demProviderFor(basemapId) : 'terrarium';
}

/** Tear down the live MapLibre instance and clear the delegator slot. */
export function destroyMapLibre(): void {
	H.destroyInstance();
	TRK.destroy(); // MS-07: release the track module's dead map + coord/color caches
	_active = null;
}

// ====================================================================
// INIT
// ====================================================================

export function initMapLibreTerrain(
	container: string | HTMLElement,
	centerOrGps?: CenterOrGpsInput,
	zoom?: number,
	exaggeration?: number,
	styleUrl?: string | null,
	basemapId?: string | null,
	hooks?: MapLibreHooks,
	savedCam?: SavedCamera | null,
): maplibregl.Map | null {
	centerOrGps = centerOrGps || ([8.45, 47.15] as [number, number]);
	zoom = zoom || 12;
	exaggeration = exaggeration || H.terrainExaggeration();
	// Route DEM by the chosen basemap; keyless styles default to terrarium.
	setMaplibreDemProvider(basemapId);

	if (typeof maplibregl === "undefined") {
		console.error("maplibregl not loaded");
		return null;
	}

	let center: [number, number] = [8.45, 47.15];
	let gpsPoints: GpsPoint[] | null = null;

	try {
		if (Array.isArray(centerOrGps)) {
			if (
				centerOrGps.length === 2 &&
				typeof centerOrGps[0] === "number" &&
				typeof centerOrGps[1] === "number"
			) {
				center = centerOrGps as [number, number];
			} else {
				gpsPoints = centerOrGps as GpsPoint[];
				const derived = H.centerFromGpsData(centerOrGps as GpsPoint[]);
				if (derived) center = derived;
			}
		} else if (centerOrGps && typeof centerOrGps === "object") {
			const lat =
				typeof centerOrGps.lat === "number" ? centerOrGps.lat : centerOrGps.latitude;
			const lng =
				typeof centerOrGps.lng === "number" ? centerOrGps.lng : centerOrGps.longitude;
			if (typeof lat === "number" && typeof lng === "number") center = [lng, lat];
		}
	} catch (e) {
		console.warn("Error normalizing centerOrGps", e);
	}

	// MS-02: a restored camera (bfcache re-init) wins over the gps/registry-derived center
	// so Back returns to the prior viewport. Track extraction above still stands.
	let initPitch = H.DEFAULT_PITCH;
	let initBearing = 0;
	if (savedCam && savedCam.center) {
		center = savedCam.center;
		zoom = savedCam.zoom;
		initPitch = savedCam.pitch;
		initBearing = savedCam.bearing;
	}

	if (getMaplibreMap()) {
		try {
			destroyMapLibre();
		} catch (_e) { /* teardown: previous instance may already be destroyed */ }
		setMaplibreMap(null);
	}

	const isTerrainStyle = styleUrl === "terrain";
	let initialStyle: StyleSpec | string | null = styleUrl || null;
	if (isTerrainStyle) {
		initialStyle = H.buildInlineTerrainStyle(exaggeration);
		if (!initialStyle) return null;
	}
	// Invariant (MP2-02): callers resolve styleUrl via basemap-registry before reaching
	// here, so a non-terrain styleUrl is always a ready URL (never falsy).
	if (!initialStyle) return null;

	const map = new maplibregl.Map({
		container: container,
		style: initialStyle,
		center: center,
		zoom: zoom,
		pitch: initPitch,
		bearing: initBearing,
		maxZoom: H.defaultMapOptions.maxZoom,
		maxPitch: H.defaultMapOptions.maxPitch,
		fadeDuration: H.defaultMapOptions.fadeDuration,
		refreshExpiredTiles: H.defaultMapOptions.refreshExpiredTiles,
		renderWorldCopies: H.defaultMapOptions.renderWorldCopies,
		attributionControl: H.defaultMapOptions.attributionControl,
	});

	// Apply sky-blue background to the canvas/container immediately so that
	// unfilled terrain tile gaps show the sky colour instead of white.
	H.applyMapBackground(map);

	// Placeholder for POI sprite icons the basemap references but the sprite lacks (silences the
	// per-icon "Image could not be loaded" console spam).
	installMissingImageFallback(map);
	installStyleFilterSanitizer(map); // null-guard basemap numeric filters (poi rank / boundary admin_level)

	// ================================================================
	// TRACK RENDERING — delegated to maplibre-track.ts
	// ================================================================

	TRK.bind(map, _trackBoundsMap);

	// ================================================================
	// STYLE SWITCHING
	// ================================================================

	let _currentStyleKey: string | null = styleUrl || null;
	let _switchGeneration = 0;

	const setStyle = async (styleKeyOrUrl: string, basemapId?: string | null): Promise<void> => {
		if (styleKeyOrUrl === _currentStyleKey) {
			return;
		}
		// Route DEM by the target basemap; keyless styles default to terrarium.
		setMaplibreDemProvider(basemapId);

		try {
			const cam: SavedCamera | null =
				map && map.getCenter
					? {
							center: map.getCenter().toArray() as [number, number],
							zoom: map.getZoom(),
							pitch: map.getPitch(),
							bearing: map.getBearing(),
						}
					: null;

			const isTerrain = styleKeyOrUrl === "terrain";
			// MP2-02: styleKeyOrUrl is always a registry-resolved URL for non-terrain styles.
			const requested: StyleSpec | string | null = isTerrain
				? H.buildInlineTerrainStyle(H.terrainExaggeration())
				: styleKeyOrUrl;
			// buildInlineTerrainStyle returns null with no MapTiler key — resolve BEFORE the destructive
			// swap so an unbuildable terrain style keeps the current view instead of stranding a bare map.
			if (!requested) { console.warn("setMaplibreStyle: terrain style unbuildable (no key), keeping current"); return; }

			_currentStyleKey = styleKeyOrUrl;
			const gen = ++_switchGeneration;
			const generationOk = (): boolean => gen === _switchGeneration;

			// Re-add app layers + restore camera after the clean rebuild. The inline terrain style bakes
			// terrain in (its own control wiring), so it only re-adds the track here.
			const reAdd = (): void => {
				if (!isTerrain) {
					try {
						H.addHillshadeAndLight(map, H.terrainExaggeration(), true, _currentDemProvider);
					} catch (e) { console.warn("setStyle re-add: addHillshadeAndLight error", e); }
				}
				try {
					if (gpsPoints && Array.isArray(gpsPoints) && gpsPoints.length > 0) {
						const colorMode = getCurrentColorMode();
						if (colorMode && colorMode !== "none") {
							TRK.updateTrackColors(colorMode, gpsPoints);
						} else {
							TRK.addTrackToMap(gpsPoints, true);
						}
					}
				} catch (e) { console.warn("setStyle re-add: track re-add error", e); }

				if (cam && cam.center) {
					try {
						map.jumpTo({ center: cam.center, zoom: cam.zoom, bearing: cam.bearing, pitch: cam.pitch });
					} catch (e: unknown) { console.warn('[maplibre-terrain] restoreCamera failed:', e); }
				}
			};

			// The shared swapStyleWithTerrain owns the terrain-off → strip → setStyle{diff:false} sequence.
			// Inline terrain re-adds on idle (style.load fires before terrain tiles settle); a string
			// basemap is ready at style.load, so it re-adds there (preserving the prior timing).
			if (typeof requested === "object") {
				swapStyleWithTerrain(map, requested, { generationOk, onIdle: reAdd });
				return;
			}

			try {
				const styleJson = await H.fetchStyleJson(requested);
				if (!generationOk()) return;
				swapStyleWithTerrain(map, styleJson as StyleSpec, { generationOk, onStyleLoad: reAdd });
				return;
			} catch (fetchErr) {
				console.warn("Style fetch failed, using URL fallback:", fetchErr);
			}
			if (!generationOk()) return;
			swapStyleWithTerrain(map, requested, { generationOk, onStyleLoad: reAdd }); // let maplibre fetch the URL
		} catch (e) {
			console.warn("setMaplibreStyle error", e);
		}
	};

	// Publish this init's closures for the stable module-level delegators.
	_active = {
		setStyle,
		updateTrackColors: (colorMode: string, gpsArr?: GpsPoint[]): void => {
			TRK.updateTrackColors(colorMode, gpsArr);
		},
	};

	// ================================================================
	// MAP LOAD
	// ================================================================
	// The load handler has ONE job: apply terrain/hillshade for non-terrain
	// styles (terrain is already in the inline style) and draw the track if
	// GPS data was passed.
	//
	// Ordering invariant: any post-load work that registers once('idle') must
	// run AFTER _applyLightAndControls / addHillshadeAndLight have called
	// easeTo(), so the idle listener fires after the pitch animation settles,
	// not before it starts.
	// ================================================================

	map.once("load", () => {
		try {
			if (isTerrainStyle) {
				// Inline terrain style: terrain is already active in the style spec.
				// addHillshadeAndLight would call setTerrain again (restarting the
				// DEM fetch cycle) and add duplicate layers — skip it entirely.
				// Only add the fog/light/controls that addHillshadeAndLight normally adds.
				_applyLightAndControls(map, exaggeration);
			} else {
				// Vector style (streets/satellite): terrain sources are not in the
				// style, so we need addHillshadeAndLight to add them and call setTerrain.
				H.addHillshadeAndLight(map, exaggeration, false, _currentDemProvider);
			}

			if (gpsPoints && Array.isArray(gpsPoints) && gpsPoints.length > 0) {
				try {
					// MS-02: skip the initial fitBounds when a camera was restored, else it
					// would re-frame the whole track and override the restored viewport.
					TRK.addTrackToMap(gpsPoints, !!savedCam);
				} catch (e) {
					console.error("Error adding track", e);
				}
			}

			try {
				if (hooks?.setupHoverTracking) {
					hooks.setupHoverTracking();
				} else if (mapDispatcher) {
					mapDispatcher.setupProximityHover({
						onHighlight: (index: number) => hooks?.onTrackHover?.(index, "3dmap"),
						onHideHighlight: () => hooks?.onTrackLeave?.(),
					});
				}
			} catch (e) {
				console.warn("Failed to register MapLibre hover tracking:", e);
			}

			hooks?.onMapReady?.(map);

		} catch (e) {
			console.error("initMapLibreTerrain: error in load handler", e);
		}
	});

	setMaplibreMap(map);

	// Inject the page-state seams so the core adapter never reads window (CM-06):
	// track recoloring lives here (delegator), the rest come from the state modules.
	mapDispatcher.register(new MapLibreAdapter(map, {
		onUpdateTrackColors: (colorMode, data) => updateMaplibreTrackColors(colorMode, data),
		isBlueMarkerVisible,
		isReplayActive,
		getLockedSplit,
	}));

	return map;
}

// ====================================================================
// LIGHT / CONTROLS — applied for terrain style without full addHillshadeAndLight
// (which would call setTerrain again and restart the DEM fetch cycle).
// ====================================================================

function _applyLightAndControls(map: maplibregl.Map, exaggeration: number | undefined): void {
	const ex = exaggeration || H.terrainExaggeration();
	try {
		H.applySky(map);
	} catch (e: unknown) { console.warn('[maplibre-terrain] applySky failed:', e); }
	try {
		map.setLight({
			anchor: "viewport",
			position: [1.15, 15.0, 65.0],
			color: "white",
			intensity: 0.6,
		});
	} catch (e: unknown) { console.warn('[maplibre-terrain] setLight failed:', e); }
	// MP2-01: use the shared per-map WeakMap control-ensurer so a terrain-sentinel init
	// followed by a basemap switch can't add a second Nav/Terrain control pair.
	H.ensureNavAndTerrainControls(map, ex);
	// Apply pitch animation — this starts the idle timer that post-load
	// callbacks will wait for.
	try {
		map.easeTo({ pitch: H.TERRAIN_PITCH, duration: 800 });
	} catch (e: unknown) { console.warn('[maplibre-terrain] easeTo failed:', e); }
}
