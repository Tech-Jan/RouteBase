/**
 * core/basemap-registry.ts — Single source of truth for map basemaps/styles.
 *
 * ID-07 migration step 1. Replaces the hardcoded MapTiler URLs scattered across
 * map-shell and every map page with one registry. Adding a basemap = one entry
 * here; removing one = deleting its row.
 *
 * Design (see analysis-core-map.md CM-01):
 *   • Every entry resolves to a URL string so the existing styleUrl plumbing
 *     (switchToMapLibre, fetchStyleJson, page createMapLibre callbacks) is untouched.
 *   • The 'terrain' entry resolves to the literal 'terrain' sentinel — pages turn
 *     that into an inline DEM style via buildInlineTerrainStyle.
 *   • Legacy ids ('streets'/'satellite'/'terrain') are kept so stored profile
 *     DefaultMapStyle values keep resolving. (helpers.getStyleUrl was removed in
 *     MP2-02 — resolveStyleUrl below is now the sole URL/key resolver.)
 *   • The MapTiler key is read at call time (never at module scope) so page load
 *     order cannot bite. It comes from the core/map-api-key seam (setMapTilerApiKey
 *     at page boot), not a window global — keyless pages read its '' default.
 */

import { getMapTilerApiKey } from './map-api-key';

export interface BasemapEntry {
    /** Stable, grep-able id. Legacy ids kept (see seed). */
    id: string;
    /** Button text. */
    label: string;
    kind: 'vector' | 'raster' | 'terrain';
    /** MapLibre style JSON URL; '{key}' placeholder; literal 'terrain' sentinel for kind 'terrain'. */
    styleUrl: string;
    /** true → entry hidden / unresolvable when no MapTiler key. */
    keyRequired?: boolean;
    /** DEM provider for 3D terrain. 'maptiler' → key-gated MapTiler DEM; absent → keyless terrarium (BT-03). */
    dem?: 'maptiler';
}

/** Full seed, unfiltered, in display order. */
export const BASEMAPS: readonly BasemapEntry[] = [
    { id: 'liberty',       label: 'Liberty',     kind: 'vector',  styleUrl: 'https://tiles.openfreemap.org/styles/liberty' },
    { id: 'bright',        label: 'Bright',      kind: 'vector',  styleUrl: 'https://tiles.openfreemap.org/styles/bright' },
    { id: 'positron',      label: 'Muted',       kind: 'vector',  styleUrl: 'https://tiles.openfreemap.org/styles/positron' },
    { id: 'osm-raster',    label: 'OSM',         kind: 'raster',  styleUrl: '/map-styles/osm-raster.style.json' },
    { id: 'satellite-esri', label: 'Satellite',  kind: 'raster',  styleUrl: '/map-styles/esri-satellite.style.json' },
    { id: 'streets',       label: 'Streets (MT api)',    kind: 'vector',  styleUrl: 'https://api.maptiler.com/maps/streets/style.json?key={key}', keyRequired: true, dem: 'maptiler' },
    { id: 'satellite',     label: 'Satellite (MT api)',  kind: 'vector',  styleUrl: 'https://api.maptiler.com/maps/hybrid/style.json?key={key}',  keyRequired: true, dem: 'maptiler' },
    { id: 'terrain',       label: '3D Terrain (MT api)', kind: 'terrain', styleUrl: 'terrain', keyRequired: true, dem: 'maptiler' },
];

/** True when a MapTiler API key is configured on the page. Read at call time. */
export function hasMapTilerKey(): boolean {
    return !!getMapTilerApiKey();
}

/** BASEMAPS minus keyRequired entries when no key is configured. */
export function availableBasemaps(): BasemapEntry[] {
    const keyed = hasMapTilerKey();
    return BASEMAPS.filter(b => keyed || !b.keyRequired);
}

/** Look up an entry by id; null when unknown. */
export function getBasemap(id: string): BasemapEntry | null {
    return BASEMAPS.find(b => b.id === id) ?? null;
}

/**
 * Resolve an entry id to a usable style URL string.
 * Substitutes '{key}' (encodeURIComponent); returns the 'terrain' sentinel for
 * the terrain entry; null for an unknown id or a keyRequired entry without a key.
 */
export function resolveStyleUrl(id: string): string | null {
    const entry = getBasemap(id);
    if (!entry) return null;
    if (entry.keyRequired && !hasMapTilerKey()) return null;
    if (entry.styleUrl.indexOf('{key}') === -1) return entry.styleUrl;
    return entry.styleUrl.replace('{key}', encodeURIComponent(getMapTilerApiKey()));
}

/**
 * DEM provider for an entry's 3D terrain: 'maptiler' iff the entry declares
 * dem: 'maptiler', else 'terrarium' (keyless AWS). Unknown ids → 'terrarium'.
 */
export function demProviderFor(id: string): 'terrarium' | 'maptiler' {
    return getBasemap(id)?.dem === 'maptiler' ? 'maptiler' : 'terrarium';
}

/**
 * Resolve the default basemap id: the profile default ('streets'|'satellite'|
 * 'terrain' from Model.DefaultMapStyle) when it resolves, else 'liberty'.
 */
export function defaultBasemapId(profileDefault?: string): string {
    if (profileDefault && resolveStyleUrl(profileDefault)) return profileDefault;
    return 'liberty';
}
