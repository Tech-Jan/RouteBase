/**
 * Segment Statistics — Orchestrator
 *
 * Sets up the shared context (ctx) and handles:
 *   - Navigation & CRUD
 *   - Effort visibility state
 *   - Shared color palette
 *
 * The actual map, chart, and cross-hover logic live in:
 *   - segment-stats-hover.ts  (cross-hover coordination, map highlights)
 *   - segment-stats-map.ts    (MapLibre map, effort layers, MapTiler, size controls)
 *   - segment-stats-charts.ts (performance/elevation/comparison/progress charts)
 *
 * Reads its config from the #segmentStatisticsConfig JSON tag rendered by
 * Statistics.cshtml (segmentId, segmentCoords, effortRows, hasSegment,
 * hasEfforts, hasGpsData, mapTilerApiKey, …). The view serializes the efforts
 * list exactly once (effortRows); the map/chart shapes (efforts, effortDates,
 * hasForeignRiders) are derived from it here (PD-06).
 */
import { readData } from '../core/data-attrs';
import type { SegmentStatisticsConfig, SegmentStatisticsContext } from './segment-stats-page-types';
import { EFFORT_COLORS } from '../core/colors';
import { initCollapsibleCards } from '../core/collapsible-card';
import { refreshEffortVisibility } from './seg-stats-shared';
import {
	AGGREGATE_MODES, defaultHighlightedEffortIndices, resolveAggregateMode, type AggregateMode,
} from './segment-stats-effort-defaults';
import { devicePrefKeys, getEnum, setEnum } from '../core/device-prefs';
import { startAnalysisPolling } from '../core/op-polling-bridge';
import { waitForAnalysis } from '../core/segment-analysis-wait';
import { setupSegmentVisibilityChip } from './segment-visibility-chip';
import { readIsland } from '../core/page-bootstrap';
import { setMapTilerApiKey } from '../core/map-api-key';

/**
 * SS-06: normalize a match-threshold text input to an integer in [min,max]. A blank or non-finite
 * entry falls back to `def`; a typed `0` (a legal threshold when min="0") is preserved — the old
 * `Number(raw) || def` coerced 0 to the default. Exported for tests.
 */
export function normalizeThresholdInput(raw: string, def: number, min: number, max: number): number {
	const n = Number(raw);
	const v = raw.trim() !== "" && Number.isFinite(n) ? n : def;
	return Math.max(min, Math.min(max, Math.round(v)));
}

/** Parse the page config from the JSON tag and derive the effortRows-based shapes. */
function readConfig(): SegmentStatisticsConfig | undefined {
	const cfg = readIsland<SegmentStatisticsConfig | null>("segmentStatisticsConfig", null);
	if (!cfg) return undefined;
	const rows = cfg.effortRows ?? [];
	cfg.efforts = rows.map((r) => ({
		trackId: r.activityId,
		name: r.activityName ?? "",
		date: r.dateIso ?? "",
		time: r.elapsedSec,
		entryId: r.entryId,
		exitId: r.exitId,
		rank: r.effortIndex + 1,
		colorIndex: r.colorIndex,
	}));
	cfg.effortDates = rows.map((r) => ({
		date: r.dateIso ?? undefined,
		time: r.elapsedSec,
		name: r.activityName ?? undefined,
		isOwn: r.isOwn,
		speed: (r.avgSpeedMs ?? 0) * 3.6,
		heartRate: r.avgHr,
		power: r.avgPower,
		elevation: r.elevationM,
		excluded: r.excluded,
	}));
	cfg.hasForeignRiders = rows.some((r) => !r.isOwn);
	return cfg;
}

// Top-level on purpose: the sibling modules (segment-stats-map/-charts/…) gate their whole
// bodies on ctx.config at module-eval time, and import hoisting would run an entry-called
// init only *after* them. This module is the root of the import graph, so its top-level
// parse runs first; module scripts are deferred, so the JSON tag is already in the DOM.
const _config = readConfig();
if (!_config) {
	console.warn("segment-statistics.ts: no config found");
}

