/**
 * Segment Statistics — Replay full-res data loader (PS-03)
 *
 * Charts load server-DOWNSAMPLED effort points (PS-01/PS-02), but replay needs byte-identical
 * full resolution for the wiggly individual paths. This module lazily fetches full-res points
 * for the replayed efforts via the existing chunked bulk endpoint (maxPointsPerEffort: 0), caches
 * them for the page session, and hands them back per chunk so markers/tracks upgrade progressively.
 *
 * Efforts whose chart data is already full-res (pointsTruncated === false — small segments, or the
 * "Full" detail knob) are used as-is: no refetch.
 *
 * Receives the page context (performanceData + config) as an explicit `ctx` parameter — the singleton
 * is no longer imported here; callers pass it, and tests pass a fake instead of mocking it.
 * Depends on: chunked-loader, effort-points-wire.
 */
import type { EffortPerformanceData, EffortDataPoint } from './segment-stats-effort-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { fetchPerfChunk } from './segment-stats-api';
import { loadInChunks } from '../core/chunked-loader';
import { hydrateEffort } from './effort-points-wire';

const PERF_CHUNK_SIZE = 50;

// Session cache of full-res effort points keyed by effortIndex. Persists across replay restarts on
// the same page load, so re-entering replay never refetches. Cleared only on a full page reload.
const fullResPoints = new Map<number, EffortDataPoint[]>();

/** Test seam: clear the session cache. */
export function _resetFullResCache(): void {
	fullResPoints.clear();
}

/** An effort needs a full-res fetch when it's downsampled (pointsTruncated) and not already cached. */
export function needsFullRes(ctx: SegmentStatisticsContext, effortIndex: number): boolean {
	if (fullResPoints.has(effortIndex)) return false;
	const eff = ctx.performanceData?.[effortIndex];
	return !!eff && eff.pointsTruncated === true;
}

/**
 * Copy of `perfData` where each VISIBLE effort's points are the best available resolution: cached
 * full-res if present, else the effort's own (downsampled or already-full) points. Keeping the
 * downsampled points as a placeholder (rather than []) means the effort set — and therefore the
 * replay marker/track indices — stays stable as full-res chunks stream in; only the geometry
 * sharpens. Non-visible efforts are left untouched (the engine only builds the visible set).
 */
export function buildReplayPerfData(
	perfData: EffortPerformanceData[],
	visibleIndices: number[],
): EffortPerformanceData[] {
	const out = perfData.slice();
	for (const i of visibleIndices) {
		const eff = perfData[i];
		if (!eff) continue;
		const full = fullResPoints.get(i);
		out[i] = full ? { ...eff, points: full } : eff;
	}
	return out;
}

/**
 * Fetch full-res points for the visible efforts that need it, in 50-effort chunks (serial, to reuse
 * the warm per-segment cache). Each resolved chunk is hydrated, cached by effortIndex, and reported
 * through `onEffortsReady` so the caller can rebuild the replay layers progressively. Resolves when
 * every needed effort is loaded; a no-op (no fetch) when nothing needs full-res.
 */
export async function loadFullResEfforts(
	ctx: SegmentStatisticsContext,
	visibleIndices: number[],
	onEffortsReady: (indices: number[]) => void,
	signal: AbortSignal,
): Promise<void> {
	const rows = ctx.config.effortRows ?? [];
	const idToIndex: Record<number, number> = {};
	const indexToId: Record<number, number> = {};
	for (const r of rows) {
		idToIndex[r.gpsInSegmentId] = r.effortIndex;
		indexToId[r.effortIndex] = r.gpsInSegmentId;
	}

	const toFetch = visibleIndices.filter((i) => needsFullRes(ctx, i));
	if (toFetch.length === 0) return;

	const orderedIds = toFetch.map((i) => indexToId[i]).filter((id): id is number => id != null);
	if (orderedIds.length === 0) return;

	await loadInChunks<number, EffortPerformanceData[]>({
		ids: orderedIds,
		chunkSize: PERF_CHUNK_SIZE,
		// Serial: the first chunk warms the per-segment full-res cache; the rest are fast slices.
		concurrency: 1,
		preserveOrder: true,
		signal,
		fetchChunk: (effortIds) => fetchFullResChunk(ctx, effortIds),
		onChunk: (efforts) => {
			const ready: number[] = [];
			for (const e of efforts) {
				const idx = idToIndex[e.effortId];
				if (idx == null) continue;
				fullResPoints.set(idx, e.points);
				ready.push(idx);
			}
			if (ready.length) onEffortsReady(ready);
		},
	});
}

// Full-res chunk fetch: maxPointsPerEffort 0 = server full resolution. Hydrates the columnar wire
// into EffortDataPoint[] at this boundary (same shape charts consume).
function fetchFullResChunk(ctx: SegmentStatisticsContext, effortIds: number[]): Promise<EffortPerformanceData[]> {
	return fetchPerfChunk(ctx.config.segmentId, effortIds, 0).then((wire) => wire.map(hydrateEffort));
}
