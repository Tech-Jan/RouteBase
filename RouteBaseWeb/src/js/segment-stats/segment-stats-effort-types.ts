/**
 * Segment-stats effort DTOs — the server-rendered + API effort shapes and the
 * hydrated in-memory forms every chart/hover/replay consumer holds.
 *
 * Formerly ambient globals (GodFileSplit).
 */

import type {
	EffortPerformanceData as GeneratedEffortPerformanceData,
	EffortPointsWire as GeneratedEffortPointsWire,
	SegmentEffortRowDto,
} from '../generated/contracts';

// ---------------------------------------------------------------------------
// Segment effort — server-rendered config data (Statistics.cshtml inline script)
// Properties use camelCase per ASP.NET Core's default JSON serialization.
// ---------------------------------------------------------------------------

/**
 * Effort metadata rendered inline by Statistics.cshtml.
 * Augmented at runtime with `color` (from the palette).
 */
export interface SegmentEffort {
	trackId: number;
	name: string;
	date: string;
	/** Elapsed time in seconds. */
	time: number;
	entryId: number;
	exitId: number;
	rank: number;
	colorIndex: number;
	/** Runtime-assigned color string from ctx.effortColors[colorIndex]. */
	color?: string | undefined;
}

// ---------------------------------------------------------------------------
// Effort performance data — hydrated in-memory shape (what ctx.performanceData and every
// chart/hover/replay consumer holds). The bulk endpoint sends the columnar
// EffortPerformanceDataWire form; effort-points-wire.ts re-hydrates it at the fetch boundary
// into these EffortDataPoint[] objects, so consumers stay unchanged.
// ---------------------------------------------------------------------------

/** The HYDRATED effort (G1: refined over the generated wire twin instead of redeclared). The
 *  identity trio stays required and compiler-tied to the wire; the metadata is Partial because
 *  this is the client's IN-MEMORY shape — placeholder stubs (charts-load) legitimately omit it —
 *  and activityName/activityDate become plain optionals (hydrateEffort maps wire nulls to absent).
 *  `points` is the client-side re-hydration of the columnar wire form. */
export interface EffortPerformanceData
	extends Pick<GeneratedEffortPerformanceData, 'effortId' | 'activityId' | 'elapsedTime'>,
		Partial<Omit<GeneratedEffortPerformanceData,
			'effortId' | 'activityId' | 'elapsedTime' | 'points' | 'activityName' | 'activityDate'>> {
	activityName?: string | undefined;
	activityDate?: string | undefined;
	points: EffortDataPoint[];
}

// ---------------------------------------------------------------------------
// Columnar wire form — the actual JSON from /api/SegmentsApi/{id}/performance-data/bulk.
// Generated from Models/EffortPerformanceData.cs (the C# also names it EffortPerformanceData —
// re-exported here under the Wire names to keep the wire-vs-hydrated distinction).
// Only effort-points-wire.ts + segment-stats-charts.ts's fetch funnel see this shape.
// ---------------------------------------------------------------------------

export type EffortPerformanceDataWire = GeneratedEffortPerformanceData;
export type EffortPointsWire = GeneratedEffortPointsWire;

export interface EffortDataPoint {
	distance: number;
	time: number;
	speed: number;
	// C# has these as double? — the server sends nulls for points without altitude/coords.
	altitude: number | null;
	lat: number | null;
	lng: number | null;
	/** Cumulative effort cost (kJ) from the segment start to this point. */
	cumulativeCostKj?: number | null | undefined;
	/** Fraction (0–1) along the segment's reference polyline (linear referencing).
	 *  Absent/null when the backend could not compute it. */
	segmentProgress?: number | null | undefined;
}

// ---------------------------------------------------------------------------
// Effort date data — server-rendered in Statistics.cshtml for progress chart
// ---------------------------------------------------------------------------

export interface EffortDateData {
	date?: string | undefined;
	time: number;
	name?: string | undefined;
	isOwn?: boolean | undefined;
	speed: number;
	heartRate?: number | null | undefined;
	power?: number | null | undefined;
	elevation: number;
	/** Item 25: durably excluded — filtered out of the distribution histogram + progress chart. */
	excluded?: boolean | undefined;
}

/**
 * One row of the segment efforts grid — generated from C# SegmentEffortRowDto (the named effortRows
 * island projection, D28). `effortIndex` (rank-1, ordered by elapsed time) is the stable id that ties a
 * row to its chart dataset / map layer / color; `excluded` (item 25) hides a row from totals/charts/
 * heatmap while it stays in the grid with a Restore action. Units: elapsedSec s, distanceM/elevationM m,
 * avgSpeedMs m/s; entry/exitId are GPS row ids that drive map/replay track slicing.
 */
export type EffortRow = SegmentEffortRowDto;
