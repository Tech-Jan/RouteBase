/**
 * Adaptive per-effort chart point budget (PS-02) + fixed-resolution zoom decision (PS-11).
 *
 * Pure, ctx-free: the whole-segment budget is divided across the visible efforts so a
 * popular segment loads a bounded point count (~150 k total) instead of every GPS point.
 * The budget is PER EFFORT, so one outlier effort only thins its OWN line — it can never
 * lower another effort's resolution (see analysis.md PS-02).
 */

/** Total points spread across all efforts at "normal" detail. */
export const TOTAL_POINT_BUDGET = 150_000;
/** Floor so short segments still get a usable line even with hundreds of efforts. */
export const MIN_POINTS_PER_EFFORT = 100;
/** Ceiling so a few-effort segment can't request an unbounded per-effort count. */
export const MAX_POINTS_PER_EFFORT = 4_000;

export type ChartDetail = "auto" | "low" | "normal" | "high" | "full";

/** Below this effort count "auto" picks "high"; at or above it picks "normal". */
export const AUTO_HIGH_EFFORT_THRESHOLD = 50;

/** Budget multiplier per knob position. "full" is handled separately (→ 0 = server full-res). */
export const DETAIL_MULTIPLIER = { low: 0.5, normal: 1, high: 2 } as const;

/**
 * Resolve "auto" to a concrete detail from the effort count: "high" for smaller segments,
 * "normal" once they grow. Never resolves to "full" — that stays an explicit opt-in.
 */
export function resolveAutoDetail(effortCount: number): Exclude<ChartDetail, "auto"> {
	return effortCount < AUTO_HIGH_EFFORT_THRESHOLD ? "high" : "normal";
}

/**
 * Per-effort point budget for a segment shown at `detail`. "auto" resolves by effort count
 * (never to "full"). "full" → 0, the sentinel the server reads as "no downsampling" (today's
 * all-points behavior). Otherwise the whole budget × multiplier is divided by the effort
 * count and clamped to [MIN, MAX].
 */
export function computeChartPointBudget(effortCount: number, detail: ChartDetail): number {
	if (detail === "auto") detail = resolveAutoDetail(effortCount);
	if (detail === "full") return 0;
	const mult = DETAIL_MULTIPLIER[detail] ?? DETAIL_MULTIPLIER.normal;
	const raw = Math.round((TOTAL_POINT_BUDGET * mult) / Math.max(effortCount, 1));
	return Math.min(MAX_POINTS_PER_EFFORT, Math.max(MIN_POINTS_PER_EFFORT, raw));
}

/**
 * Whether the charts should client-side LTTB-decimate (PS-11). Only when the data is
 * full-resolution (budget 0 = knob "Full") AND not in aggregate mode: server-downsampled
 * data is already at a fixed bounded resolution, so decimation off makes zoom rescale the
 * loaded points ("picture-like") like the aggregate chart instead of re-slicing per zoom.
 */
export function shouldClientDecimate(requestedBudget: number, aggregateMode: boolean): boolean {
	return requestedBudget === 0 && !aggregateMode;
}
