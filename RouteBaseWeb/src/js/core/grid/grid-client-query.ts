/**
 * Schema-driven client-side filtering + sorting.
 *
 * Derives the in-memory predicate and comparator for a column from what the schema already declares
 * (`getValue` + the resolved filter type), so page integrations no longer hand-write per-column
 * `applyFilters`/`applySort` switches. Used by the in-memory data source and by grid-view's
 * sort-only fast path (reordering already-loaded rows without a data-source round-trip).
 */

import type { GridColumn, GridFilterValues, GridSort } from './grid-types';
import { isFilterValueEmpty, resolveColumnFilter } from './grid-types';
import { parseRanges } from './grid-filter-parse';
import { must } from '../invariant';
import { matchesCombo, matchesDateRange, matchesRanges, matchesSet, matchesText } from './grid-predicates';

/** Generic value comparator: nulls sort last regardless of direction; strings use base-sensitivity locale order. */
export function compareValues(a: number | string | null, b: number | string | null): number {
	if (a == null && b == null) return 0;
	if (a == null) return 1;
	if (b == null) return -1;
	if (typeof a === 'number' && typeof b === 'number') return a - b;
	return String(a).localeCompare(String(b), undefined, { sensitivity: 'base' });
}

/** True when every sort level maps to a column with a `getValue` accessor (→ client-sortable). */
export function canSortClientSide<TRow>(columns: GridColumn<TRow>[], sorts: GridSort[]): boolean {
	return sorts.every((s) => columns.find((c) => c.key === s.column)?.getValue != null);
}

/**
 * Stable multi-key sort over `sorts` (primary first) using each column's `getValue`; an index
 * tiebreak keeps equal rows in their input order. Columns without `getValue` compare equal.
 */
export function sortRowsBySchema<TRow>(columns: GridColumn<TRow>[], rows: TRow[], sorts: GridSort[]): TRow[] {
	const accessors = sorts.flatMap((s) => {
		const get = columns.find((c) => c.key === s.column)?.getValue;
		return get ? [{ dir: s.direction === 'asc' ? 1 : -1, get }] : [];
	});
	if (accessors.length === 0) return rows;

	const indexed = rows.map((row, i) => ({ row, i }));
	indexed.sort((x, y) => {
		for (const { dir, get } of accessors) {
			const a = get(x.row);
			const b = get(y.row);
			// Nulls last regardless of direction (compareValues' contract) — outside the dir flip,
			// which would otherwise float null rows to the top of every descending sort.
			if (a == null || b == null) {
				if (a == null && b == null) continue;
				return a == null ? 1 : -1;
			}
			const cmp = compareValues(a, b);
			if (cmp !== 0) return cmp * dir;
		}
		return x.i - y.i;
	});
	return indexed.map((e) => e.row);
}

/**
 * Filters rows against the active filter values, deriving each column's predicate from its resolved
 * filter type + `getValue`. A column's `clientFilter` (custom semantics) wins over the derived
 * predicate. Filters on columns with neither are ignored (can't be evaluated client-side).
 */
export function filterRowsBySchema<TRow>(columns: GridColumn<TRow>[], rows: TRow[], filters: GridFilterValues): TRow[] {
	const active = Object.entries(filters)
		.filter(([, v]) => !isFilterValueEmpty(v))
		.flatMap(([key, value]) => {
			const col = columns.find((c) => c.key === key);
			return col && (col.clientFilter || (col.getValue && resolveColumnFilter(col))) ? [{ value, col }] : [];
		});
	if (active.length === 0) return rows;

	return rows.filter((row) =>
		active.every(({ col, value }) => {
			if (col.clientFilter) return col.clientFilter(row, value);
			// Admission above: no clientFilter ⇒ getValue + a resolvable filter exist.
			const v = must(col.getValue, 'admitted: getValue-backed filter')(row);
			switch (must(resolveColumnFilter(col), 'admitted: resolvable filter').type) {
				case 'text':
					return matchesText(v == null ? null : String(v), value.text);
				case 'multiselect':
					return matchesSet(v == null ? null : String(v), value.selected);
				case 'combo':
					return matchesCombo(v == null ? null : String(v), value.text, value.selected);
				case 'daterange':
					return matchesDateRange(v == null ? null : String(v), value.from, value.to);
				case 'numberrange':
					return matchesRanges(typeof v === 'number' ? v : null, parseRanges(value.expr));
			}
		}),
	);
}
