/**
 * Segment Statistics — initial effort selection defaults (pure; leaf module).
 *
 * Owns the aggregate auto-on threshold and the "which efforts start highlighted" rule, so the
 * seed (segment-statistics.ts, runs before the perf load) and the actual auto-engage
 * (segment-stats-charts-load.ts, runs after it) can never disagree about which view the page
 * opens in.
 */

import type { EffortRow } from './segment-stats-effort-types';
import { must } from '../core/invariant';

/** Auto-enable aggregate mode when more than this many efforts are loaded. */
export const AGGREGATE_AUTO_THRESHOLD = 10;

/**
 * The aggregate view's persisted state, and it is a TRI-STATE on purpose.
 *
 * `auto` is a real answer, not the absence of one: the view engages itself past
 * AGGREGATE_AUTO_THRESHOLD efforts, so "I have not chosen" and "I chose off" behave differently on a
 * 40-effort segment. Stored as a boolean the two collapse — whichever meaning you give an absent key,
 * the other becomes unreachable — which is the same "what does absent mean" defect that made a user's
 * chart smoothing depend on whether their page had a MapTiler key.
 */
export type AggregateMode = 'auto' | 'on' | 'off';
export const AGGREGATE_MODES: readonly AggregateMode[] = ['auto', 'on', 'off'];

/**
 * Does a segment with this many efforts open in aggregate mode? Both callers pass the FULL row
 * count (excluded rows included) — perf data is a dense array sized by every row.
 */
export function autoAggregateForEffortCount(count: number): boolean {
	return count > AGGREGATE_AUTO_THRESHOLD;
}

/** The tri-state plus this segment's effort count → the boolean the charts actually render from. */
export function resolveAggregateMode(mode: AggregateMode, effortCount: number): boolean {
	if (mode === 'on') return true;
	if (mode === 'off') return false;
	return autoAggregateForEffortCount(effortCount);
}

/**
 * Efforts highlighted on load: top 3 by rank plus the most recent — first place alone in aggregate mode.
 *
 * Aggregate's subject is still the band, so it does NOT pre-highlight four efforts over it. But zero
 * highlighted efforts means zero individual lines and an empty hover readout, so the leader is the one
 * line the band always gets — the reference every other effort is read against.
 *
 * `aggregateOn` is PASSED IN rather than re-derived from the row count, which is what it used to do.
 * Once the mode is a stored tri-state the count no longer decides it, and a module that re-derived
 * would hand back the aggregate default to a user who had explicitly turned aggregate OFF.
 */
export function defaultHighlightedEffortIndices(rows: EffortRow[], aggregateOn: boolean): number[] {
	// Item 25: excluded efforts stay in the grid (marked, restorable) but never paint.
	const active = rows.filter((r) => !r.excluded);
	if (active.length === 0) return [];

	// effortIndex IS the rank, but the best-ranked ACTIVE row is not always index 0 — rank 1 can be excluded.
	let first = must(active[0], 'active.length > 0 (guarded above)');
	let latest = first;
	for (const r of active) {
		if (r.effortIndex < first.effortIndex) first = r;
		if ((r.dateIso ?? '') > (latest.dateIso ?? '')) latest = r;
	}
	if (aggregateOn) return [first.effortIndex];

	const picked = new Set(active.filter((r) => r.effortIndex < 3).map((r) => r.effortIndex));
	picked.add(latest.effortIndex);
	return Array.from(picked);
}
