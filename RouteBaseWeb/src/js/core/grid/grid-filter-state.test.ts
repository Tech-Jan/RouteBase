// @vitest-environment node
import { describe, it, expect, vi } from 'vitest';
import { GridFilterState } from './grid-filter-state';
import { must } from '../invariant';
import type { GridFilterValues, GridQuery } from './grid-types';

const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

function make(defaults?: GridFilterValues): GridFilterState {
	return new GridFilterState({ column: 'date', direction: 'desc' }, 100, undefined, defaults);
}

describe('GridFilterState — filters + notify', () => {
	it('coalesces a burst of edits into one notification with the latest snapshot', async () => {
		const state = make();
		const seen: GridQuery[] = [];
		state.subscribe((q) => seen.push(q));
		state.setFilter('name', { text: 'a' });
		state.setFilter('name', { text: 'ab' });
		state.setFilter('type', { selected: ['Run'] });
		await flush();
		expect(seen).toHaveLength(1);
		const q = must(seen[0], 'one notification');
		expect(q.filters).toEqual({ name: { text: 'ab' }, type: { selected: ['Run'] } });
		expect(q.page).toBe(1);
	});

	it('setFilter with an unchanged or empty value is a no-op', async () => {
		const state = make();
		state.setFilter('name', { text: 'a' });
		await flush();
		const fn = vi.fn();
		state.subscribe(fn);
		state.setFilter('name', { text: 'a' }); // unchanged
		state.setFilter('other', { text: '   ' }); // whitespace-only → empty → no-op
		await flush();
		expect(fn).not.toHaveBeenCalled();
		expect(state.getFilter('other')).toBeUndefined();
	});

	it('clearFilters restores the default "home" filters, not empty', async () => {
		const state = make({ owner: { selected: ['__me__'] } });
		state.setFilter('owner', { selected: ['__everyone__'] });
		state.setFilter('name', { text: 'x' });
		await flush();
		state.clearFilters();
		await flush();
		expect(state.getFilter('owner')).toEqual({ selected: ['__me__'] });
		expect(state.getFilter('name')).toBeUndefined();
	});

	it('subscribe returns an unsubscriber that stops further notifications without affecting other listeners', async () => {
		const state = make();
		const a = vi.fn();
		const b = vi.fn();
		const unsubA = state.subscribe(a);
		state.subscribe(b);
		state.setFilter('name', { text: 'a' });
		await flush();
		expect(a).toHaveBeenCalledTimes(1);
		expect(b).toHaveBeenCalledTimes(1);
		unsubA();
		state.setFilter('name', { text: 'b' });
		await flush();
		expect(a).toHaveBeenCalledTimes(1);
		expect(b).toHaveBeenCalledTimes(2);
	});

	it('snapshot is detached from internal state', () => {
		const state = make();
		state.setFilter('name', { text: 'a' });
		const snap = state.snapshot();
		must(snap.filters.name, 'name filter').text = 'mutated';
		must(snap.sorts[0], 'primary sort').column = 'mutated';
		expect(state.getFilter('name')).toEqual({ text: 'a' });
		expect(must(state.currentSorts[0], 'primary sort').column).toBe('date');
	});
});

describe('GridFilterState — toggleSort', () => {
	it('plain click flips the single-sort column, new column starts ascending', () => {
		const state = make();
		state.toggleSort('date');
		expect(state.currentSorts).toEqual([{ column: 'date', direction: 'asc' }]);
		state.toggleSort('name');
		expect(state.currentSorts).toEqual([{ column: 'name', direction: 'asc' }]);
		state.toggleSort('name');
		expect(state.currentSorts).toEqual([{ column: 'name', direction: 'desc' }]);
	});

	it('additive click adds a level, flips it, then removes it (asc → desc → off)', () => {
		const state = make();
		state.toggleSort('name', true);
		expect(state.currentSorts).toEqual([
			{ column: 'date', direction: 'desc' },
			{ column: 'name', direction: 'asc' },
		]);
		state.toggleSort('name', true);
		expect(state.currentSorts[1]).toEqual({ column: 'name', direction: 'desc' });
		state.toggleSort('name', true);
		expect(state.currentSorts).toEqual([{ column: 'date', direction: 'desc' }]);
	});

	it('never drops the last remaining sort level', () => {
		const state = make();
		state.toggleSort('date', true); // desc → flip? date is primary asc? initial [date desc] → additive flips to asc
		state.toggleSort('date', true); // asc → desc
		state.toggleSort('date', true); // desc → off, but it is the only level → reset to asc
		expect(state.currentSorts).toHaveLength(1);
		expect(must(state.currentSorts[0], 'primary sort').column).toBe('date');
	});

	it('plain click on the primary of a multi-sort flips its direction while collapsing (GR-07)', () => {
		const state = make();
		state.toggleSort('name'); // [name asc]
		state.toggleSort('type', true); // [name asc, type asc]
		state.toggleSort('name'); // collapse + flip
		expect(state.currentSorts).toEqual([{ column: 'name', direction: 'desc' }]);
	});

	it('plain click on a secondary of a multi-sort collapses to it ascending', () => {
		const state = make();
		state.toggleSort('name'); // [name asc]
		state.toggleSort('type', true); // [name asc, type asc]
		state.toggleSort('type');
		expect(state.currentSorts).toEqual([{ column: 'type', direction: 'asc' }]);
	});

	it('resetSort restores the default and no-ops when already there', async () => {
		const state = make();
		state.toggleSort('name');
		state.resetSort();
		expect(state.currentSorts).toEqual([{ column: 'date', direction: 'desc' }]);
		await flush();
		const fn = vi.fn();
		state.subscribe(fn);
		state.resetSort();
		await flush();
		expect(fn).not.toHaveBeenCalled();
	});
});
