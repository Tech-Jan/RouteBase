// @vitest-environment node
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { describe, it, expect } from 'vitest';
import { highlightedEffortIndices } from './segment-stats-aggregate';

// aggregate mode now RECEIVES ctx (createAggregateSystem's dep + highlightedEffortIndices'
// parameter) instead of reaching for the module singleton — so its highlight set, which drives WHICH
// efforts are drawn as individual lines on top of the band, is now unit-testable with a fake context
// (no #segmentStatisticsConfig island, no vi.mock). The set = statsbox-checked ∪ the pinned selection.

function mkCtx(stats: number[], selected: number | null): SegmentStatisticsContext {
	return {
		effortStats: new Set<number>(stats),
		selectedEffortIndex: selected,
	} as unknown as SegmentStatisticsContext;
}

describe('highlightedEffortIndices', () => {
	it('is exactly the statsbox set when nothing is pinned', () => {
		expect([...highlightedEffortIndices(mkCtx([1, 3, 5], null))].sort((a, b) => a - b)).toEqual([1, 3, 5]);
	});

	it('adds the pinned selection to the statsbox set', () => {
		expect([...highlightedEffortIndices(mkCtx([1, 3], 7))].sort((a, b) => a - b)).toEqual([1, 3, 7]);
	});

	it('does not double-count a pinned effort already in the statsbox set', () => {
		const set = highlightedEffortIndices(mkCtx([2, 4], 4));
		expect(set.size).toBe(2);
		expect([...set].sort((a, b) => a - b)).toEqual([2, 4]);
	});

	it('is empty when no efforts are highlighted and none pinned', () => {
		expect(highlightedEffortIndices(mkCtx([], null)).size).toBe(0);
	});
});
