// maplibre-helpers.ts — Shared MapLibre utility functions
// Used by maplibre-terrain.ts and other modules that create MapLibre maps.
// Loaded BEFORE maplibre-terrain.ts (either as a static import or explicit <script type="module">).
import type { GpsPoint } from '../core/core-types';
import type { MapLibreHelpersNamespace } from './maplibre-types';
import { safeRemoveLayer, safeRemoveSource } from '../core/map-utils-shared';
import { getMapTilerApiKey } from '../core/map-api-key';
import { mapDispatcher } from '../core/map-dispatcher';
import { terrariumDemSource } from './terrarium-dem';
import { sanitizeStyleFilters } from './style-filter-sanitizer';
import { getAccountSettings } from '../core/account-settings';
import {
	getMaplibreMap, setMaplibreMap, getCurrentMarker, setCurrentMarker,
	getNavControl, setNavControl, getTerrainControl, setTerrainControl,
} from '../core/map-state';

type StyleSpec = import("maplibre-gl").StyleSpecification;
type RasterDemSpec = import("maplibre-gl").RasterDEMSourceSpecification;
export const helpers = {} as MapLibreHelpersNamespace;

	// ====================================================================
	// API KEY
	// ====================================================================

	helpers.getApiKey = (): string => getMapTilerApiKey();

	helpers.getDemTilesUrl = (): string | null => {
		const apiKey = helpers.getApiKey();
		if (!apiKey) {
			console.warn("MapTiler API key not configured. 3D terrain will not work.");
			return null;
		}
		return "https://api.maptiler.com/tiles/terrain-rgb/tiles.json?key=" + apiKey;
	};

	// MP2-02: helpers.getStyleUrl removed — it duplicated basemap-registry's URL/key
	// substitution and its only callers (falsy-styleUrl fallbacks) were unreachable.
	// core/basemap-registry.resolveStyleUrl is the single source of truth for style URLs.

	// ====================================================================
	// SAFE LAYER/SOURCE REMOVAL
	// ====================================================================

	helpers.safeRemoveLayer = (map: maplibregl.Map, id: string): void => {
		safeRemoveLayer(map, id);
	};

	helpers.safeRemoveSource = (map: maplibregl.Map, id: string): void => {
		safeRemoveSource(map, id);
	};

	// ====================================================================
	// STYLE JSON SANITIZATION
	// ====================================================================

	/**
	 * Strip MapTiler's built-in terrain config AND raster-dem sources from a
	 * fetched style JSON object.  Also removes any layers that reference those
	 * sources (typically hillshade layers MapTiler embeds).
	 *
	 * Why: MapTiler's vector styles (streets, hybrid, etc.) ship with their own
	 * raster-dem source.  If we only `delete json.terrain` the DEM source stays
	 * alive — MapLibre still fetches those tiles even though nothing uses them
	 * via setTerrain.  When we then addSource('terrainSource', ...) we end up
	 * with TWO raster-dem sources competing for the same DEM tiles, doubling
	 * GPU/network work.
	 *
	 * Call this on any style JSON fetched from MapTiler BEFORE passing it to
	 * `new maplibregl.Map({ style: ... })` or `map.setStyle(...)`.
	 *
	 * @param styleJson  Mutable style JSON object
	 * @returns The same object (mutated), for chaining
	 */
	helpers.stripBuiltInTerrain = (styleJson: StyleSpec): StyleSpec => {
		if (!styleJson || typeof styleJson !== "object") return styleJson;

		// Remove terrain config
		delete styleJson.terrain;

		// Remove viewport overrides that the style embeds
		delete styleJson.center;
		delete styleJson.zoom;
		delete styleJson.bearing;
		delete styleJson.pitch;

		// Find all raster-dem source IDs
		const demSourceIds: string[] = [];
		const sources = styleJson.sources as Record<string, { type?: string }> | undefined;
		if (sources) {
			for (const [key, src] of Object.entries(sources)) {
				if (src && src.type === "raster-dem") demSourceIds.push(key);
			}
		}

		if (demSourceIds.length === 0) return styleJson;

		// Remove layers that reference any of those DEM sources
		if (Array.isArray(styleJson.layers)) {
			styleJson.layers = (styleJson.layers as { source?: string }[]).filter(
				(layer) =>
					!layer || !layer.source || demSourceIds.indexOf(layer.source) === -1,
			) as unknown as StyleSpec["layers"];
		}

		// Remove the DEM sources themselves
		if (sources) {
			for (const id of demSourceIds) delete sources[id];
		}

		return styleJson;
	};

	// ====================================================================
	// FOG — limits horizon tile fetching at low pitch angles
	// ====================================================================

	// NOTE: applyFog is defined later alongside applySky as a backwards-
	// compatible alias.  The standalone definition that was here has been
	// removed to avoid confusion with the v4/v5 split below.

	// ====================================================================
	// GPS DATA NORMALIZATION
	// ====================================================================

	/** Derive center [lng, lat] from gpsData array of {lat, lng, ...} */
	helpers.centerFromGpsData = (gpsArray: GpsPoint[]): [number, number] | null => {
		if (!Array.isArray(gpsArray) || gpsArray.length === 0) return null;
		// Pick the first numeric of the two spellings; null means the point has no usable
		// coordinate. Folding filter+map into one pass lets the null check narrow to number,
		// which the old filter-then-map pair could not carry across the callback boundary.
		const num = (a: unknown, b: unknown): number | null =>
			typeof a === "number" ? a : typeof b === "number" ? b : null;

		const valid: { lat: number; lng: number }[] = [];
		for (const p of gpsArray) {
			if (!p) continue;
			const lat = num(p.lat, p.latitude);
			const lng = num(p.lng, p.longitude);
			if (lat === null || lng === null) continue;
			valid.push({ lat, lng });
		}
		if (valid.length === 0) return null;

		let sumLat = 0,
			sumLng = 0;
		for (const v of valid) {
			sumLat += v.lat;
			sumLng += v.lng;
		}
		return [sumLng / valid.length, sumLat / valid.length];
	};

	/** Normalize GPS array -> array of [lng, lat] coordinates */
	helpers.coordsFromGps = (gpsArray: GpsPoint[]): [number, number][] => {
		if (!Array.isArray(gpsArray) || gpsArray.length === 0) return [];
		return gpsArray
			.map((p) => {
				const lat = typeof p.lat === "number" ? p.lat : p.latitude;
				const lng = typeof p.lng === "number" ? p.lng : p.longitude;
				return [lng, lat] as [number, number];
			})
			.filter((c) => Array.isArray(c) && c.length === 2 && !isNaN(c[0]) && !isNaN(c[1]));
	};

	// ====================================================================
	// INLINE TERRAIN STYLE BUILDER
	// ====================================================================

	const _inlineStyleCache: { [key: string]: StyleSpec } = {};

	helpers.buildInlineTerrainStyle = (exaggeration: number): StyleSpec | null => {
		const ex = exaggeration || helpers.terrainExaggeration();

		// Always hand back a deep clone, never the cached object itself. MapLibre mutates the style it
		// receives (injects internal source/layer state), so returning the cached canonical object would
		// corrupt it and the next terrain init would build from a broken style.
		if (_inlineStyleCache[ex]) return JSON.parse(JSON.stringify(_inlineStyleCache[ex])) as StyleSpec;

		const demUrl = helpers.getDemTilesUrl();
		if (!demUrl) {
			console.error("Cannot build terrain style: API key missing");
			return null;
		}

		const hillshadeExagInline: number =
			getAccountSettings().map.hillshadeExaggeration;

		const inlineLayers: StyleSpec["layers"] = [{ id: "osm", type: "raster", source: "osm" }];
		if (hillshadeExagInline > 0) {
			inlineLayers.push({
				id: "hills",
				type: "hillshade",
				source: "terrainSource",
				layout: { visibility: "visible" },
				paint: { "hillshade-shadow-color": "#473B24" },
			});
		}

		const style = {
			version: 8,
			sources: {
				osm: {
					type: "raster",
					tiles: [
						"https://a.tile.openstreetmap.org/{z}/{x}/{y}.png",
						"https://b.tile.openstreetmap.org/{z}/{x}/{y}.png",
						"https://c.tile.openstreetmap.org/{z}/{x}/{y}.png",
					],
					tileSize: 256,
					attribution: "&copy; OpenStreetMap contributors",
					maxzoom: 19,
				},
				terrainSource: { type: "raster-dem", url: demUrl, tileSize: 256 },
			},
			layers: inlineLayers,
			terrain: { source: "terrainSource", exaggeration: ex },
		};
		_inlineStyleCache[ex] = style as unknown as StyleSpec;
		return JSON.parse(JSON.stringify(style)) as StyleSpec;
	};

	// ====================================================================
	// STYLE JSON CACHE + REQUEST COUNTER
	// ====================================================================

	let _styleJsonCache: { [url: string]: StyleSpec } = {};

	/**
	 * Fetch a MapTiler style JSON, strip built-in terrain/DEM sources, and
	 * cache the result by URL.  Returns a deep clone on cache hit so MapLibre
	 * cannot corrupt the cached object.
	 *
	 * On network or parse error the returned Promise rejects — callers should
	 * fall back to bare `map.setStyle(url)`.
	 *
	 * @param url  Full MapTiler style.json URL
	 * @returns    Deep-cloned, stripped style object
	 */
	helpers.fetchStyleJson = (url: string): Promise<StyleSpec> => {
		if (_styleJsonCache[url]) {
			return Promise.resolve(JSON.parse(JSON.stringify(_styleJsonCache[url])));
		}
		return fetch(url)
			.then((res) => {
				if (!res.ok) return Promise.reject("HTTP " + res.status);
				return res.json();
			})
			.then((json: StyleSpec) => {
				helpers.stripBuiltInTerrain(json);
				sanitizeStyleFilters(json); // null-guard basemap numeric filters (poi rank / boundary admin_level)
				_styleJsonCache[url] = json;
				return JSON.parse(JSON.stringify(json)) as StyleSpec;
			});
	};

	/** Clear both style caches (e.g. after an API key or hillshade-setting change). */
	helpers.clearStyleCache = (): void => {
		_styleJsonCache = {};
		// MP2-03: the inline terrain style bakes in hillshadeExaggeration at build time,
		// so it must be cleared too or a settings change keeps the stale hillshade decision.
		for (const k in _inlineStyleCache) delete _inlineStyleCache[k];
	};

	// ====================================================================
	// TERRAIN & HILLSHADE
	// ====================================================================

	const TERRAIN_PITCH = 10;
	const DEFAULT_PITCH = 0;

	// A FUNCTION, not the const it replaced: account settings must be read at CALL time. maplibre-terrain.ts
	// captured this one at module scope, which would now pin every map to the default — initPageSettings()
	// fills the seam after module eval.
	helpers.terrainExaggeration = (): number => getAccountSettings().map.terrainExaggeration;
	helpers.TERRAIN_PITCH = TERRAIN_PITCH;
	helpers.DEFAULT_PITCH = DEFAULT_PITCH;

	// Tracks which DEM provider each map's terrainSource was created for, so a
	// basemap switch to the other provider re-adds the source. Source id stays
	// 'terrainSource' in both cases (invariant 5).
	const _terrainProviderByMap = new WeakMap<object, 'terrarium' | 'maptiler'>();

	// Nav/TerrainControl tracked per map instance (not via window globals): the
	// overview/live-recording/segment-create pages each build their own map and
	// don't share window.maplibreInstance, so a window-scoped guard would let the
	// first map claim the control and starve the rest of a 3D toggle (BT-06 edge case).
	const _navControlByMap = new WeakMap<maplibregl.Map, maplibregl.IControl>();
	const _terrainControlByMap = new WeakMap<maplibregl.Map, maplibregl.IControl>();

	// MP2-01: single control-ensuring path keyed on the per-map WeakMaps. Both the
	// vector-style init (addHillshadeAndLight) and the terrain-sentinel init
	// (maplibre-terrain._applyLightAndControls) call this, so a page that inits on the
	// `terrain` sentinel then switches basemap no longer gets a second Nav/Terrain control.
	helpers.ensureNavAndTerrainControls = (map: maplibregl.Map, exaggeration: number): void => {
		const ex = exaggeration || helpers.terrainExaggeration();
		try {
			if (!_navControlByMap.get(map) && map.addControl) {
				const nav = new maplibregl.NavigationControl({
					visualizePitch: true,
					showZoom: false,
					showCompass: true,
				});
				map.addControl(nav, "top-left");
				_navControlByMap.set(map, nav);
			}
		} catch (e) {
			console.warn("NavigationControl failed", e);
		}

		try {
			if (
				!_terrainControlByMap.get(map) &&
				map.addControl &&
				typeof maplibregl.TerrainControl !== "undefined"
			) {
				const terrain = new maplibregl.TerrainControl({
					source: "terrainSource",
					exaggeration: ex,
				});
				map.addControl(terrain, "top-left");
				_terrainControlByMap.set(map, terrain);
			}
		} catch (e) {
			console.warn("TerrainControl failed", e);
		}
	};

	helpers.addHillshadeAndLight = (
		map: maplibregl.Map,
		exaggeration: number,
		skipPitchAnimation?: boolean,
		demProvider: 'terrarium' | 'maptiler' = 'terrarium',
	): void => {
		const ex = exaggeration || helpers.terrainExaggeration();
		try {
			// Resolve the DEM source spec by provider. MapTiler stays key-gated —
			// no key → hard return (today's behavior). Terrarium is keyless (BT-04).
			let demSourceSpec: RasterDemSpec;
			if (demProvider === "maptiler") {
				const demUrl = helpers.getDemTilesUrl();
				if (!demUrl) {
					console.error("Cannot add terrain: API key missing");
					return;
				}
				demSourceSpec = { type: "raster-dem", url: demUrl, tileSize: 256 };
			} else {
				demSourceSpec = terrariumDemSource();
			}

			// Provider switch: a terrainSource created for the other provider must
			// be removed + re-added so the DEM matches the active basemap.
			const recordedProvider = _terrainProviderByMap.get(map);
			if (map.getSource("terrainSource") && recordedProvider && recordedProvider !== demProvider) {
				try {
					map.setTerrain(null);
				} catch (_e) { /* terrain may already be cleared */ }
				helpers.safeRemoveLayer(map, "hillshade");
				helpers.safeRemoveSource(map, "terrainSource");
			}

			if (!map.getSource("terrainSource")) {
				try {
					map.addSource("terrainSource", demSourceSpec);
					_terrainProviderByMap.set(map, demProvider);
				} catch (e) {
					console.warn("addSource terrainSource failed", e);
				}
			}

			// setTerrain guard: call it only when terrain is not already active, or when the
			// exaggeration actually changed. Re-calling setTerrain on an already-terrain-enabled map —
			// even with identical params — restarts MapLibre's internal DEM tile-load cycle, which then
			// permanently prevents `idle` from firing (post-load callbacks that wait on idle hang). Never
			// call map.setTerrain() directly from outside this file; go through here.
			let existingTerrain: { source?: string; exaggeration?: number } | null = null;
			try {
				existingTerrain = map.getTerrain() as { source?: string; exaggeration?: number } | null;
			} catch (_e) { /* teardown: getTerrain may not exist in older builds */ }
			if (!existingTerrain) {
				try {
					map.setTerrain({ source: "terrainSource", exaggeration: ex });
				} catch (e) {
					console.warn("map.setTerrain failed", e);
				}
			} else {
				if (existingTerrain.exaggeration !== ex) {
					try {
						map.setTerrain({
							source: existingTerrain.source || "terrainSource",
							exaggeration: ex,
						});
					} catch (e: unknown) { console.warn('[maplibre-helpers] setTerrain exaggeration update failed:', e); }
				}
			}

			// Skip hillshade layer entirely when exaggeration is 0 — DEM source is
			// still needed for the 3D mesh but the render pass is pure overhead.
			const hillshadeExag: number =
				getAccountSettings().map.hillshadeExaggeration;

			if (hillshadeExag > 0) {
				if (!map.getLayer("hillshade")) {
					try {
						map.addLayer({
							id: "hillshade",
							type: "hillshade",
							source: "terrainSource",
							paint: {
								"hillshade-exaggeration": hillshadeExag,
								"hillshade-highlight-color": "#fff",
								"hillshade-shadow-color": "#000",
								"hillshade-accent-color": "#6e6e6e",
							},
						});
					} catch (e) {
						console.warn("addLayer hillshade failed", e);
					}
				} else {
					try {
						map.setPaintProperty("hillshade", "hillshade-exaggeration", hillshadeExag);
					} catch (e: unknown) { console.warn('[maplibre-helpers] setPaintProperty hillshade-exaggeration failed:', e); }
				}
			} else {
				helpers.safeRemoveLayer(map, "hillshade");
			}

			try {
				helpers.applySky(map);
			} catch (e: unknown) { console.warn('[maplibre-helpers] applySky failed:', e); }

			try {
				map.setLight({
					anchor: "viewport",
					position: [1.15, 15.0, 65.0],
					color: "white",
					intensity: 0.6,
				});
			} catch (e) {
				console.warn("map.setLight failed", e);
			}

			helpers.ensureNavAndTerrainControls(map, ex);

			if (!skipPitchAnimation) {
				try {
					map.easeTo({ pitch: TERRAIN_PITCH, duration: 800 });
				} catch (e) {
					console.warn("map.easeTo pitch failed", e);
				}
			}
		} catch (e) {
			console.warn("addHillshadeAndLight error", e);
		}
	};

	// MP2-04: helpers.removeHillshadeAndTerrain deleted — 100% dead (no caller; every
	// "turn off 3D" flow goes through setStyle's full swap-and-rebuild) and internally
	// buggy (removed only the terrain control, leaking the nav control). A future explicit
	// "terrain off" UX should reuse the per-map WeakMap pattern via ensureNavAndTerrainControls.

	// ====================================================================
	// DESTROY
	// ====================================================================

	helpers.destroyInstance = (): void => {
		try {
			if (mapDispatcher && mapDispatcher.activeEngine() === "maplibre") {
				mapDispatcher._adapter?.destroy();
			}
		} catch (_e) { /* teardown: adapter may already be destroyed */ }

		const map = getMaplibreMap();
		if (!map) {
			return;
		}

		try {
			const navCtl = _navControlByMap.get(map) || getNavControl();
			if (navCtl && map.removeControl) {
				map.removeControl(navCtl);
				_navControlByMap.delete(map);
				setNavControl(null);
			}
		} catch (_e) { /* teardown: control may already be removed */ }
		try {
			const terrainCtl = _terrainControlByMap.get(map) || getTerrainControl();
			if (terrainCtl && map.removeControl) {
				map.removeControl(terrainCtl);
				_terrainControlByMap.delete(map);
				setTerrainControl(null);
			}
		} catch (_e) { /* teardown: control may already be removed */ }

		const knownLayers = ["track-line", "track-start", "track-end", "hillshade", "sky", "hills"];
		const knownSources = [
			"track",
			"track-start-src",
			"track-end-src",
			"dem",
			"terrainSource",
			"hillshadeSource",
		];

		try {
			const style = map.getStyle ? map.getStyle() : null;
			if (style && Array.isArray(style.layers)) {
				style.layers.forEach((layer) => {
					if (!layer || !layer.id) return;
					const id = layer.id;
					if (
						knownLayers.indexOf(id) !== -1 ||
						id.startsWith("track") ||
						id.toLowerCase().includes("hillshade") ||
						id.toLowerCase().includes("terrain") ||
						id === "sky"
					) {
						helpers.safeRemoveLayer(map, id);
					}
				});
			}
		} catch (_e) { /* teardown: style may be unavailable during destroy */ }

		knownSources.forEach((s) => {
			helpers.safeRemoveSource(map, s);
		});

		try {
			const style2 = map.getStyle ? map.getStyle() : null;
			if (style2 && style2.sources) {
				Object.keys(style2.sources).forEach((srcId: string) => {
					if (srcId.indexOf("track") === 0 || knownSources.indexOf(srcId) !== -1) {
						helpers.safeRemoveSource(map, srcId);
					}
				});
			}
		} catch (_e) { /* teardown: style may be unavailable during destroy */ }

		try {
			const cont = map.getContainer && map.getContainer();
			if (cont && cont.parentNode) cont.innerHTML = "";
		} catch (_e) { /* teardown: container may already be detached */ }

		try {
			map.remove();
		} catch (e) {
			console.warn("Error calling map.remove()", e);
		}

		try {
			const marker = getCurrentMarker();
			if (marker) {
				marker.remove();
			}
		} catch (_e) { /* teardown: marker may already be removed */ }
		setCurrentMarker(null);
		setMaplibreMap(null);

	};

	// ====================================================================
	// MAP INSTANCE DEFAULTS
	// ====================================================================
	// Shared constants for every maplibregl.Map() instance.
	// Consumer code reads helpers.defaultMapOptions and helpers.MAP_*
	// instead of hardcoding values — single place to tune.

	helpers.MAP_MAX_ZOOM = 18;
	helpers.MAP_MAX_PITCH = 85;
	helpers.MAP_DEFAULT_REPLAY_ZOOM = 13;

	// Sky config applied after every style load to limit horizon tile fetching.
	// MapLibre v5 replaced setFog() with setSky(). The two APIs accept
	// different property names, so we keep separate configs.
	helpers.SKY_CONFIG = {
		"sky-color": "#c9d8e8",
		"fog-color": "rgba(220, 230, 240, 0.8)",
		"fog-ground-blend": 0.5,
	};

	// v4 setFog() config (different property set)
	helpers.FOG_CONFIG = {
		range: [1, 10],
		color: "rgba(220, 230, 240, 0.8)",
		"horizon-blend": 0.1,
	};

	/** Apply sky/fog to a MapLibre map instance (v5 setSky, v4 setFog fallback) */
	helpers.applySky = (map: maplibregl.Map): void => {
		// Respect the fogDistance profile setting.
		// 0 = fog disabled; any positive value = far range in map units.
		const fogDist: number = getAccountSettings().map.fogDistance;

		try {
			if (fogDist === 0) {
				// Disable fog/sky completely
				if (typeof map.setSky === "function") {
					map.setSky({});
				// eslint-disable-next-line @typescript-eslint/no-explicit-any
				} else if (typeof (map as any).setFog === "function") {
					// eslint-disable-next-line @typescript-eslint/no-explicit-any
					(map as any).setFog(null);
				}
				return;
			}

			if (typeof map.setSky === "function") {
				const skyConfig = Object.assign({}, helpers.SKY_CONFIG);
				skyConfig["fog-ground-blend"] = Math.max(0.1, Math.min(1.0, fogDist / 20));
				map.setSky(skyConfig);
			// eslint-disable-next-line @typescript-eslint/no-explicit-any
			} else if (typeof (map as any).setFog === "function") {
				const fogConfig = Object.assign({}, helpers.FOG_CONFIG);
				fogConfig.range = [Math.max(0.5, fogDist * 0.1), fogDist];
				// eslint-disable-next-line @typescript-eslint/no-explicit-any
				(map as any).setFog(fogConfig);
			}
		} catch (e: unknown) { console.warn('[maplibre-helpers] applySky/setFog failed:', e); }
	};

	// Backwards-compatible alias
	helpers.applyFog = helpers.applySky;

	//   fadeDuration: 0
	//     The 300ms default crossfade creates a window where old tiles fade
	//     out while new ones fade in.  Setting to 0 removes the crossfade.
	//
	//   refreshExpiredTiles:
	//     Always false (expired tiles are kept and re-used without revalidation).
	//
	//   renderWorldCopies: false
	//     Skip rendering world copies at low zoom.

	helpers.defaultMapOptions = (() => ({
		fadeDuration: 0,
		refreshExpiredTiles: false,
		renderWorldCopies: false,
		maxZoom: helpers.MAP_MAX_ZOOM,
		maxPitch: helpers.MAP_MAX_PITCH,
		// Always collapse the attribution bar to the ⓘ button (compact never
		// auto-expands on wide screens).
		attributionControl: { compact: true },
	}))();

	// Background colour applied to the WebGL canvas and its container div.
	// When terrain DEM tiles haven't loaded yet, MapLibre renders nothing for
	// those pixels — the canvas background shows through.  Matching it to
	// the fog/sky colour (same as setFog's color below) makes the gaps
	// blend in rather than flashing white.
	helpers.MAP_BACKGROUND_COLOR = "#c9d8e8";

	helpers.applyMapBackground = (map: maplibregl.Map): void => {
		try {
			const canvas = map.getCanvas && map.getCanvas();
			if (canvas) canvas.style.background = helpers.MAP_BACKGROUND_COLOR;
			const container = map.getContainer && map.getContainer();
			if (container) container.style.background = helpers.MAP_BACKGROUND_COLOR;
		} catch (e: unknown) { console.warn('[maplibre-helpers] applyMapBackground failed:', e); }
	};
