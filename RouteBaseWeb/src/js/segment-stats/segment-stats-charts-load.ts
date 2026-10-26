/**
 * Segment Statistics — progressive chart-data load + aggregate-mode auto-toggle
 * (extracted from segment-stats-charts.ts, SC2-05).
 *
 * Owns the chunked performance-data fetch, the placeholder-array seeding, the
 * per-effort point budget, and the aggregate auto-on decision. `onLoaded` (the
 * core module's finishChartInit) is called on both the success and the fatal
 * chunk-failure paths so a transient failure still renders + wires the card (SC2-02).
 */
import type { EffortPerformanceData } from './segment-stats-effort-types';
import type { SegmentStatisticsConfig, SegmentStatisticsContext } from './segment-stats-page-types';
import { loadInChunks } from '../core/chunked-loader';
import { fetchPerfChunk } from './segment-stats-api';
import { resolveAggregateMode } from './segment-stats-effort-defaults';
import { setStoredAggregateMode, storedAggregateMode } from './segment-statistics';
import { hydrateEffort } from './effort-points-wire';
import { computeChartPointBudget, type ChartDetail } from './chart-point-budget';
import { getAccountSettings } from '../core/account-settings';

// Efforts per chunk for the progressive load. Concurrency is pinned to 1 (see loadPerformanceChart).
const PERF_CHUNK_SIZE = 50;

export interface ChartLoadSystem {
	load: () => Promise<void>;
	maybeAutoAggregate: () => void;
	syncAggregateButton: () => void;
	setAggregateManual: (v: boolean) => void;
}