// Initialise the MapTiler key seam before the sibling map module (segment-stats-map.ts) reads it via
// helpers.getApiKey. This module is the root of the import graph, so its top-level runs first. '' when the
// user has no key configured → the registry renders keyless (liberty).
setMapTilerApiKey(_config?.mapTilerApiKey ?? '');

/**
 * Device-pref namespace for this page's chart group. One name for the whole segment-stats chart cluster,
 * not one per chart: the x-axis and the aggregate view are group-wide controls, so per-chart keys would
 * store four copies of one answer.
 */
const SEGMENT_CHARTS = 'segment';
const SEGMENT_X_AXES = ['time', 'percent'] as const;

/** The stored aggregate tri-state. Exported for the toggle in segment-stats-charts.ts to write back. */
export function storedAggregateMode(): AggregateMode {
	return getEnum<AggregateMode>(devicePrefKeys.chartAggregate(SEGMENT_CHARTS), AGGREGATE_MODES, 'auto');
}

export function setStoredAggregateMode(mode: AggregateMode): void {
	setEnum(devicePrefKeys.chartAggregate(SEGMENT_CHARTS), mode);
}

/** The stored X quantity for the comparison charts. */
export function setStoredChartXAxis(mode: string): void {
	setEnum(devicePrefKeys.chartXAxis(SEGMENT_CHARTS), mode);
}

// The account-settings seam is NOT filled here any more, and the ordering still holds: core/core.ts fills
// it, and all three layouts load core.js as a <script type="module"> BEFORE their RenderSectionAsync
// ("Scripts") — module scripts execute in document order, so core.ts runs to completion before this file
// is even fetched. That is why the charts can read getAccountSettings() at module-eval time below.

// ====================================================================
// SHARED CONTEXT — exported for direct import by all segment-stats modules
// ====================================================================

export const ctx: SegmentStatisticsContext = _config ? {
	config: _config,

	// Shared colors — centralized palette from core/colors.ts
	effortColors: EFFORT_COLORS,

	// Effort visibility state
	effortVisibility: new Set(),
	// Statsbox membership — efforts whose value shows in the hover readout box and whose line is drawn
	// at full opacity; others stay visible but dimmed. Seeded from the .effort-stats-toggle checkboxes.
	effortStats: new Set(),
	// Click-selected effort (area fill + thicker line); null = none. Pinned via a readout-box row click.
	selectedEffortIndex: null,
	// Effort whose readout-box row is currently hovered (transient emphasis); null = none.
	hoveredEffortIndex: null,
	// Last scrubbed x-value (chart hover) — lets box-row hover refresh map markers without a chart event.
	lastHoverX: undefined,

	// Chart instances (set by segment-stats-charts.ts)
	performanceChart: null,
	elevationChart: null,
	effortChart: null,
	progressChart: null,
	distributionChart: null,
	performanceData: null,
	// Per-effort point budget requested from the bulk endpoint (0 = full-res). Set by the charts
	// module at load; read by the fixed-resolution decimation switch and the replay full-res skip.
	perfPointBudget: 0,
	// Device preferences, restored at ctx build so the FIRST paint is already the view the user left in —
	// flipping either of these after the initial render is a visible re-draw, not a restore.
	currentXAxis: getEnum(devicePrefKeys.chartXAxis(SEGMENT_CHARTS), SEGMENT_X_AXES, "time"),
	deltaCharts: new Set(),
	// Aggregate (density backdrop + P25–P75 band + median) mode for the comparison charts. The stored
	// mode is a TRI-STATE; this is it resolved against THIS segment's effort count (auto → past 10).
	aggregateMode: resolveAggregateMode(storedAggregateMode(), _config.effortRows?.length ?? 0),
	progressXAxis: "equal",
	progressYMetric: "time",

	// Render callbacks (set by segment-stats-charts.ts)
	_applyStatsDimming: null,
	_renderProgressChart: null,
	// Repaints the current readout box from ctx state (set by segment-stats-charts.ts).
	_rerenderStrip: null,

	// Hover callbacks (set by segment-stats-hover.ts)
	onChartHover: null,
	onChartLeave: null,
	// Refreshes the map highlight markers from ctx state (set by segment-stats-hover.ts).
	refreshHighlight: null,
	showMapHighlight: null,
	hideMapHighlight: () => {},
	_hideMapMarkers: () => {},
	findClosestPointByX: null,

	// Replay entry points — declared here so all modules can call ctx.startReplay() / ctx.stopReplay().
	// The actual implementations are assigned by segment-stats-replay-ui.ts after it loads.
	startReplay: () => {
		console.warn("startReplay called before replay UI loaded");
	},
	stopReplay: () => {
		console.warn("stopReplay called before replay UI loaded");
	},

	// Public helpers
	getVisibleEffortIndices: (): number[] => Array.from(ctx.effortVisibility),
	setVisibleEffortIndices: setVisibleEffortIndices,
	syncCheckboxFromChart: syncCheckboxFromChart,
}
	// Absent config leaves ctx null at runtime; every sibling module gates on "if (!ctx) throw" at
	// import time (build-at-import is load-bearing, see the ctx note above).
	// !-ok -- retyping ctx as "| null" is the forbidden singleton restructure
	: null!;

