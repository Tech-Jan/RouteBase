// @vitest-environment node
import { describe, expect, it } from 'vitest';
import {
	emptySelection, click, clear, selectionRange, selectionPointCount, isWholeTrack,
} from './edit-selection';

describe('edit-selection', () => {
	it('first click anchors, second completes an inclusive range', () => {
		const anchored = click(emptySelection(), 5);
		expect(anchored.complete).toBe(false);
		expect(anchored.anchor).toBe(5);

		// second click BEFORE the anchor → range auto-swapped to [2..5]
		const completed = click(anchored, 2);
		expect(completed.complete).toBe(true);
		expect(selectionRange(completed)).toEqual({ from: 2, to: 5 });
		expect(selectionPointCount(completed)).toBe(4);
	});

	it('third click starts a new selection', () => {
		const completed = click(click(emptySelection(), 2), 5);
		const restarted = click(completed, 9);
		expect(restarted.anchor).toBe(9);
		expect(restarted.complete).toBe(false);
		expect(selectionRange(restarted)).toBeNull();
	});

	it('clear empties the selection', () => {
		const completed = click(click(emptySelection(), 2), 5);
		expect(clear()).toEqual(emptySelection());
		expect(selectionRange(clear())).toBeNull();
		expect(completed.complete).toBe(true); // clear is pure — original untouched
	});

	it('isWholeTrack true only for [0..count-1]', () => {
		const whole = click(click(emptySelection(), 0), 9);
		expect(isWholeTrack(whole, 10)).toBe(true);
		const partial = click(click(emptySelection(), 0), 8);
		expect(isWholeTrack(partial, 10)).toBe(false);
		expect(isWholeTrack(emptySelection(), 10)).toBe(false); // incomplete
	});

	it('single-point selection reported (from==to)', () => {
		const single = click(click(emptySelection(), 4), 4);
		expect(selectionPointCount(single)).toBe(1);
	});
});
