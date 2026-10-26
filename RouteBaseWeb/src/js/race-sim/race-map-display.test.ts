// @vitest-environment node
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createRaceMapDisplay, type RaceMapSink } from './race-map-display';

function sink(): RaceMapSink & {
	dimmed: Array<readonly number[] | null>;
	visible: Array<readonly number[] | null>;
	flushes: number;
} {
	const dimmed: Array<readonly number[] | null> = [];
	const visible: Array<readonly number[] | null> = [];
	let flushes = 0;
	return {
		dimmed,
		visible,
		get flushes() { return flushes; },
		setDimmed: (v) => { dimmed.push(v); },
		setVisible: (v) => { visible.push(v); },
		flushNow: () => { flushes++; },
	};
}

describe('createRaceMapDisplay', () => {
	beforeEach(() => vi.useFakeTimers());

	function display(markers: RaceMapSink, mode?: 'all' | 'highlight' | 'hide') {
		return createRaceMapDisplay(markers, { mode, coalesceMs: 150 });
	}

	it('highlight (the default) dims everyone outside the filtered set', () => {
		const m = sink();
		display(m).onFiltered([1, 2], true);

		vi.advanceTimersByTime(150);

		expect(m.dimmed.at(-1)).toEqual([1, 2]);
		expect(m.visible.at(-1)).toBeNull();   // the whole field stays ON the map
	});

	it('an INACTIVE filter does not dim — "no filter" is not "a filter matching everyone"', () => {
		// Otherwise an untouched table paints the entire field grey the moment the page loads.
		const m = sink();
		display(m).onFiltered([0, 1, 2], false);

		vi.advanceTimersByTime(150);

		expect(m.dimmed.at(-1)).toBeNull();
	});

	it('hide isolates the filtered set instead of dimming', () => {
		const m = sink();
		const d = display(m, 'hide');

		d.onFiltered([3], true);
		vi.advanceTimersByTime(150);

		expect(m.visible.at(-1)).toEqual([3]);
		expect(m.dimmed.at(-1)).toBeNull();
	});

	it('all ignores the filter entirely', () => {
		const m = sink();
		const d = display(m, 'all');

		d.onFiltered([3], true);
		vi.advanceTimersByTime(150);

		expect(m.visible.at(-1)).toBeNull();
		expect(m.dimmed.at(-1)).toBeNull();
	});

	it('coalesces a burst of filter keystrokes into ONE map update', () => {
		const m = sink();
		const d = display(m);

		d.onFiltered([1], true);
		vi.advanceTimersByTime(50);
		d.onFiltered([1, 2], true);
		vi.advanceTimersByTime(50);
		d.onFiltered([1, 2, 3], true);
		vi.advanceTimersByTime(150);

		expect(m.flushes).toBe(1);
		expect(m.dimmed.at(-1)).toEqual([1, 2, 3]);   // and it is the LAST value, not the first
	});

	it('a mode flip applies at once — it is a click, not a keystroke', () => {
		const m = sink();
		const d = display(m);
		d.onFiltered([2], true);
		vi.advanceTimersByTime(150);

		d.setMode('hide');

		expect(m.visible.at(-1)).toEqual([2]);   // no timer advance needed
	});

	it('switching OUT of hide restores the whole field', () => {
		const m = sink();
		const d = display(m, 'hide');
		d.onFiltered([2], true);
		vi.advanceTimersByTime(150);

		d.setMode('all');

		// Nulling the layer filter here is not redundant — it is what undoes the previous hide.
		expect(m.visible.at(-1)).toBeNull();
	});

	it('a mode flip swallows a pending filter update rather than firing behind it', () => {
		const m = sink();
		const d = display(m);

		d.onFiltered([1], true);   // schedules
		d.setMode('hide');         // applies immediately, cancelling the pending timer
		vi.advanceTimersByTime(300);

		expect(m.flushes).toBe(1);
	});

	it('destroy cancels a pending update', () => {
		const m = sink();
		const d = display(m);

		d.onFiltered([1], true);
		d.destroy();
		vi.advanceTimersByTime(300);

		expect(m.flushes).toBe(0);
	});
});
