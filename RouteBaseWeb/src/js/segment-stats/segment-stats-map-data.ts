/**
 * Segment Statistics — Shared map data transforms
 *
 * Pure data-transform helpers for the MapLibre map engine.
 * No rendering, no map API calls.
 *
 * Depends on: core/html escapeHtml only (+ prog() passed as an argument to async ops)
 */
import { escapeHtml } from '../core/html';
import { formatDateDMY } from '../core/grid/grid-format';
import { formatElapsedHms } from './seg-stats-shared';
import type { TrackPoint } from '../core/core-types';
import type { SegmentEffort } from './segment-stats-effort-types';
import type { SegmentStatsMapDataApi } from './segment-stats-map-types';
import { must } from '../core/invariant';

const api = {} as SegmentStatsMapDataApi;
export const mapDataApi = api;

	// ====================================================================
	// GPS INDEX HELPERS
	// ====================================================================

	/**
	 * Find the index in `gps` closest to the given [targetLat, targetLng].
	 * Uses squared Euclidean distance — sufficient for the small coordinate
	 * deltas that arise when matching track points to segment endpoints.
	 */
	api.closestGpsIndex = (gps: TrackPoint[], targetLat: number, targetLng: number): number => {
		let best = 0,
			bestDist = Infinity;
		for (let i = 0; i < gps.length; i++) {
			const p = must(gps[i], 'i < length (loop-bounded)');
			const dlat = p.lat - targetLat;
			const dlng = p.lng - targetLng;
			const d = dlat * dlat + dlng * dlng;
			if (d < bestDist) {
				bestDist = d;
				best = i;
			}
		}
		return best;
	};

	// ====================================================================
	// SEGMENT SPLIT INDICES
	// ====================================================================

	/**
	 * Find the on-segment start/end array indices for a GPS track.
	 *
	 * Primary path: match gpsId values from effort.entryId / exitId — these
	 * are the GPS row IDs written to GPSinSegment by the analysis pipeline
	 * and are preserved through downsampling, so every returned point still
	 * carries its original GPS_ID.
	 *
	 * Fallback: nearest-coordinate match to segment endpoints (used when the
	 * track has been fetched without gpsId values).
	 */
	api.splitSegmentIndices = (
		gps: TrackPoint[],
		effort: SegmentEffort,
		segmentCoords: number[][],
	): { startIdx: number; endIdx: number } => {
		if (gps.length > 0 && gps[0]?.gpsId != null && effort.entryId && effort.exitId) {
			let startIdx = 0,
				endIdx = gps.length - 1;
			for (let i = 0; i < gps.length; i++) {
				// ?? NaN: a missing per-point gpsId made both comparisons false (undefined <=), and
				// NaN <= does exactly the same — the ! hid that, it never guaranteed anything.
				const gid = must(gps[i], 'i < length (loop-bounded)').gpsId ?? NaN;
				if (gid <= effort.entryId) startIdx = i;
				if (gid <= effort.exitId) endIdx = i;
			}
			return { startIdx, endIdx };
		}
		// Shape segments have no polyline (segmentCoords is empty) — there's nothing to split a
		// track against, so treat the whole effort as on-segment (one full-opacity line).
		if (!segmentCoords || segmentCoords.length === 0) {
			return { startIdx: 0, endIdx: Math.max(0, gps.length - 1) };
		}
		// Fallback: nearest-point to segment endpoints
		const segStart = must(segmentCoords[0], 'non-empty (guarded above)');
		const segEnd = must(segmentCoords[segmentCoords.length - 1], 'non-empty (guarded above)');
		let s = api.closestGpsIndex(gps, segStart[0] ?? NaN, segStart[1] ?? NaN);
		let e = api.closestGpsIndex(gps, segEnd[0] ?? NaN, segEnd[1] ?? NaN);
		if (s > e) {
			const tmp = s;
			s = e;
			e = tmp;
		}
		return { startIdx: s, endIdx: e };
	};

	// ====================================================================
	// POPUP HTML
	// ====================================================================

	/**
	 * Build the popup HTML string for a segment effort.
	 */
	api.buildEffortPopupHtml = (effort: SegmentEffort): string => {
		// formatElapsedHms, not toISOString().substr — the latter wraps efforts past 24 h (SC2-10).
		// formatDateDMY, not the wire string verbatim — the field is wall-clock ISO now (B3) and an
		// echo would regress to showing the full ISO stamp.
		const timeStr = formatElapsedHms(effort.time);
		return (
			'<div style="color:#111;min-width:160px;">' +
			"<strong>" +
			escapeHtml(effort.name ?? "") +
			"</strong>" +
			"<br>Rank: #" +
			effort.rank +
			"<br>Date: " +
			escapeHtml(formatDateDMY(effort.date)) +
			"<br>Time: " +
			timeStr +
			"</div>"
		);
	};

	// ====================================================================
	// TRACK COLLECTION
	// ====================================================================

	/**
	 * Collect unique track IDs from a list of effort indices.
	 * Skips indices beyond the efforts array length.
	 *
	 * Returns both the de-duplicated valid indices and the unique track IDs,
	 * ready for a single bulk-fetch call.
	 */
	api.collectUniqueTracks = (
		effortIndices: number[],
		efforts: SegmentEffort[],
	): { validIndices: number[]; trackIds: number[] } => {
		const validIndices: number[] = [];
		const trackIds: number[] = [];
		for (let vi = 0; vi < effortIndices.length; vi++) {
			const i = effortIndices[vi];
			// undefined >= n was false and fell through — the new arm is dead at runtime (bucket B).
			if (i === undefined || i >= efforts.length) continue;
			validIndices.push(i);
			const tid = must(efforts[i], 'i < length (guarded above)').trackId;
			if (trackIds.indexOf(tid) === -1) trackIds.push(tid);
		}
		return { validIndices, trackIds };
	};