if (!_config) {
	// No config — page is not a Statistics page; all other modules will skip initialization.
} else {
	// ====================================================================
	// NAVIGATION & CRUD
	// ====================================================================

	// SS-07: `btn` is disabled by the click handler before we start; re-enable it on every
	// non-navigating exit (cancel/error/catch) so the user can retry, but leave it disabled on
	// the success path (the page is navigating away).
	async function deleteSegment(segmentId: number, segmentName: string, btn?: HTMLButtonElement): Promise<void> {
		if (
			!confirm(
				'Delete segment "' +
					segmentName +
					'"?\n\nThis will permanently delete the segment, its GPS data, and all match results.',
			)
		) {
			if (btn) btn.disabled = false;
			return;
		}
		try {
			const response = await fetch("/api/SegmentsApi/" + segmentId, {
				method: "DELETE",
			});
			if (response.ok || response.status === 204) {
				window.location.href = "/Segments";
			} else {
				const error = await response.json().catch(() => ({}) as Record<string, unknown>);
				alert("Error deleting segment: " + (error.message || `HTTP ${response.status}`));
				if (btn) btn.disabled = false;
			}
		} catch (error: unknown) {
			const msg = error instanceof Error ? error.message : String(error);
			alert("Error deleting segment: " + msg);
			if (btn) btn.disabled = false;
		}
	}

	// Owner-panel button (rendered only for the segment owner) — guard for absence.
	// SS-07: guard against a double-click queuing a second confirm/DELETE behind the first.
	document.getElementById("segDeleteBtn")?.addEventListener("click", (e) => {
		const btn = e.currentTarget as HTMLButtonElement;
		if (btn.disabled) return;
		btn.disabled = true;
		void deleteSegment(Number(readData(btn, 'segmentId')), readData(btn, 'segmentName') ?? "", btn);
	});

	// ====================================================================
	// MATCH THRESHOLD SLIDER
	// ====================================================================

	const numInput = document.getElementById("matchThresholdInput") as HTMLInputElement | null;
	const resetLink = document.getElementById("resetMatchThreshold") as HTMLAnchorElement | null;

	// Bounds + default come from the server-rendered <input> (min/max/data-default),
	// so the C# constants stay the single source of truth. The fallbacks only apply
	// on pages with no segment (hence no input).
	const MIN_MATCH_THRESHOLD = numInput ? Number(numInput.min) : 0;
	const MAX_MATCH_THRESHOLD = numInput ? Number(numInput.max) : 100;

	function clampThreshold(v: number): number {
		return Math.max(MIN_MATCH_THRESHOLD, Math.min(MAX_MATCH_THRESHOLD, Math.round(v)));
	}

	const DEFAULT_MATCH_THRESHOLD = numInput ? clampThreshold(Number(readData(numInput, 'default'))) : 0;

	if (numInput) {
		numInput.addEventListener("change", () => {
			// SS-06: `|| DEFAULT` coerced a legitimate 0 (min="0") to the default. Only fall back to the
			// default for a blank/non-finite entry — a typed 0 stays 0.
			numInput.value = String(normalizeThresholdInput(numInput.value, DEFAULT_MATCH_THRESHOLD, MIN_MATCH_THRESHOLD, MAX_MATCH_THRESHOLD));
		});
	}

	if (resetLink && numInput) {
		resetLink.addEventListener("click", (e) => {
			e.preventDefault();
			numInput.value = String(DEFAULT_MATCH_THRESHOLD);
		});
	}

	async function reanalyzeSegment(segmentId: number, btn: HTMLElement): Promise<void> {
		const threshold = numInput ? clampThreshold(Number(numInput.value)) : DEFAULT_MATCH_THRESHOLD;
		if (
			!confirm(
				`Re-analyze this segment at ${threshold}% match threshold?\nThis will find all matching efforts in your activities.`,
			)
		)
			return;
		const originalHtml = btn.innerHTML;
		(btn as HTMLButtonElement).disabled = true;
		btn.innerHTML = '<span class="spinner-border spinner-border-sm me-2"></span>Queuing...';
		try {
			const response = await fetch("/api/SegmentsApi/" + segmentId + "/analyze", {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ matchThresholdPercent: threshold }),
			});
			if (response.ok) {
				// Analysis runs on the background worker now — show it in the shared progress toast
				// and reload once it finishes so the new efforts/charts render.
				startAnalysisPolling();
				btn.innerHTML = '<span class="spinner-border spinner-border-sm me-2"></span>Analyzing...';
				const result = await waitForAnalysis();
				if (result === "failed") {
					(btn as HTMLButtonElement).disabled = false;
					// eslint-disable-next-line no-unsanitized/property -- restores the button's own prior innerHTML (app-authored markup saved above)
					btn.innerHTML = originalHtml;
				} else {
					window.location.reload();
				}
			} else {
				const err = await response.json().catch(() => ({}) as Record<string, unknown>);
				alert("Analysis failed: " + (err.message || `HTTP ${response.status}`));
				(btn as HTMLButtonElement).disabled = false;
				// eslint-disable-next-line no-unsanitized/property -- restores the button's own prior innerHTML (app-authored markup saved above)
				btn.innerHTML = originalHtml;
			}
		} catch (_error) {
			alert("Error analyzing segment");
			(btn as HTMLButtonElement).disabled = false;
			// eslint-disable-next-line no-unsanitized/property -- restores the button's own prior innerHTML (app-authored markup saved above)
			btn.innerHTML = originalHtml;
		}
	}

	// Header visibility chip → select → PATCH. Owns its own absence guards.
	setupSegmentVisibilityChip();

	// Owner-panel button (rendered only for the segment owner) — guard for absence.
	document.getElementById("segReanalyzeBtn")?.addEventListener("click", (e) => {
		const btn = e.currentTarget as HTMLElement;
		void reanalyzeSegment(Number(readData(btn, 'segmentId')), btn);
	});

	// Replay button (rendered only when the segment has efforts) — guard for absence.
	document.getElementById("segReplayBtn")?.addEventListener("click", (e) => {
		e.stopPropagation();
		ctx.startReplay();
	});

		// ====================================================================
		// EFFORT VISIBILITY / STATSBOX STATE
		// ====================================================================
		// The per-row checkboxes (visibility + statsbox) and their filtered-scope master toggles now live in
		// the efforts grid (segment-stats-efforts-table.ts), built on the reusable grid's selection feature.
		// That module owns the UI and feeds results back via ctx.setVisibleEffortIndices() and ctx.effortStats +
		// ctx._applyStatsDimming(). See setVisibleEffortIndices() below.
		//
		// Seed the selections from the effort rows here (data-driven) so they're ready before the charts/map
		// render: visibility starts at "all active efforts"; the statsbox (highlight) default comes from
		// defaultHighlightedEffortIndices — top-3 + latest, or first place alone in aggregate mode.
		// The grid mirrors these into its checkboxes on load. Item 25: excluded efforts stay in the grid
		// (marked, restorable) but are seeded OUT of visibility/statsbox so they never paint on the map or
		// comparison/progress charts (the perf-data read also anti-joins them out server-side).
		const effortRows = _config.effortRows ?? [];
		effortRows.filter((r) => !r.excluded).forEach((r) => ctx.effortVisibility.add(r.effortIndex));
		// ctx.aggregateMode, not a second derivation from the row count: with the mode stored, a user who
		// turned aggregate OFF on a 40-effort segment must still get their top-3 + latest highlighted, or
		// the page opens with a chart that draws nothing.
		for (const i of defaultHighlightedEffortIndices(effortRows, ctx.aggregateMode)) ctx.effortStats.add(i);

		// ====================================================================
		// COLLAPSIBLE CARDS
		// ====================================================================

	initCollapsibleCards();
}

