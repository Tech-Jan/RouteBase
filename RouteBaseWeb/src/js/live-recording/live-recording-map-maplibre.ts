// live-recording-map-maplibre.ts — MapLibre engine for live recording map
// Manages per-person track lines and position dots.
// Exported as liveRecMapLibre; consumed by live-recording-map.ts.
import type { SavedCamera } from '../core/core-types';
import { must } from '../core/invariant';
import type { LiveRecMapLibreApi } from './live-recording-types';
import { helpers as H } from '../maplibre/maplibre-helpers';
import { installMissingImageFallback } from '../maplibre/missing-image-fallback';
import { installStyleFilterSanitizer } from '../maplibre/style-filter-sanitizer';
import { safeRemoveLayer, safeRemoveSource } from '../core/map-utils-shared';
import { demProviderFor } from '../core/basemap-registry';
import { swapStyleWithTerrain, cancelPendingSwap } from '../maplibre/maplibre-style-swap';

/** Zoom the camera adopts when it first locks onto a rider. flyToFirstPoint and the pages'
 *  setFollowTarget calls must agree, so the constant lives with the engine (re-exported by the facade). */
export const FOLLOW_ZOOM = 15;

interface PersonState {
	trackColor: string;
	dotColor: string;
	coords: [number, number][];
}

let _map: maplibregl.Map | null = null;
let _mapReady = false;
// Style-switch generation: the style JSON is fetched async, so a slow fetch from an EARLIER click can
// resolve after a later style was applied and silently revert the map (while the shell/buttons show the
// newer one, FE-09). Each setStyle claims a generation; only the newest may apply.
let _styleSwitchGen = 0;
const _pendingPersons: Map<string, PersonState> = new Map();

// DEM provider for the current basemap. Default 'terrarium' (keyless AWS) so 3D works with NO MapTiler
// key — the hard constraint on the anonymous public share (BT-06.3). init/setStyle route it from the
// basemap id, mirroring the shared maplibre-terrain engine's setMaplibreDemProvider.
let _currentDemProvider: 'terrarium' | 'maptiler' = 'terrarium';

// ─── Source / layer id helpers ───────────────────────────────────────────

function _trackSrcId(id: string): string { return `live-track-${id}`; }
function _trackLayId(id: string): string { return `live-track-line-${id}`; }
function _posSrcId(id: string): string   { return `live-pos-${id}`; }
function _posLayId(id: string): string   { return `live-pos-dot-${id}`; }

// ─── Add a person's layers to the current map ────────────────────────────

function _addPersonLayers(map: maplibregl.Map, personId: string, state: PersonState): void {
	const tSrc = _trackSrcId(personId);
	const tLay = _trackLayId(personId);
	const pSrc = _posSrcId(personId);
	const pLay = _posLayId(personId);

	if (!map.getSource(tSrc)) {
		const coords = state.coords.length > 0 ? state.coords : [];
		map.addSource(tSrc, {
			type: 'geojson',
			data: {
				type: 'Feature',
				geometry: { type: 'LineString', coordinates: coords },
				properties: {},
			},
		});
	}
	if (!map.getLayer(tLay)) {
		map.addLayer({
			id: tLay,
			type: 'line',
			source: tSrc,
			paint: {
				'line-color': state.trackColor,
				'line-width': 3,
				'line-opacity': 0.9,
			},
		});
	}

	const posCoord = state.coords.length > 0
		? must(state.coords[state.coords.length - 1], 'non-empty coords (guarded)')
		: [0, 0] as [number, number];

	if (!map.getSource(pSrc)) {
		map.addSource(pSrc, {
			type: 'geojson',
			data: {
				type: 'Feature',
				geometry: { type: 'Point', coordinates: posCoord },
				properties: {},
			},
		});
	}
	if (!map.getLayer(pLay)) {
		map.addLayer({
			id: pLay,
			type: 'circle',
			source: pSrc,
			paint: {
				'circle-radius': 8,
				'circle-color': state.dotColor,
				'circle-stroke-width': 2,
				'circle-stroke-color': '#ffffff',
			},
		});
	}
}

// ─── Remove a person's layers from the current map ───────────────────────

