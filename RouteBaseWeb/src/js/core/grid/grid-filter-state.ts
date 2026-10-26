/**
 * Single source of truth for a grid's filter + sort + page state.
 *
 * Pure state container with a change-subscription — it knows nothing about the DOM, the URL, or the
 * server. Consumers (grid-view, the page integration) subscribe to changes and decide what to do
 * (fetch data, mirror to the URL, etc.). Mutations are coalesced so a burst of edits fires once.
 */

import type { GridFilterValue, GridFilterValues, GridQuery, GridSort } from './grid-types';
import { isFilterValueEmpty } from './grid-types';
import { must } from '../invariant';

export class GridFilterState {
	private filters: GridFilterValues = {};
	/** Sort order, primary first. Single-sort grids keep this length 1; multi-sort grids may grow it. */
	private sorts: GridSort[];
	private page = 1;
	private readonly pageSize: number;
	private readonly defaultSort: GridSort;
	/** Filter values restored by clearFilters()/silentReset() — the grid's "home" state, not empty. */
	private readonly defaultFilters: GridFilterValues;
	private readonly listeners = new Set<(q: GridQuery) => void>();
	private notifyScheduled = false;

	constructor(defaultSort: GridSort, pageSize: number, initial?: Partial<GridQuery>, defaultFilters?: GridFilterValues) {
		this.defaultSort = { ...defaultSort };
		this.sorts = [{ ...defaultSort }];
		this.pageSize = pageSize;
		this.defaultFilters = defaultFilters ? structuredClone(defaultFilters) : {};
		if (initial?.filters) this.filters = { ...initial.filters };
		// Prefer a multi-sort `sorts` array if supplied, else fall back to the single `sort`.
		if (initial?.sorts && initial.sorts.length) this.sorts = initial.sorts.map((s) => ({ ...s }));
		else if (initial?.sort) this.sorts = [{ ...initial.sort }];
		if (initial?.page) this.page = initial.page;
	}

	/** Returns an unsubscriber (ShapeStore convention); page-lifetime consumers may ignore it. */
	subscribe(fn: (q: GridQuery) => void): () => void {
		this.listeners.add(fn);
		return () => this.listeners.delete(fn);
	}

	snapshot(): GridQuery {
		// Deep-ish copy so subscribers can't mutate internal state.
		return {
			filters: structuredClone(this.filters),
			sort: { ...must(this.sorts[0], 'sorts is never empty (class invariant)') },
			sorts: this.sorts.map((s) => ({ ...s })),
			page: this.page,
			pageSize: this.pageSize,
		};
	}

	getFilter(key: string): GridFilterValue | undefined {
		return this.filters[key];
	}

	/** Sets/clears a column's filter value and resets to page 1. No-op if unchanged. */
	setFilter(key: string, value: GridFilterValue): void {
		const next = isFilterValueEmpty(value) ? undefined : value;
		const prev = this.filters[key];
		if (JSON.stringify(prev) === JSON.stringify(next)) return;
		if (next === undefined) delete this.filters[key];
		else this.filters[key] = next;
		this.page = 1;
		this.scheduleNotify();
	}

	/**
	 * Restores filters to the grid's default "home" state (which may be non-empty, e.g. owner=Me) and resets
	 * to page 1. Sort is kept. No-op when filters already equal the default.
	 */
	clearFilters(): void {
		const next = structuredClone(this.defaultFilters);
		if (JSON.stringify(this.filters) === JSON.stringify(next)) return;
		this.filters = next;
		this.page = 1;
		this.scheduleNotify();
	}

	/**
	 * Restores default filters + default sort + page 1 WITHOUT notifying subscribers — the caller drives the
	 * re-render and a single refresh (used by GridView's "Reset all", which also resets layout in one pass).
	 */
	silentReset(): void {
		this.filters = structuredClone(this.defaultFilters);
		this.sorts = [{ ...this.defaultSort }];
		this.page = 1;
	}

	/** True when the sort is a single column at the grid's default (used to no-op resetSort). */
	private isDefaultSort(): boolean {
		const only = this.sorts.length === 1 ? this.sorts[0] : undefined;
		return (
			only !== undefined &&
			only.column === this.defaultSort.column &&
			only.direction === this.defaultSort.direction
		);
	}

	/** Restores sort to the grid's default (single column) and resets to page 1. No-op when already at the default. */
	resetSort(): void {
		if (this.isDefaultSort()) return;
		this.sorts = [{ ...this.defaultSort }];
		this.page = 1;
		this.scheduleNotify();
	}

	/**
	 * Toggles sort on a column.
	 *  - non-additive (plain click): collapse to a single sort on `column`. Same column flips direction;
	 *    a new column starts ascending.
	 *  - additive (shift/ctrl-click, multi-sort grids only): add `column` as a secondary sort, or flip it if
	 *    already present. Flipping an already-descending level removes it, so users can drop a sort level.
	 */
	toggleSort(column: string, additive = false): void {
		if (additive) {
			const idx = this.sorts.findIndex((s) => s.column === column);
			if (idx === -1) {
				this.sorts = [...this.sorts, { column, direction: 'asc' }];
			} else if (must(this.sorts[idx], 'findIndex hit (guarded above)').direction === 'asc') {
				this.sorts = this.sorts.map((s, i) => (i === idx ? { ...s, direction: 'desc' as const } : s));
			} else {
				// asc → desc → off, but never drop the last remaining level (keep at least one sort).
				const next = this.sorts.filter((_, i) => i !== idx);
				this.sorts = next.length ? next : [{ column, direction: 'asc' }];
			}
		} else {
			// Collapse to a single sort: flip when the column was already the primary (also with extra
			// multi-sort levels active — dropping them alone shouldn't eat the flip), else start ascending.
			const primary = must(this.sorts[0], 'sorts is never empty (class invariant)');
			const direction =
				primary.column === column ? (primary.direction === 'asc' ? 'desc' : 'asc') : ('asc' as const);
			this.sorts = [{ column, direction }];
		}
		this.page = 1;
		this.scheduleNotify();
	}

	get currentSorts(): GridSort[] {
		return this.sorts.map((s) => ({ ...s }));
	}

	private scheduleNotify(): void {
		if (this.notifyScheduled) return;
		this.notifyScheduled = true;
		queueMicrotask(() => {
			this.notifyScheduled = false;
			const q = this.snapshot();
			this.listeners.forEach((fn) => fn(q));
		});
	}
}
