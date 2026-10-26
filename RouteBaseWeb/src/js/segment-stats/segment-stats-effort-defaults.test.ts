// @vitest-environment node
import { describe, it, expect } from 'vitest';
import type { EffortRow } from './segment-stats-effort-types';
import {
	AGGREGATE_AUTO_THRESHOLD,
	autoAggregateForEffortCount,
	defaultHighlightedEffortIndices,
	resolveAggregateMode,
} from './segment-stats-effort-defaults';

// effortIndex doubles as rank (rows arrive rank-sorted), so index < 3 IS the top 3.
function row(effortIndex: number, dateIso: string, excluded = false): EffortRow {
	return { effortIndex, dateIso, excluded } as unknown as EffortRow;
}

function rows(n: number): EffortRow[] {
	return Array.from({ length: n }, (_, i) => row(i, `2026-01-${String(i + 1).padStart(2, '0')}`));
}

describe('autoAggregateForEffortCount', () => {
	it('is off at the threshold and on one past it', () => {
		expect(autoAggregateForEffortCount(AGGREGATE_AUTO_THRESHOLD)).toBe(false);
		expect(autoAggregateForEffortCount(AGGREGATE_AUTO_THRESHOLD + 1)).toBe(true);
	});
});

/**
 * The tri-state is the point: 'auto' is a real answer, not a missing one.
 *
 * A boolean cannot express this. Whichever meaning you gave an absent key, one of the two behaviours
 * below would become unreachable — either a user could never turn aggregate off on a big segment, or
 * they could never leave it on the automatic rule. That collapse is the same defect that made chart
 * smoothing depend on whether a page had a MapTiler key.
 */
describe('resolveAggregateMode', () => {
	const big = AGGREGATE_AUTO_THRESHOLD + 1;
	const small = AGGREGATE_AUTO_THRESHOLD;

	it('auto defers to the effort count, in both directions', () => {
		expect(resolveAggregateMode('auto', big)).toBe(true);
		expect(resolveAggregateMode('auto', small)).toBe(false);
	});

	it('on and off override the count, in both directions', () => {
		// These two are exactly what a stored boolean could not represent alongside 'auto'.
		expect(resolveAggregateMode('off', big)).toBe(false);
		expect(resolveAggregateMode('on', small)).toBe(true);
	});
});

describe('defaultHighlightedEffortIndices', () => {
	it('picks the top 3 plus the most recent', () => {
		// Ranks 0,1,2 + the latest date (rank 4) — not the last row.
		const set = [row(0, '2026-01-01'), row(1, '2026-01-02'), row(2, '2026-01-03'),
			row(3, '2026-01-04'), row(4, '2026-06-01'), row(5, '2026-01-05')];
		expect(defaultHighlightedEffortIndices(set, false).sort((a, b) => a - b)).toEqual([0, 1, 2, 4]);
	});

	it('deduplicates when the most recent is already in the top 3', () => {
		const set = [row(0, '2026-06-01'), row(1, '2026-01-02'), row(2, '2026-01-03')];
		expect(defaultHighlightedEffortIndices(set, false).sort((a, b) => a - b)).toEqual([0, 1, 2]);
	});

	it('skips excluded efforts', () => {
		const set = [row(0, '2026-01-01', true), row(1, '2026-01-02'), row(2, '2026-01-03')];
		expect(defaultHighlightedEffortIndices(set, false).sort((a, b) => a - b)).toEqual([1, 2]);
	});

	// Contract changed 2026-08-04: aggregate used to open with NO individual lines. Zero highlights
	// also meant an empty hover readout, so the leader is now seeded — one reference line over the band.
	it('highlights first place only when the page opens in aggregate mode', () => {
		expect(defaultHighlightedEffortIndices(rows(AGGREGATE_AUTO_THRESHOLD + 1), true)).toEqual([0]);
	});

	it('falls to the best-ranked ACTIVE effort in aggregate mode when rank 1 is excluded', () => {
		const set = [row(0, '2026-01-01', true), row(1, '2026-01-02'), row(2, '2026-01-03')];
		expect(defaultHighlightedEffortIndices(set, true)).toEqual([1]);
	});

	it('returns nothing in aggregate mode for an empty or fully excluded set', () => {
		expect(defaultHighlightedEffortIndices([], true)).toEqual([]);
		expect(defaultHighlightedEffortIndices([row(0, '2026-01-01', true)], true)).toEqual([]);
	});

	/**
	 * The regression the signature change exists to prevent. This function used to re-derive aggregate
	 * from the row count, so once the mode became storable, a user who turned aggregate OFF on a
	 * 40-effort segment got an empty highlight set — a comparison chart with nothing drawn on it. The
	 * caller resolves the mode once and passes the answer; nothing derives it twice.
	 */
	it('still seeds the highlights on a big segment when aggregate was turned off', () => {
		const many = rows(AGGREGATE_AUTO_THRESHOLD + 30);
		expect(defaultHighlightedEffortIndices(many, false).length).toBeGreaterThan(0);
	});

	it('still seeds the default just below the aggregate threshold', () => {
		expect(defaultHighlightedEffortIndices(rows(AGGREGATE_AUTO_THRESHOLD), false).length).toBeGreaterThan(0);
	});

	it('returns nothing for an empty or fully excluded set', () => {
		expect(defaultHighlightedEffortIndices([], false)).toEqual([]);
		expect(defaultHighlightedEffortIndices([row(0, '2026-01-01', true)], false)).toEqual([]);
	});
});
