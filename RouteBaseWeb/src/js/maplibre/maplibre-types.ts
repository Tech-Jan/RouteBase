/**
 * MapLibre slice contracts — module APIs for the shared MapLibre helpers, track
 * renderer, and replay camera controls.
 *
 * Formerly ambient globals (GodFileSplit).
 */

import type { GpsPoint } from '../core/core-types';

// ---------------------------------------------------------------------------
// MapLibre Helpers — shared terrain utilities (maplibre-helpers.ts)
// ---------------------------------------------------------------------------

export interface MapLibreHelpersNamespace {
	getApiKey(): string;
	getDemTilesUrl(): string | null;
	safeRemoveLayer(map: maplibregl.Map, id: string): void;
	safeRemoveSource(map: maplibregl.Map, id: string): void;
	stripBuiltInTerrain(styleJson: import("maplibre-gl").StyleSpecification): import("maplibre-gl").StyleSpecification;
	centerFromGpsData(gpsArray: GpsPoint[]): [number, number] | null;
	coordsFromGps(gpsArray: GpsPoint[]): [number, number][];
	buildInlineTerrainStyle(exaggeration: number): import("maplibre-gl").StyleSpecification | null;
	fetchStyleJson(url: string): Promise<import("maplibre-gl").StyleSpecification>;
	clearStyleCache(): void;
	/** The user's 3D relief setting. A call, never a captured const — see maplibre-helpers.ts. */
	terrainExaggeration(): number;
	TERRAIN_PITCH: number;
	DEFAULT_PITCH: number;
	addHillshadeAndLight(map: maplibregl.Map, exaggeration: number, skipPitchAnimation?: boolean, demProvider?: 'terrarium' | 'maptiler'): void;
	ensureNavAndTerrainControls(map: maplibregl.Map, exaggeration: number): void;
	destroyInstance(): void;
	MAP_MAX_ZOOM: number;
	MAP_MAX_PITCH: number;
	MAP_DEFAULT_REPLAY_ZOOM: number;
	SKY_CONFIG: Record<string, string | number>;
	FOG_CONFIG: Record<string, string | number | [number, number]>;
	applySky(map: maplibregl.Map): void;
	applyFog(map: maplibregl.Map): void;
	defaultMapOptions: {
		fadeDuration: number;
		refreshExpiredTiles: boolean;
		renderWorldCopies: boolean;
		maxZoom: number;
		maxPitch: number;
		attributionControl: { compact: boolean };
	};
	MAP_BACKGROUND_COLOR: string;
	applyMapBackground(map: maplibregl.Map): void;
}

// ---------------------------------------------------------------------------
// MapLibre Track — track rendering module (maplibre-track.ts)
// ---------------------------------------------------------------------------

export interface MapLibreTrackApi {
	bind(map: maplibregl.Map, trackBoundsMap: WeakMap<object, [[number, number], [number, number]]>): void;
	getCachedCoords(gpsArr: GpsPoint[]): [number, number][];
	addTrackToMap(gpsPointsArr: GpsPoint[], skipFitBounds?: boolean): void;
	updateTrackColors(colorMode: string, gpsArr?: GpsPoint[]): void;
	destroy(): void;
}
