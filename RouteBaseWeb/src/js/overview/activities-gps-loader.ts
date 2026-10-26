/**
 * overview/activities-gps-loader.ts
 *
 * Data-loading helpers for the Activities overview map, kept separate from the rendering
 * orchestrator (activities-overview.ts). Owns:
 *   • the chunked GPS fetch (via core/chunked-loader) with progress + cancellation, and
 *   • the cheap up-front viewport query used to frame the camera before any GPS loads.
 *
 * Rendering, the map shell, and progressive quality live in activities-overview.ts; this
 * module is pure transport + geometry so it stays easy to reason about and reuse.
 */

import type { OverviewTrack } from './overview-types';
import { must } from '../core/invariant';
import type { QualityTier } from '../core/core-types';
import type { ActivityEndpoint } from '../generated/contracts';
import { loadInChunks } from '../core/chunked-loader';

const GPS_ENDPOINT = '/api/activities/gps-data';
const BOUNDS_ENDPOINT = '/api/activities/bounds';

/**
 * Overview-specific quality tiers (the segment page keeps core/quality-tiers' uncapped tier: it has
 * no upgrade path, so capping there would stick). The initial overview load is capped — at overview
 * zooms the extra points are invisible but cost payload, browser RAM and server cache — and zooming
 * past the threshold upgrades just the visible tracks to full resolution (0 = uncapped) via
 * activities-overview-progressive.
 */
export const OVERVIEW_TIERS: QualityTier[] = [
	{ zoom: 0, maxPoints: 300, label: 'overview' },
	{ zoom: 11, maxPoints: 0, label: 'full' },
];

/** Index of the highest overview tier whose zoom threshold ≤ zoom. */
export function overviewTierForZoom(zoom: number): number {
	for (let i = OVERVIEW_TIERS.length - 1; i >= 0; i--) {
		if (zoom >= must(OVERVIEW_TIERS[i], 'tier i (loop-bounded)').zoom) return i;
	}
	return 0;
}

// Initial-load cap = tier 0 (progressive init marks every track as tier 0).
const OVERVIEW_MAX_POINTS = must(OVERVIEW_TIERS[0], 'tier 0 exists').maxPoints;

/** Usable GPS pair: finite numbers and not the (0,0) null-island placeholder. A single-axis zero
 *  (equator / prime-meridian crossing) is a legitimate point and is kept. */
export function isUsableLatLng(p: [number, number]): boolean {
	return Number.isFinite(p[0]) && Number.isFinite(p[1]) && !(p[0] === 0 && p[1] === 0);
}

/** Lat/lng box in the {min,max} shape used by the overview's _computeBounds helpers. */
export interface ViewBounds {
	minLat: number;
	maxLat: number;
	minLng: number;
	maxLng: number;
}

/** API response row from POST /api/activities/bounds — generated from C# ActivityEndpoint
 *  (Models/ActivityEndpoint.cs, serialized as-is). */
type ActivityEndpointDto = ActivityEndpoint;

/** Fetch one chunk of tracks, keeping only those with usable GPS points. */
export function fetchActivityChunk(
	ids: number[],
	maxPointsPerTrack: number,
	signal?: AbortSignal,
): Promise<OverviewTrack[]> {
	return fetch(GPS_ENDPOINT, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ activityIds: ids, maxPointsPerTrack }),
		signal: signal ?? null,
	})
		.then((res: Response) => {
			if (!res.ok) throw new Error('HTTP ' + res.status);
			return res.json();
		})
		.then((tracks: OverviewTrack[]) =>
			tracks.filter((t) => t.points && t.points.length > 0),
		);
}

export interface ChunkedActivityLoad {
	ids: number[];
	maxPointsPerTrack?: number;
	/** Called per resolved chunk — the hook for progressive painting / caching. */
	onChunk: (tracks: OverviewTrack[]) => void;
	onProgress?: (loaded: number, total: number) => void;
	signal?: AbortSignal;
}

/** Load all `ids` in chunks, streaming each chunk to `onChunk` as it arrives. */
export function loadActivitiesChunked(opts: ChunkedActivityLoad): Promise<OverviewTrack[][]> {
	const { ids, maxPointsPerTrack = OVERVIEW_MAX_POINTS, onChunk, onProgress, signal } = opts;
	return loadInChunks<number, OverviewTrack[]>({
		ids,
		fetchChunk: (subset) => fetchActivityChunk(subset, maxPointsPerTrack, signal),
		onChunk: (tracks) => onChunk(tracks),
		onProgress,
		// Smaller chunks → faster first paint + smaller per-chunk render bursts (less jank);
		// higher concurrency keeps total throughput up despite the extra requests.
		chunkSize: 150,
		concurrency: 2,
		signal,
	});
}

// ---------------------------------------------------------------------------
// Up-front viewport: where most tracks start/end (no GPS load)
// ---------------------------------------------------------------------------

/** Drop the outer 5% of points on each axis so a single faraway trip doesn't zoom the world out. */
const TRIM_PERCENTILE = 0.05;

function _percentile(sortedAsc: number[], p: number): number {
	const i = Math.min(sortedAsc.length - 1, Math.max(0, Math.round(p * (sortedAsc.length - 1))));
	return must(sortedAsc[i], 'clamped index within sortedAsc');
}

function _trimmedBounds(rows: ActivityEndpointDto[]): ViewBounds | null {
	const lats: number[] = [];
	const lngs: number[] = [];
	for (const r of rows) {
		if (r.startLat != null && r.startLng != null) {
			lats.push(r.startLat);
			lngs.push(r.startLng);
		}
		if (r.endLat != null && r.endLng != null) {
			lats.push(r.endLat);
			lngs.push(r.endLng);
		}
	}
	if (lats.length === 0) return null;
	lats.sort((a, b) => a - b);
	lngs.sort((a, b) => a - b);
	return {
		minLat: _percentile(lats, TRIM_PERCENTILE),
		maxLat: _percentile(lats, 1 - TRIM_PERCENTILE),
		minLng: _percentile(lngs, TRIM_PERCENTILE),
		maxLng: _percentile(lngs, 1 - TRIM_PERCENTILE),
	};
}

/**
 * Fetch the filtered set's start/end coordinates and return a percentile-trimmed bounding box.
 * Cheap (no GPS join) so it can run before the chunked GPS load to position the camera once.
 * Returns null when no activity has coordinates.
 */
export async function fetchActivityViewBounds(
	ids: number[],
	signal?: AbortSignal,
): Promise<ViewBounds | null> {
	const res = await fetch(BOUNDS_ENDPOINT, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ activityIds: ids }),
		signal: signal ?? null,
	});
	if (!res.ok) throw new Error('HTTP ' + res.status);
	const rows: ActivityEndpointDto[] = await res.json();
	return _trimmedBounds(rows);
}
