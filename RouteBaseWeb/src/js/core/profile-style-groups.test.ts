// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { DEFAULT_DOT_COLOR, groupByStyle } from './profile-style-groups';

/** Pure — no canvas, no DOM, so this file runs in the node environment. */
describe('groupByStyle', () => {
	const finishedNone = [0, 0, 0, 0];

	it('buckets dots sharing a colour into one group, preserving index order', () => {
		const groups = groupByStyle(['#a', '#b', '#a', '#b'], finishedNone, null, null, null, 4);

		expect(groups).toHaveLength(2);
		expect(groups.map((g) => g.color)).toEqual(['#a', '#b']);
		expect(groups[0]?.indices).toEqual([0, 2]);
		expect(groups[1]?.indices).toEqual([1, 3]);
	});

	it('splits the same colour by dimmed and by finished — they paint at different alphas', () => {
		// bright names the KEPT dots, so 0 and 2 are lit and 1 and 3 dim.
		const groups = groupByStyle(['#a', '#a', '#a', '#a'], [0, 0, 1, 1], new Set([0, 2]), null, null, 4);

		expect(groups).toHaveLength(4);
		// Dimmed groups come FIRST: the array is in DRAW order now, not insertion order.
		expect(groups.map((g) => [g.isDim, g.isFinished, g.indices])).toEqual([
			[true, false, [1]],
			[true, true, [3]],
			[false, false, [0]],
			[false, true, [2]],
		]);
	});

	it('returns groups in draw order — dimmed under the field, emphasised on top of it', () => {
		// 2 is dimmed (bright drops it), 1 is emphasised, 0 is a plain dot.
		const groups = groupByStyle(['#a', '#a', '#a'], [0, 0, 0], new Set([0, 1]), null, new Set([1]), 3);

		expect(groups.map((g) => g.indices)).toEqual([[2], [0], [1]]);
		expect(groups.map((g) => g.isEmphasised)).toEqual([false, false, true]);
	});

	it('dimming beats emphasis — a rider the filter dropped recedes even if he is emphasised', () => {
		const groups = groupByStyle(['#a', '#a'], [0, 0], new Set([0]), null, new Set([0, 1]), 2);

		const dimmed = groups.find((g) => g.isDim);
		expect(dimmed?.indices).toEqual([1]);
		expect(dimmed?.isEmphasised).toBe(false);
	});

	it('drops dots outside the visible set entirely', () => {
		const groups = groupByStyle(['#a', '#a', '#a', '#a'], finishedNone, null, new Set([1, 3]), null, 4);

		expect(groups).toHaveLength(1);
		expect(groups[0]?.indices).toEqual([1, 3]);
	});

	it('a null bright set means nothing is dimmed — not that everything is', () => {
		const groups = groupByStyle(['#a', '#a'], [0, 0], null, null, null, 2);

		expect(groups).toHaveLength(1);
		expect(groups[0]?.isDim).toBe(false);
	});

	it('falls back to the default colour for a short or sparse colours array', () => {
		const groups = groupByStyle([], finishedNone, null, null, null, 2);

		expect(groups).toHaveLength(1);
		expect(groups[0]?.color).toBe(DEFAULT_DOT_COLOR);
		expect(groups[0]?.indices).toEqual([0, 1]);
	});

	it('reads exactly `count` dots, ignoring any tail the caller has not filled', () => {
		const groups = groupByStyle(['#a', '#a', '#b'], [0, 0, 0], null, null, null, 2);

		expect(groups).toHaveLength(1);
		expect(groups[0]?.indices).toEqual([0, 1]);
	});
});
