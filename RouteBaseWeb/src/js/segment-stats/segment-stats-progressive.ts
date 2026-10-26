/**
 * Segment Statistics — Track loading (bulk fetch)
 *
 * Loads all effort tracks via a single chunked bulk POST instead of N+1
 * individual fetches. Track quality is a single uncapped tier today
 * (maxPoints 0 = full resolution), so there is no zoom-driven re-fetch/upgrade
 * — tracks load at full resolution up front.
 *
 * Depends on: ctx (shared context from segment-statistics.ts)
 */
import type { TrackPoint } from '../core/core-types';
import type { SegmentStatsProgressiveApi } from './segment-stats-map-types';
import { loadInChunks } from '../core/chunked-loader';
import { ctx } from './segment-statistics';
import { buildTrackUrl, fetchTrackChunk, fetchTracksPage, isShareRoute } from './segment-stats-api';
if (!ctx) {
	throw new Error("segment-stats-progressive: no ctx");
}

// Effort tracks load in chunks (shared core/chunked-loader) so a popular segment with hundreds of
// efforts stays responsive and only shows the progress counter when there's more than one chunk.
const BULK_CHUNK_SIZE = 150;

// Transport lives in segment-stats-api.ts so the URL comes from page config, not a literal here —
// that is what lets the share route reuse this loader unchanged.

	// ====================================================================
	// PUBLIC API
	// ====================================================================

	const api = {} as SegmentStatsProgressiveApi;
export const progressiveApi = api;

	/** Per-track point cap (SH-07): a single uncapped tier today — always full resolution, no
	 *  zoom-driven re-fetch. The tier-lookup machinery it replaced always resolved to 0 anyway. */
	api.getMaxPoints = (): number => 0;

	/** Build the full-track URL with maxPoints parameter (single-track fallback) */
	api.buildTrackUrl = (trackId: number, maxPoints: number): string => buildTrackUrl(trackId, maxPoints);

	/**
	 * Bulk-fetch GPS coordinates for multiple tracks, loaded in chunks so a large effort set
	 * stays responsive. Returns a Promise resolving to the merged { trackId: [{lat,lng}, ...] }.
	 * Surfaces a live "X / Y" counter on #segMapStatus only when the load spans more than one chunk.
	 */
	api.fetchBulkTracks = async (
		trackIds: number[],
		maxPoints: number,
		opts?: { signal?: AbortSignal; onChunk?: (chunk: Record<string, TrackPoint[]>) => void },
	): Promise<Record<string, TrackPoint[]>> => {
		const statusEl = document.getElementById('segMapStatus');
		const showProgress = trackIds.length > BULK_CHUNK_SIZE && statusEl != null;

		// The share route takes NO track ids — it pages over a set the server derives from the grant, so
		// it cannot return a track the link does not cover. That means paging here rather
		// than chunking the caller's id list; the merged result is the same shape either way.
		if (isShareRoute()) {
			const merged: Record<string, TrackPoint[]> = {};
			let skip = 0;
			let total = Number.POSITIVE_INFINITY;
			while (skip < total) {
				if (opts?.signal?.aborted) break;
				const page = await fetchTracksPage(skip, BULK_CHUNK_SIZE, maxPoints);
				total = page.total;
				const count = Object.keys(page.tracks).length;
				if (count === 0) break;   // guards against a server that keeps reporting a larger total
				Object.assign(merged, page.tracks);
				opts?.onChunk?.(page.tracks);
				skip += count;
				if (showProgress && statusEl) {
					statusEl.textContent = '(Loading ' + skip + ' / ' + total + ' tracks…)';
				}
			}
			if (showProgress && statusEl) statusEl.textContent = '';
			return merged;
		}

		const chunkDicts = await loadInChunks<number, Record<string, TrackPoint[]>>({
			ids: trackIds,
			chunkSize: BULK_CHUNK_SIZE,
			fetchChunk: (subset) => fetchTrackChunk(subset, maxPoints),
			// Per-chunk hook for progressive paint — the engine renders each chunk as it lands.
			onChunk: opts?.onChunk,
			onProgress: showProgress
				? (loaded, total) => {
						if (statusEl) statusEl.textContent = '(Loading ' + loaded + ' / ' + total + ' tracks…)';
					}
				: undefined,
			signal: opts?.signal,
		});

		if (showProgress && statusEl) statusEl.textContent = '';
		return Object.assign({}, ...chunkDicts);
	};
