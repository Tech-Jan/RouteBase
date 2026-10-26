/**
 * Shared types for the reusable client-rendered data grid.
 *
 * The grid is driven by a single `GridColumn[]` schema: each column declares how it renders
 * (`formatter`), whether/how it can be filtered (`filter`), and whether it can be sorted. The same
 * column `key` is the contract shared with the server (sort param + filter param). Presentation
 * lives only in the `formatter` — the server returns raw row DTOs.
 */

export type GridFilterType = 'text' | 'multiselect' | 'daterange' | 'numberrange' | 'combo';

/** Column data type — drives the default filter control when `filterable` is set. */
export type GridDataType = 'number' | 'string' | 'date';

/**
 * One checkbox option for a `multiselect`/`combo` filter. A plain string is shorthand for
 * `{ value, label }` with both the same (the common case — e.g. activity types). The `{ value, label }`
 * form is used when the wire value differs from the display text (e.g. owner tokens `__me__` → "Me").
 */
export type FilterOption = string | { value: string; label: string };

/** Normalises a {@link FilterOption} to its `{ value, label }` form. */
export function normalizeOption(o: FilterOption): { value: string; label: string } {
	return typeof o === 'string' ? { value: o, label: o } : o;
}

export interface GridColumnFilter {
	type: GridFilterType;
	/** Placeholder for text/number inputs (e.g. `*run*`). */
	placeholder?: string | undefined;
	/** Inline options for a multiselect/combo (when known at render time). */
	options?: FilterOption[] | undefined;
}

/**
 * Turns a column into a row-selection control: a per-row checkbox plus a header "master" checkbox
 * that bulk-selects/clears the *currently filtered* rows (tristate: all/some/none of the filtered set).
 * The grid owns the selection set (keyed by {@link GridViewOptions.getRowId}) and emits changes via
 * {@link GridViewOptions.onSelectionChange}; how the selection is *used* (e.g. intersect with the filter
 * or treat as the single source of truth) is left to the consumer. A selection column should set
 * `sortable: false`, declare no `filter`, and give a fixed `width`; it needs no `formatter`.
 */
export interface GridColumnSelection {
	/** Selection-set id (e.g. 'visibility' | 'statsbox'). */
	key: string;
	/** HTML for the header master control's icon (e.g. `<i class="bi bi-eye"></i>`). */
	headerIcon?: string | undefined;
	/** Tooltip applied to the header master and each per-row checkbox. */
	title?: string | undefined;
}

export interface GridColumn<TRow> {
	/** Stable key — used as the sort column and the filter param name. */
	key: string;
	header: string;
	sortable?: boolean | undefined;
	align?: 'start' | 'end' | 'center' | undefined;
	/** Makes this a selection column (per-row checkbox + filtered-scope master). See {@link GridColumnSelection}. */
	selection?: GridColumnSelection | undefined;
	/** When set, the column only renders if its key is in the grid's active `visibleKeys`. */
	optionalKey?: string | undefined;
	/** The column's data type — combined with `filterable` to derive a default filter control. */
	dataType?: GridDataType | undefined;
	/** Opt a `dataType` column into filtering with the data-type default control (no explicit `filter` needed). */
	filterable?: boolean | undefined;
	/** Explicit per-column filter-row control. Overrides the `dataType`/`filterable` default. */
	filter?: GridColumnFilter | undefined;
	/**
	 * Raw value for client-side sorting/filtering, in the unit the user filters on (e.g. km for a
	 * distance column whose range filter is entered in km). Enables the grid's client-side sort fast
	 * path (a sort-only change reorders loaded rows without a data-source round-trip) and the
	 * schema-driven in-memory data source. Omit → the column can't be sorted/filtered client-side.
	 */
	getValue?: ((row: TRow) => number | string | null) | undefined;
	/**
	 * Custom client-side predicate for filter semantics the generic type-driven matching can't express
	 * (e.g. keyword tokens like Me/Others resolved against row state). Wins over the derived predicate.
	 */
	clientFilter?: ((row: TRow, value: GridFilterValue) => boolean) | undefined;
	/** Renders a cell's inner HTML from the row. Must escape any user-supplied text.
	 *  Required for every column except selection columns (which render their own checkbox). */
	formatter?: ((row: TRow) => string) | undefined;
	/** Extra classes applied to both the header and body cells of this column. */
	cellClass?: string | undefined;
	/** Optional initial column width in px. Omitted → width is measured from content on first render. */
	width?: number | undefined;
	/**
	 * Hide this column from the toolbar's Columns picker even though it is optional (`optionalKey`). Use for
	 * optional columns whose visibility is controlled programmatically rather than by the user (e.g. owner,
	 * shown only in multi-user mode).
	 */
	pickerHidden?: boolean | undefined;
}

/** The default filter control for a data type — numeric → range, date → daterange, string → text. */
export function defaultFilter(dataType: GridDataType): GridColumnFilter {
	switch (dataType) {
		case 'number':
			return { type: 'numberrange' };
		case 'date':
			return { type: 'daterange' };
		case 'string':
			return { type: 'text' };
	}
}

/**
 * The effective filter for a column: an explicit `filter` wins; otherwise a `filterable` column with a
 * `dataType` gets the data-type default. Returns undefined for non-filterable columns.
 */
export function resolveColumnFilter<TRow>(col: GridColumn<TRow>): GridColumnFilter | undefined {
	if (col.filter) return col.filter;
	if (col.filterable && col.dataType) return defaultFilter(col.dataType);
	return undefined;
}

/** Generated shape (G1) with direction refined to the SortDirection vocabulary union — the C#
 *  property is a plain string, the value set is closed (D2). */
export interface GridSort extends Omit<import('../../generated/contracts').GridSort, 'direction'> {
	direction: import('../../generated/vocabularies').SortDirection;
}

/** One column's filter value. Shape depends on the column's filter type. */
export interface GridFilterValue {
	/** text */
	text?: string;
	/** multiselect */
	selected?: string[];
	/** numberrange expression (e.g. `3-6, 8-12`, `>5`) */
	expr?: string;
	/** daterange lower bound */
	from?: string;
	/** daterange upper bound */
	to?: string;
}

/** Filter values keyed by column key. */
export type GridFilterValues = Record<string, GridFilterValue>;

export interface GridQuery {
	filters: GridFilterValues;
	/** Primary sort (always `sorts[0]`). Kept for single-sort consumers (server endpoints, URL mirroring). */
	sort: GridSort;
	/** Full sort order, primary first. Single-sort grids carry a one-element array; multi-sort grids carry more. */
	sorts: GridSort[];
	page: number;
	pageSize: number;
}

export interface GridDataResponse<TRow> {
	rows: TRow[];
	filteredIds: number[];
	total: number;
	/** Grand total of all rows ignoring filters — the denominator for the toolbar count. Defaults to `total`. */
	unfilteredTotal?: number;
}

/** Pluggable backend for grid data — HTTP (server-paged) or in-memory (client, unpaged). */
export interface GridDataSource<TRow> {
	fetch(query: GridQuery): Promise<GridDataResponse<TRow>>;
}

/** True when a filter value carries no active constraint. */
export function isFilterValueEmpty(v: GridFilterValue | undefined): boolean {
	if (!v) return true;
	return (
		!(v.text && v.text.trim()) &&
		!(v.selected && v.selected.length) &&
		!(v.expr && v.expr.trim()) &&
		!(v.from && v.from.trim()) &&
		!(v.to && v.to.trim())
	);
}
