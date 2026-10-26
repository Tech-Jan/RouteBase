/**
 * Live Recording (recording slice) map contracts — public + internal engine APIs
 * for the LiveView map modules.
 *
 * Formerly ambient globals (GodFileSplit).
 */

import type { MapShellConfig, SavedCamera } from '../core/core-types';

/** Public API for the LiveView map module (live-recording-map.ts, MapShell-backed, MapLibre-only). */
export interface LiveRecordingMapApi {
	/** Create the map + registry style switcher + size controls via MapShell. */
	init(opts: { containerId: string; styleSwitcherId: string; sizeControls?: MapShellConfig['sizeControls'] }): void;
	/** Destroy the MapShell and clear all state. */
	destroy(): void;
	/** True once the MapLibre engine has finished loading. */
	isReady(): boolean;
	/** Register a person track (call after init + isReady). */
	addPerson(personId: string, trackColor: string, dotColor: string): void;
	/** Remove a person's track layers. */
	removePerson(personId: string): void;
	/** Replace a person's track with new coords ([lng, lat] pairs). */
	updateTrack(personId: string, coords: [number, number][]): void;
	/** Fly / zoom to first GPS fix. */
	flyToFirstPoint(lat: number, lng: number): void;
	/**
	 * Follow one person's live position, or `null` for a free camera (segment-replay parity):
	 * while following, the camera's CENTRE belongs to the followed person — panning is off, so
	 * dragging no longer breaks follow, while pinch/twist/tilt keep working over them. Unfollow is
	 * explicit. Frames itself from the map's current camera, so switching target never jumps the view —
	 * pass `zoom` to override that (the first lock-on zooms in from the world view).
	 */
	setFollowTarget(personId: string | null, opts?: { zoom?: number }): void;
	/** The currently followed person, or null in free mode. */
	getFollowTarget(): string | null;
}

/** Internal MapLibre engine API for live recording (live-recording-map-maplibre.ts). */
export interface LiveRecMapLibreApi {
	/** styleUrl is a resolved URL or the 'terrain' sentinel; cam restores center/zoom; basemapId selects the DEM provider for terrain (BT-06.3). */
	init(containerId: string, styleUrl: string, cam?: SavedCamera | null, basemapId?: string | null): void;
	destroy(): void;
	getMap(): import("maplibre-gl").Map | null;
	isReady(): boolean;
	setStyle(styleUrl: string, basemapId?: string | null): void;
	addPerson(personId: string, trackColor: string, dotColor: string): void;
	removePerson(personId: string): void;
	updateTrack(personId: string, coords: [number, number][]): void;
	flyToFirstPoint(lat: number, lng: number): void;
	/** Last known [lng, lat] for a person, or null if none stored. */
	getLastPosition(personId: string): [number, number] | null;
	/** Drop every rider's retained coordinates. Page teardown only — destroy() keeps them (bfcache). */
	clearPersons(): void;
}