export function createChartLoad(deps: { config: SegmentStatisticsConfig; onLoaded: () => void; ctx: SegmentStatisticsContext }): ChartLoadSystem {
	const { config, onLoaded, ctx } = deps;

	// The user's choice wins over the auto-on threshold — and now it SURVIVES the reload, because the
	// tri-state is stored rather than being a page-load-only boolean. 'auto' is still a real answer, so
	// nothing here has to guess what an absent preference meant.
	function setAggregateManual(v: boolean): void { setStoredAggregateMode(v ? 'on' : 'off'); }

	// Reflect ctx.aggregateMode on the toggle chip (pressed state + active class).
	function syncAggregateButton(): void {
		const aggBtn = document.getElementById("chartAggregateToggle");
		if (!aggBtn) return;
		aggBtn.setAttribute("aria-pressed", String(ctx.aggregateMode));
		aggBtn.classList.toggle("active", ctx.aggregateMode);
	}

	// Re-resolve the stored tri-state against the effort count. Called at load end, before the first
	// render, so the initial paint is already the right view. Counts config.effortRows — the SAME source
	// the seed counts (segment-statistics.ts) — never the derived perf array: perf is indexed by
	// effortIndex, so a sparse/re-based index would silently grow it past the row count and the two
	// callers would disagree again (FE-13).
	//
	// It repeats the ctx seed's resolution on purpose: the seed runs at import, this runs after the perf
	// load, and a toggle in between must not be undone. Both read the same stored mode and the same
	// count, so they cannot disagree — which the old page-load-only `aggregateManual` flag could not say.
	function maybeAutoAggregate(): void {
		ctx.aggregateMode = resolveAggregateMode(storedAggregateMode(), config.effortRows?.length ?? 0);
		syncAggregateButton();
	}

	// Fetch one chunk of effort performance data (by GPSinSegment id) from the bulk endpoint and
	// hydrate the columnar wire form into per-point objects at this boundary — downstream consumers
	// keep working on EffortDataPoint[].
	function _fetchPerfChunk(effortIds: number[], maxPointsPerEffort: number): Promise<EffortPerformanceData[]> {
		return fetchPerfChunk(config.segmentId, effortIds, maxPointsPerEffort)
			.then((wire) => wire.map(hydrateEffort));
	}

	// Progressive, chunked load: the chart data for a popular segment with hundreds of efforts arrives
	// in batches instead of one multi-MB payload, so the page stays responsive and paints as it loads.
	async function load(): Promise<void> {
		const rows = config.effortRows ?? [];

		// No row metadata to chunk by → nothing to load. (The legacy whole-segment endpoint was
		// deleted; effortRows is always serialized when the segment has efforts.)
		if (rows.length === 0) return;

		// Adaptive per-effort budget (PS-02): the whole-segment point budget divided across the
		// efforts, scaled by the ChartDetail knob (full → 0 = server full-res). Remembered on ctx so
		// the fixed-resolution decimation switch (PS-11) and lazy replay refetch (PS-03) can read it.
		// From the account-settings island, like every other chart preference in this slice
		// (tension/borderWidth in segment-stats-charts.ts). It used to arrive as a duplicate copy on the
		// page config with a hard-coded "normal" fallback, which silently overrode the real default of
		// "auto" — and "auto" resolves to "high" below 50 efforts, so small segments rendered coarser
		// than the user's setting asked for.
		const detail = getAccountSettings().charts.chartDetail as ChartDetail;
		const budget = computeChartPointBudget(rows.length, detail);
		ctx.perfPointBudget = budget;

		// Dense placeholder array (points: []) keeps every dataset position equal to its effortIndex
		// while efforts stream in; each chunk overwrites its slots by index. id→index maps a returned
		// effort (keyed by GPSinSegment id) back to its dataset position.
		const total = rows.length;
		const perf = new Array<EffortPerformanceData>(total);
		const idToIndex: Record<number, number> = {};
		for (const row of rows) {
			idToIndex[row.gpsInSegmentId] = row.effortIndex;
			perf[row.effortIndex] = {
				effortId: row.gpsInSegmentId,
				activityId: row.activityId,
				activityName: row.activityName ?? "",
				activityDate: row.dateIso ?? undefined,
				elapsedTime: row.elapsedSec,
				points: [],
			};
		}
		ctx.performanceData = perf;

		const orderedIds = rows.map((r) => r.gpsInSegmentId);

		const statusEl = document.getElementById("segChartStatus");
		const showProgress = total > PERF_CHUNK_SIZE && statusEl != null;
		const controller = new AbortController();
		window.addEventListener("pagehide", () => controller.abort(), { once: true });

		try {
			// Chunks only fill the data array (and update the progress counter) — the chart is rendered
			// once after the whole load completes, so the axes fit the full extent and we avoid repeated
			// rebuilds while loading.
			await loadInChunks<number, EffortPerformanceData[]>({
				ids: orderedIds,
				chunkSize: PERF_CHUNK_SIZE,
				// Serial: a cold cache + concurrent requests would trigger redundant full computes and
				// use the shared DbContext concurrently. The first chunk warms the per-segment cache;
				// the rest are fast filtered slices.
				concurrency: 1,
				preserveOrder: true,
				signal: controller.signal,
				fetchChunk: (ids) => _fetchPerfChunk(ids, budget),
				onChunk: (efforts) => {
					for (const e of efforts) {
						const idx = idToIndex[e.effortId];
						if (idx != null) perf[idx] = e;
					}
				},
				onProgress: showProgress
					? (loaded, t) => {
							if (statusEl) statusEl.textContent = "(Loading " + loaded + " / " + t + " efforts…)";
						}
					: undefined,
			});
		} catch (error) {
			// Abort (pagehide) → the page is tearing down; leave it alone.
			if (controller.signal.aborted) return;
			console.error("Error loading performance chart:", error);
			// Fatal chunk failure (SC2-02): surface it + still render whatever loaded and wire the controls.
			if (statusEl) statusEl.textContent = "(Chart data failed to load — reload the page to retry.)";
			onLoaded();
			return;
		}

		if (showProgress && statusEl) statusEl.textContent = "";
		// RS2-03: the dense placeholder array was assigned before any chunk landed, so only now is the
		// performance data truly loaded — replay refuses to start until this flag flips.
		ctx.performanceDataLoaded = true;
		// Render once, now that every effort is loaded — full data → correct axis extents.
		onLoaded();
	}

	return { load, maybeAutoAggregate, syncAggregateButton, setAggregateManual };
}
