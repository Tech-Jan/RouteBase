/**
 * Segment Statistics page contracts — the parsed page config and the shared
 * context object that ties the charts/map/replay modules together.
 *
 * Formerly ambient globals (GodFileSplit).
 */

import type { ChartJsInstance, ChartEventLike } from '../core/core-types';
import type {
	SegmentEffort,
	EffortDateData,
	EffortPerformanceData,
	EffortDataPoint,
} from './segment-stats-effort-types';

// ---------------------------------------------------------------------------
// Segment Statistics page config — parsed from the #segmentStatisticsConfig
// JSON tag by segment-statistics.ts (no window global)
// ---------------------------------------------------------------------------

type GeneratedSegmentStatsConfig = import('../generated/contracts').SegmentStatisticsConfigDto;

/**
 * The parsed #segmentStatisticsConfig island — the GENERATED DTO (F2b) plus the fields
 * segment-statistics.ts derives at runtime from effortRows. Field docs live on
 * SegmentStatisticsConfigDto (Models/Sharing/SegmentDetailView.cs); the load-bearing bit is that
 * hasHeatmap/heatmapStatus are OMITTED when the heatmap surface is off (poll gate).
 */
export interface SegmentStatisticsConfig extends Omit<GeneratedSegmentStatsConfig, 'segmentCoords'> {
	/** Refined from the generated (null | number)[][]: the entity columns are nullable, the data
	 *  never is — Segment_GPS rows always carry coordinates. */
	segmentCoords?: number[][];
	/** Derived at runtime from effortRows (segment-statistics.ts) — not serialized by the view. */
	efforts?: SegmentEffort[];
	/** Derived at runtime from effortRows (segment-statistics.ts) — not serialized by the view. */
	effortDates?: EffortDateData[];
	/** True when at least one effort belongs to another rider — drives the conditional Rider column.
	 *  Derived at runtime from effortRows (segment-statistics.ts). */
	hasForeignRiders?: boolean;
}

// ---------------------------------------------------------------------------
// Segment Statistics — shared context object (segment-statistics.ts)
// ---------------------------------------------------------------------------

export interface SegmentStatisticsContext {
	config: SegmentStatisticsConfig;
	/** Per-effort point budget last requested from performance-data/bulk (0 = server full-res).
	 *  Drives the fixed-resolution decimation switch (PS-11). Replay's full-res refetch keys off each
	 *  effort's `pointsTruncated` flag (segment-stats-replay-data.ts), not this budget (SC2-10). */
	perfPointBudget: number;
	effortColors: string[];
	effortVisibility: Set<number>;
	/** Efforts whose value shows in the hover statsbox + line drawn full-opacity; others are dimmed. */
	effortStats: Set<number>;
	/** Effort pinned via a readout-box row click (area fill + thicker line); null = none. */
	selectedEffortIndex: number | null;
	/** Effort whose readout-box row is currently hovered (transient emphasis); null = none. */
	hoveredEffortIndex: number | null;
	/** Last scrubbed x-value (chart hover) so box-row hover can refresh markers without a chart event. */
	lastHoverX: number | undefined;
	/** Re-fit the chart x-axis to the currently-visible efforts before a chart.update (effort toggle).
	 * Optional: assigned by the chart module; call sites guard with `?.()`. */
	_refitXAxis?: () => void;
	/** Re-fit the comparison charts' y-axis to the currently-visible efforts (un-freeze → recompute →
	 * re-freeze), so hiding the tallest effort shrinks the height. Assigned by the chart module. */
	_refitYAxis?: () => void;
	performanceChart: ChartJsInstance | null;
	elevationChart: ChartJsInstance | null;
	effortChart: ChartJsInstance | null;
	progressChart: ChartJsInstance | null;
	distributionChart: ChartJsInstance | null;
	performanceData: EffortPerformanceData[] | null;
	/** True once the chunked performance-data load has fully completed (RS2-03); the replay gate
	 *  refuses to start while this is false, since `performanceData` holds a dense placeholder array
	 *  from the moment the load begins. Never set on the chunk-fail/abort paths. */
	performanceDataLoaded?: boolean;
	currentXAxis: string;
	/** Chart keys currently in delta (vs reference effort) mode: "speed" | "elevation" | "effort". */
	deltaCharts: Set<string>;
	/** Aggregate (density backdrop + P25–P75 band + median) mode for the comparison charts; auto-on past 10 efforts (AGGREGATE_AUTO_THRESHOLD). */
	aggregateMode: boolean;
	/** Rebuild all three comparison charts' aggregate datasets in place (band/highlight/delta change). */
	_rebuildAggregate?: (() => void) | null;
	progressXAxis: string;
	progressYMetric: string;
	/** Re-apply statsbox line dimming to the live charts (no full rebuild). Set by the charts module. */
	_applyStatsDimming: (() => void) | null;
	_renderProgressChart: (() => void) | null;
	_renderDistributionChart?: (() => void) | null;
	/** Repaint the current readout box from ctx state (box-row hover/click changes emphasis/selection). */
	_rerenderStrip: (() => void) | null;
	onChartHover: ((chart: ChartJsInstance, event: ChartEventLike) => void) | null;
	onChartLeave: (() => void) | null;
	/** Guarded set-active-element (ID-16 stale-element throw swallowed), no draw() — for the rAF-driven
	 *  map→chart hover. Returns true iff it changed anything. Set by segment-stats-hover.ts (SH-01). */
	_trySetChartActive?: (chart: ChartJsInstance | null, dsIndex: number, pointIndex: number) => boolean;
	/** Guarded set-active-element that draws on change (SH-01/SH-02). Set by segment-stats-hover.ts. */
	_setChartActive?: (chart: ChartJsInstance | null, dsIndex: number, pointIndex: number) => void;
	/** Guarded clear-active-element (SH-01/SH-02). Set by segment-stats-hover.ts. */
	_clearChartActive?: (chart: ChartJsInstance | null) => void;
	/** Refresh the map highlight markers from ctx state (selection + hovered effort at lastHoverX). */
	refreshHighlight: (() => void) | null;
	showMapHighlight:
		| ((effortIndex: number, lat: number, lng: number, color: string, highlighted?: boolean) => void)
		| null;
	hideMapHighlight: () => void;
	/** Lightweight hide — hides markers without DOM removal (for hover frames). */
	_hideMapMarkers: () => void;
	findClosestPointByX: ((points: EffortDataPoint[], targetX: number, xField: string) => number) | null;
	startReplay: () => void;
	stopReplay: () => void;
	/** Enable/disable the Replay button for the current visible-effort set (0 efforts = nothing to
	 *  race). Assigned by the replay UI; call sites guard with `?.()`. */
	_syncReplayAvailability?: () => void;
	/** True when the heatmap raster is the active map view (chip on + tiles ready). Set by segment-stats-map.ts. */
	isHeatmapShown?: () => boolean;
	getVisibleEffortIndices: () => number[];
	/** Replace the visible-effort set with `indices` and refresh the charts/map (used by the efforts grid). */
	setVisibleEffortIndices: (indices: number[]) => void;
	syncCheckboxFromChart: (index: number, visible: boolean) => void;
	showSegmentPositionMarker?: (lat: number, lng: number) => void;
}