function _removePersonLayers(map: maplibregl.Map, personId: string): void {
	safeRemoveLayer(map, _trackLayId(personId));
	safeRemoveLayer(map, _posLayId(personId));
	safeRemoveSource(map, _trackSrcId(personId));
	safeRemoveSource(map, _posSrcId(personId));
}

// ─── Re-add all persons after a style change ─────────────────────────────

function _replayPersons(map: maplibregl.Map): void {
	_pendingPersons.forEach((state, id) => {
		_addPersonLayers(map, id, state);
	});
}

// ─── Terrain (keyless 3D, shared engine path) ────────────────────────────

/** Route the DEM provider from the chosen basemap id; keyless basemaps → terrarium (BT-06.3). */
function _routeDemProvider(basemapId?: string | null): void {
	_currentDemProvider = basemapId ? demProviderFor(basemapId) : 'terrarium';
}

/**
 * Apply 3D terrain for the current basemap — the same `addHillshadeAndLight` orchestration the
 * overview / segment-stats maps use, now shared by the live map instead of its old "default basemaps
 * stay 2D" workaround. It adds the DEM source + setTerrain + sky/light + nav/terrain controls, guarded
 * by the helper's per-map WeakMaps so a basemap switch never doubles a source or control.
 * `skipPitch` keeps a restored/switched camera flat; a fresh open eases into the terrain pitch.
 */
function _applyTerrain(map: maplibregl.Map, skipPitch: boolean): void {
	H.addHillshadeAndLight(map, H.terrainExaggeration(), skipPitch, _currentDemProvider);
}

// ─── Public API ──────────────────────────────────────────────────────────