// ====================================================================
// EFFORT VISIBILITY — helper functions (module scope for ctx reference)
// ====================================================================

// Replace the visible-effort set and refresh the charts/map. Called by the efforts grid
// (segment-stats-efforts-table.ts) after a selection or filter change.
function setVisibleEffortIndices(indices: number[]): void {
	ctx.effortVisibility.clear();
	for (const i of indices) ctx.effortVisibility.add(i);
	updateVisibleEfforts();
}

// Update ctx visibility for one effort. Kept for the ctx contract (chart/map sync helpers may call it).
function syncCheckboxFromChart(index: number, visible: boolean): void {
	if (visible) ctx.effortVisibility.add(index);
	else ctx.effortVisibility.delete(index);
}

function updateVisibleEfforts(): void {
	const visible = ctx.getVisibleEffortIndices();

	if (ctx.aggregateMode) {
		// Aggregate mode: the band is computed over the visible efforts, so a visibility change means
		// recomputing band+median (and re-drawing highlighted lines) — a full aggregate rebuild, which
		// also re-fits the axes. The per-dataset hidden toggle below doesn't apply (datasets aren't
		// effort-indexed here).
		ctx._rebuildAggregate?.();
	} else {
		// Re-fit the comparison charts' x-axis to the visible efforts before updating, so hiding the
		// longest effort tightens the axis (not just the line) in a single repaint.
		ctx._refitXAxis?.();

		// Toggle dataset hidden state on all existing charts — avoid destroy+recreate.
		const visibleSet = new Set(visible);   // Set.has, not O(n) Array.includes per dataset ×3 charts
		[ctx.performanceChart, ctx.elevationChart, ctx.effortChart].forEach((chart) => {
			if (!chart) return;
			for (let i = 0; i < chart.data.datasets.length; i++) {
				chart.getDatasetMeta(i).hidden = !visibleSet.has(i);
			}
		});
		// Re-fit the y-axis to the now-visible efforts (also issues the chart.update). Without this the
		// pinned axis stays as tall as the hidden efforts, even though the x-axis already tightened.
		ctx._refitYAxis?.();
	}

	// SH-03: full re-render instead of a hand-rolled visibility mirror. The old mirror looped over ALL
	// progress datasets including the trailing connector line (index N, never in getVisibleEffortIndices),
	// so it permanently hid the connector + dotted its vertices on every Apply. renderProgressChart()
	// rebuilds from current visibility and excludes the connector by construction.
	ctx._renderProgressChart?.();

	// Untick every effort and there is nothing to race — the Replay button reflects that instead of
	// accepting the click and answering with a modal.
	ctx._syncReplayAvailability?.();

	// Redraw the map's effort tracks for the new visible set (debounced, in-memory) regardless of
	// the active display mode. No-ops when the current mode shows no efforts (segment-only).
	refreshEffortVisibility();
}
