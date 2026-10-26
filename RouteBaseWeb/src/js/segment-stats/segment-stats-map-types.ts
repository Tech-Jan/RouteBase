/**
 * Segment-stats map contracts — shared map state + the map data/hover/engine/
 * progressive module APIs.
 *
 * Formerly ambient globals (GodFileSplit).
 */

import type { TrackPoint, ShapeRect } from '../core/core-types';
import type { SegmentEffort } from './segment-stats-effort-types';

// ---------------------------------------------------------------------------
// Segment Stats Map — shared map state (segment-stats-map.ts)
// ---------------------------------------------------------------------------

export interface SegmentStatsMapShared {
	segmentCoords: number[][];
	efforts: SegmentEffort[];
	effortGpsArrays: TrackPoint[][];
	/** Painted rectangles for shape-defined segments (empty for polyline segments). */
	shapes: ShapeRect[];
	getActiveEngine: () => { engine: string; styleUrl: string | null };
}

// ---------------------------------------------------------------------------
// Segment Stats Map Data — shared data transforms (segment-stats-map-data.ts)
// ---------------------------------------------------------------------------

export interface SegmentStatsMapDataApi {
	closestGpsIndex(gps: TrackPoint[], targetLat: number, targetLng: number): number;
	splitSegmentIndices(
		gps: TrackPoint[],
		effort: SegmentEffort,
		segmentCoords: number[][],
	): { startIdx: number; endIdx: number };
	buildEffortPopupHtml(effort: SegmentEffort): string;
	collectUniqueTracks(
		effortIndices: number[],
		efforts: SegmentEffort[],
	): { validIndices: number[]; trackIds: number[] };
}

// ---------------------------------------------------------------------------
// Segment Stats Map Hover — map→chart hover tracking (segment-stats-map-hover.ts)
// ---------------------------------------------------------------------------

/** Engine adapter for the map→chart proximity hover (segment-stats-map.ts binds a MapLibre one). */
export interface SegStatsHoverMapAdapter {
	/**
	 * Project a lat/lng to container pixels; null when the point can't be projected.
	 * H1: called O(1) times per hover frame (mouse anchor + axis probes) — never per GPS point;
	 * MapLibre's project() does a DEM elevation lookup per call when terrain is enabled.
	 */
	project(lat: number, lng: number): { x: number; y: number } | null;
	onDragStart(cb: () => void): void;
	onDragEnd(cb: () => void): void;
}

export interface SegmentStatsMapHoverApi {
	bind(adapter: SegStatsHoverMapAdapter): void;
	/** Takes the geographic mouse position (MapLibre e.lngLat); hit-test runs in a locally-linearized pixel space. */
	handleMouseMove(lngLat: { lng: number; lat: number }): void;
	resetState(): void;
}

// ---------------------------------------------------------------------------
// Segment Stats MapLibre — MapLibre engine API (segment-stats-map-maplibre.ts)
// ---------------------------------------------------------------------------

export interface SegmentStatsMapLibreApi {
	clearAll(): void;
	addPolyline(
		id: string,
		coords: number[][],
		color: string,
		width: number,
		opacity: number,
		dashArray?: number[],
		beforeId?: string,
	): void;
	addCircleMarker(
		id: string,
		lngLat: number[],
		color: string,
		radius: number,
		label?: string,
	): maplibregl.Marker | undefined;
	fitBounds(coords: number[][]): void;
	/** Draw the segment polyline + S/E markers. Optionally fit the map to the segment. */
	showSegmentRoute(fitBounds: boolean): void;
	/** Draw the painted rectangles of a shape-defined segment. Optionally fit the map to them. */
	showShapes(fitBounds: boolean): void;
	showEfforts(indices: number[], fitBoundsToFirst: boolean): Promise<void>;
	showSelectedEfforts(): Promise<void>;
	/** Redraw the currently-visible efforts (after a visibility toggle). No-op if the seg-efforts source is absent. */
	refreshVisibleEfforts(): Promise<void>;
	removeDefaultTrack(): void;
	hideStaticLayers(): void;
	showStaticLayers(): void;
}

// ---------------------------------------------------------------------------
// Segment Stats Progressive — progressive quality engine (segment-stats-progressive.ts)
// ---------------------------------------------------------------------------

export interface SegmentStatsProgressiveApi {
	getMaxPoints(): number;
	buildTrackUrl(trackId: number, maxPoints: number): string;
	/**
	 * Bulk-fetch effort tracks in chunks. `opts.onChunk` fires per resolved chunk (the hook for
	 * progressive paint); `opts.signal` cancels the load. Without opts the call still resolves to
	 * the fully-merged dict.
	 */
	fetchBulkTracks(
		trackIds: number[],
		maxPoints: number,
		opts?: { signal?: AbortSignal; onChunk?: (chunk: Record<string, TrackPoint[]>) => void },
	): Promise<Record<string, TrackPoint[]>>;
}
