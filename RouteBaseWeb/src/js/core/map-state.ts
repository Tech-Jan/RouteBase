/**
 * map-state.ts — shared MapLibre mutable state (the map layer's bottom layer; imports nothing).
 *
 * ARCH-F1: this is the genuine lowest layer of the map engine — a leaf that BOTH `core/` and
 * `maplibre/` depend on DOWNWARD, so neither directory has to reach up into the other for live map
 * state. It moved here from `maplibre/maplibre-state.ts` to kill the sharpest cross-edge
 * (`core/map-utils-maplibre.ts` used to import UP into `maplibre/` for the replay flags). The old
 * path was a temporary re-export shim; a 2026-07-31 refactor repointed all 26 importers across
 * activity/, segment-stats/, track-edit/ and maplibre/, and the shim is **deleted** — this is now the
 * only path. The one-way core↛maplibre rule is enforced by eslint `no-restricted-imports`
 * (eslint.config.mjs → "MAP-ENGINE LAYERING"), which also documents it in full.
 *
 * Replaces the window.* globals that carried live map state across the maplibre, core
 * (map-utils-maplibre) and page bundles. Slots are read at call time, exactly like the former window
 * reads. Cross-bundle sharing works because Rollup emits this leaf as one chunk deduped per page
 * (module cache), so every importer sees the same instance.
 */

type MaplibreMap = maplibregl.Map;
type MaplibreMarker = maplibregl.Marker;
type MaplibreControl = maplibregl.IControl;

// Active MapLibre map instance (was window.maplibreInstance).
let _map: MaplibreMap | null = null;
export function getMaplibreMap(): MaplibreMap | null { return _map; }
export function setMaplibreMap(map: MaplibreMap | null): void { _map = map; }

// Blue highlight marker for the replay visibility toggle (was window.maplibreCurrentMarker).
let _currentMarker: MaplibreMarker | null = null;
export function getCurrentMarker(): MaplibreMarker | null { return _currentMarker; }
export function setCurrentMarker(marker: MaplibreMarker | null): void { _currentMarker = marker; }

// Whether the blue hover marker should be visible (was window._blueMarkerShouldBeVisible;
// default-true semantics: the old reader tested `!== false`, so unset meant visible).
let _blueMarkerVisible = true;
export function isBlueMarkerVisible(): boolean { return _blueMarkerVisible; }
export function setBlueMarkerVisible(visible: boolean): void { _blueMarkerVisible = visible; }

// Whether a Details replay is running — suppresses hover updates (was window.isReplayActive).
let _replayActive = false;
export function isReplayActive(): boolean { return _replayActive; }
export function setReplayActive(active: boolean): void { _replayActive = active; }

// Nav / Terrain control refs for the `terrain`-sentinel path, which has no per-map key for
// the maplibre-helpers WeakMaps (was window._maplibreNavControl / _maplibreTerrainControl).
let _navControl: MaplibreControl | null = null;
export function getNavControl(): MaplibreControl | null { return _navControl; }
export function setNavControl(control: MaplibreControl | null): void { _navControl = control; }

let _terrainControl: MaplibreControl | null = null;
export function getTerrainControl(): MaplibreControl | null { return _terrainControl; }
export function setTerrainControl(control: MaplibreControl | null): void { _terrainControl = control; }
