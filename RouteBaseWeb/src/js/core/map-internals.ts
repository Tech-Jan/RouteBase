/**
 * core/map-internals.ts — the ONE home for MapLibre private-field access (K4, sibling of
 * chart-internals.ts). Type-only imports from maplibre-gl (core/ may import maplibre TYPES,
 * never runtime values — core-layering.test.ts pins this), so the seam is layering-safe by
 * construction. Each accessor holds a single documented cast naming the internal field; do not
 * add a new `as unknown as` for map internals at a call site — extend this seam instead.
 */

import type maplibregl from 'maplibre-gl';
import { must } from './invariant';

/** MapLibre's internal HandlerManager surface the app reads/shields. */
export interface MapHandlersInternal {
	_updatingCamera?: boolean | undefined;
	isActive?: (() => boolean) | undefined;
}

export function mapHandlers(map: maplibregl.Map): MapHandlersInternal | undefined {
	// cast: map.handlers — MapLibre's internal HandlerManager (undocumented)
	return (map as unknown as { handlers?: MapHandlersInternal }).handlers;
}

/** Camera-update interception hook (returns overrides; {} = leave the frame alone). */
export type CameraUpdateHook = (next: { center?: maplibregl.LngLat }) => { center?: maplibregl.LngLat };

export function setTransformCameraUpdate(map: maplibregl.Map, hook: CameraUpdateHook | null): void {
	// cast: map.transformCameraUpdate — settable at runtime but only a constructor option in the types
	(map as unknown as { transformCameraUpdate: CameraUpdateHook | null }).transformCameraUpdate = hook;
}

/** Write the terrain centre altitude on builds without map.setCenterElevation(). */
export function setTransformCenterAltitude(transform: object, elevation: number): void {
	// cast: transform.centerAltitude — internal MapLibre API (the documented setter arrived later)
	(transform as { centerAltitude: number }).centerAltitude = elevation;
}

/** A gesture handler's toggle surface (dragPan, scrollZoom, …). */
export interface GestureHandlerInternal {
	enable(options?: unknown): void;
	disable(): void;
}

export function gestureHandler(map: maplibregl.Map, name: string): GestureHandlerInternal {
	// cast: map[name] — the gesture handlers hang off the map by name, untyped as a record.
	// must(): an unknown name used to return undefined and TypeError at the call site.
	return must((map as unknown as Record<string, GestureHandlerInternal>)[name], 'known gesture handler name');
}