// Annotated so future drift from LiveRecMapLibreApi fails `npx tsc --noEmit` (LR-09).
const api: LiveRecMapLibreApi = {
	// styleUrl is always a resolved URL (or the 'terrain' sentinel) — MapShell resolves it.
	init(containerId: string, styleUrl: string, cam?: SavedCamera | null, basemapId?: string | null): void {
		_routeDemProvider(basemapId);
		const isTerrain = styleUrl === 'terrain';
		const style: maplibregl.StyleSpecification | string = isTerrain
			? (H.buildInlineTerrainStyle(H.terrainExaggeration()) as maplibregl.StyleSpecification)
			: styleUrl;

		_map = new maplibregl.Map({
			container: containerId,
			style: style as maplibregl.StyleSpecification,
			center: cam ? cam.center : [0, 0],
			zoom: cam ? cam.zoom : 2,
			// 3D-by-default: a fresh open eases into the terrain pitch (load handler below); a bfcache
			// re-init restores the prior pitch/bearing here so Back returns to the exact viewport.
			pitch: cam ? cam.pitch : H.DEFAULT_PITCH,
			bearing: cam ? cam.bearing : 0,
			...H.defaultMapOptions,
		});

		H.applyMapBackground(_map);
		installMissingImageFallback(_map);
		installStyleFilterSanitizer(_map); // null-guard basemap numeric filters (poi rank / boundary admin_level)

		const map = _map; // narrowed rebind for the load closure (G3)
		map.on('load', () => {
			_mapReady = true;
			_replayPersons(map);
			// 3D on for every basemap (keyless terrarium DEM by default). A fresh open eases into the pitch;
			// a bfcache re-init keeps the camera restored above (skipPitch), so Back doesn't re-tilt.
			_applyTerrain(map, /* skipPitch */ !!cam);
		});
	},

	// The person store deliberately SURVIVES destroy(): MapShell's bfcache restore destroys the map and
	// re-inits it, and init's 'load' handler replays the retained persons onto the new map (pinned).
	// Dropping the riders' tracks on a bfcache return is not the fix for the leak — clearPersons() is.
	destroy(): void {
		if (!_map) return;
		const map = _map; // narrowed rebind for the forEach closure (G3)
		cancelPendingSwap(map); // drop any in-flight style.load/idle re-add before the map goes away
		_pendingPersons.forEach((_state, id) => {
			_removePersonLayers(map, id);
		});
		try { _map.remove(); } catch { /* ignore */ }
		_map = null;
		_mapReady = false;
	},

	/** Drop every rider's retained coordinates. The page teardown, not a map churn (LV-06). */
	clearPersons(): void {
		_pendingPersons.clear();
	},

	getMap(): maplibregl.Map | null {
		return _map;
	},

	isReady(): boolean {
		return _mapReady;
	},

	// Style switch: saves camera, then the shared swapStyleWithTerrain does the one correct sequence
	// (terrain off → strip → setStyle{diff:false} → re-add on style.load → 3D on idle). The old bespoke
	// path used a *diffing* setStyle, which killed 3D on switch AND wiped the rider track + dot with no
	// reliable style.load to re-add them; the shared helper is what the overview/segments maps use.
	setStyle(styleUrl: string, basemapId?: string | null): void {
		if (!_map) return;
		_routeDemProvider(basemapId);
		const gen = ++_styleSwitchGen;

		const cam = {
			center: _map.getCenter(),
			zoom: _map.getZoom(),
			bearing: _map.getBearing(),
			pitch: _map.getPitch(),
		};

		const isTerrain = styleUrl === 'terrain';

		// A stale fetch (an earlier click resolving late) must not clobber the newer style (FE-09);
		// _map can also be gone by now (teardown during the fetch). The same guard rides into the helper.
		const doSwap = (style: maplibregl.StyleSpecification | string): void => {
			if (gen !== _styleSwitchGen || !_map) return;
			swapStyleWithTerrain(_map, style, {
				generationOk: () => gen === _styleSwitchGen && !!_map,
				// Restore the camera + re-add every rider's track/dot the instant the clean rebuild loads.
				onStyleLoad: (map) => { map.jumpTo(cam); _replayPersons(map); },
				// 3D back on once the new basemap's tiles settle; skipPitch keeps the camera just restored.
				onIdle: (map) => _applyTerrain(map, /* skipPitch */ true),
			});
		};

		if (isTerrain) {
			const style = H.buildInlineTerrainStyle(H.terrainExaggeration());
			if (style) doSwap(style);
		} else {
			H.fetchStyleJson(styleUrl)
				.then((style) => doSwap(style))
				.catch(() => doSwap(styleUrl)); // fallback: let maplibre fetch directly
		}
	},

	addPerson(personId: string, trackColor: string, dotColor: string): void {
		const existing = _pendingPersons.get(personId);
		const state: PersonState = {
			trackColor,
			dotColor,
			coords: existing?.coords ?? [],
		};
		_pendingPersons.set(personId, state);
		if (_map && _mapReady) {
			_addPersonLayers(_map, personId, state);
		}
	},

	removePerson(personId: string): void {
		if (_map && _mapReady) {
			_removePersonLayers(_map, personId);
		}
		_pendingPersons.delete(personId);
	},

	updateTrack(personId: string, coords: [number, number][]): void {
		const state = _pendingPersons.get(personId);
		if (!state) return;
		state.coords = coords;

		if (!_map || !_mapReady) return;

		const tSrc = _map.getSource(_trackSrcId(personId)) as maplibregl.GeoJSONSource | undefined;
		if (tSrc) {
			tSrc.setData({
				type: 'Feature',
				geometry: { type: 'LineString', coordinates: coords },
				properties: {},
			});
		}

		if (coords.length > 0) {
			const last = must(coords[coords.length - 1], 'non-empty coords (guarded)');
			const pSrc = _map.getSource(_posSrcId(personId)) as maplibregl.GeoJSONSource | undefined;
			if (pSrc) {
				pSrc.setData({
					type: 'Feature',
					geometry: { type: 'Point', coordinates: last },
					properties: {},
				});
			}
		}
	},

	flyToFirstPoint(lat: number, lng: number): void {
		if (!_map) return;
		_map.flyTo({ center: [lng, lat], zoom: FOLLOW_ZOOM });
	},

	/** Last known [lng, lat] for a person, or null if none stored. */
	getLastPosition(personId: string): [number, number] | null {
		const coords = _pendingPersons.get(personId)?.coords ?? [];
		return coords.length > 0 ? must(coords[coords.length - 1], 'non-empty coords (guarded)') : null;
	},

	// LV-08: panTo / setDragCallback deleted — zero production callers; the follow camera
	// (CameraControls) replaced both. _clearPersonStore, which only a test called, became the named
	// clearPersons() above, with a real production caller (the facade's destroy).
};

export const liveRecMapLibre = api;
