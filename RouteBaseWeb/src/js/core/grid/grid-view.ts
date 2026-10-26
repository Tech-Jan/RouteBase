/**
 * Reusable client-rendered data grid.
 *
 * Renders a `<table>` (header + filter row + body) from a `GridColumn[]` schema and a pluggable
 * {@link GridDataSource}. Owns the {@link GridFilterState}; on any filter/sort/page change it queries
 * the data source, re-renders the body, and fires `onData` (e.g. to drive a map) and `onQueryChange`
 * (e.g. to mirror the URL). Presentation lives entirely in each column's `formatter`.
 */

import { asyncCallback } from '../async-listener';
import { readData, setData } from '../../core/data-attrs';
import type {
	GridColumn,
	GridColumnSelection,
	GridDataResponse,
	GridDataSource,
	GridFilterValue,
	GridFilterValues,
	GridQuery,
	GridSort,
} from './grid-types';
import { GridFilterState } from './grid-filter-state';
import { buildCheckboxPopup, buildFilterRow, closeOpenPopups } from './grid-filter-row';
import { GridColumnSizer } from './grid-column-sizer';
import { GridColumnOrder } from './grid-column-order';
import { canSortClientSide, sortRowsBySchema } from './grid-client-query';
import { escapeHtml } from '../html';

// "Clear filters" glyph: a funnel with a small ✕ badge at its top-right (inherits the button's currentColor).
const FILTER_CLEAR_ICON =
	'<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.8" ' +
	'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
	'<path d="M3 4h10l-4 5v7l-2.4 1.8V9z"/>' +
	'<circle cx="17.5" cy="6.5" r="4.2"/>' +
	'<path d="M15.9 4.9l3.2 3.2M19.1 4.9l-3.2 3.2"/></svg>';

/** Which pieces of the reusable toolbar to render. */
export interface GridToolbarOptions {
	/** Row count, e.g. "42 of 1,203" (filtered of unfiltered). */
	count?: boolean | undefined;
	/** "Reset filters" button — clears all column filters and resets sort to the default. */
	resetFilters?: boolean | undefined;
	/** "Reset all" button — also resets column widths, order, and visible columns to defaults. */
	resetAll?: boolean | undefined;
	/** Columns dropdown — toggles optional (`optionalKey`, non-`pickerHidden`) columns. */
	columns?: boolean | undefined;
}

export interface GridViewOptions<TRow> {
	mount: HTMLElement;
	columns: GridColumn<TRow>[];
	dataSource: GridDataSource<TRow>;
	defaultSort: GridSort;
	pageSize?: number | undefined;
	/** First page rendered immediately (avoids a load round-trip). */
	initialData?: GridDataResponse<TRow> | undefined;
	initialQuery?: Partial<GridQuery> | undefined;
	/** Filter values the grid resets to (not empty) when "Clear filters"/"Reset all" is clicked. Omit → empty. */
	defaultFilters?: GridFilterValues | undefined;
	/** Optional-column keys currently shown. */
	visibleKeys?: Set<string> | undefined;
	/** Where to render the reusable toolbar (count + reset + columns). Page-provided so it sits outside the
	 *  horizontal-scroll wrapper. Omit → no toolbar. */
	toolbarMount?: HTMLElement | undefined;
	/** Which toolbar pieces to show (requires `toolbarMount`). */
	toolbar?: GridToolbarOptions | undefined;
	/** Baseline visible optional columns restored by "Reset all". Defaults to the empty set. */
	defaultVisibleKeys?: Set<string> | undefined;
	/** Called when the Columns picker (or "Reset all") changes the visible optional columns, so the page can persist. */
	onVisibleKeysChange?: ((keys: Set<string>) => void) | undefined;
	/** Formats the toolbar count. Default: `"{filtered}/{total}"` (or just `"{total}"` when unfiltered), localized. */
	countFormatter?: ((filtered: number, total: number) => string) | undefined;
	emptyMessage?: string | undefined;
	tableClass?: string | undefined;
	/**
	 * Controls-only mode: the grid renders its toolbar + header + filter row but **no data rows** — the
	 * results live outside the table (e.g. a card / minimap grid) and are driven by `onData`. Suppresses the
	 * internal body rows and the `Loading…` / `No results` / empty messages (the consumer's own empty-state
	 * handles emptiness). Column widths still apply (measured from the header cells). Default false. */
	externalResults?: boolean | undefined;
	/** Enable drag-to-resize column borders + fixed column widths (kills filter reflow flicker). Default true. */
	resizableColumns?: boolean | undefined;
	/** Enable drag-to-reorder columns (grip handle on each header). Default true. */
	reorderableColumns?: boolean | undefined;
	/** Enable multi-column sort: shift/ctrl-click a header to add a secondary sort level. Default false (single sort). */
	multiSort?: boolean | undefined;
	/** Names this table in the device-pref registry — persists resized column widths + column order across
	 *  reloads (omit → not persisted). See core/device-prefs.ts. */
	persistWidthsKey?: string | undefined;
	onData?: ((res: GridDataResponse<TRow>, q: GridQuery) => void) | undefined;
	/** Mirrors the query (e.g. to the URL). Fired only after the matching data actually rendered, so the
	 *  URL never advertises a query whose fetch failed. */
	onQueryChange?: ((q: GridQuery) => void) | undefined;
	/** Fired when a data-source fetch fails (never for aborted/superseded requests). The grid shows its own
	 *  error row; use this to react downstream (e.g. flag the map's data as stale). */
	onError?: ((err: unknown, q: GridQuery) => void) | undefined;
	/** Stable per-row id — required when any column declares `selection` (keys the selection sets + matches filteredIds). */
	getRowId?: ((row: TRow) => number) | undefined;
	/** Seeds each selection set (keyed by the selection column's `key`). */
	initialSelection?: Record<string, number[]> | undefined;
	/** Fires whenever a selection set changes (per-row checkbox or header master), with the current filtered ids. */
	onSelectionChange?: ((key: string, selectedIds: Set<number>, filteredIds: number[]) => void) | undefined;
	/**
	 * A click anywhere on a row that is not an interactive control. Requires `getRowId`.
	 *
	 * Clicks on a checkbox, button, link or input are NOT row clicks — the cell owns those, and a row
	 * handler that swallowed them would fire twice for every selection toggle.
	 */
	onRowClick?: ((rowId: number, row: TRow) => void) | undefined;
}

// CR2-01: local impl merged into core/html.ts verbatim (now the sole implementation);
// re-export keeps the grid's public import surface (4 external callers) unchanged.
export { escapeHtml };

export class GridView<TRow> {
	private readonly opts: GridViewOptions<TRow>;
	private readonly state: GridFilterState;
	private visibleKeys: Set<string>;

	private readonly table = document.createElement('table');
	private readonly thead = document.createElement('thead');
	private readonly tbody = document.createElement('tbody');
	private sizer?: GridColumnSizer<TRow>;
	private columnOrder?: GridColumnOrder;
	private headerCells: HTMLElement[] = [];
	private readonly sortIcons = new Map<string, HTMLElement>();
	private reqToken = 0;
	/** True while a data-source fetch is in flight (guards selection masters acting on stale filtered ids). */
	private refreshing = false;
	/** Last rendered rows — kept so column reorder can re-render in place without a server round-trip. */
	private lastRows: TRow[] = [];
	/** Query + response of the last successful data render — the baseline the sort-only fast path reorders. */
	private lastQuery?: GridQuery;
	private lastResponse?: GridDataResponse<TRow>;
	/** Row object → its rendered `<tr>`, so a sort-only change moves existing nodes instead of re-parsing HTML. */
	private rowEls = new WeakMap<object, HTMLTableRowElement>();
	/** Key of the column currently being dragged to reorder (between dragstart and drop/dragend). */
	private dragKey?: string | undefined;
	/** Toolbar count element (when the toolbar is enabled). */
	private countEl?: HTMLElement;
	/** Slot holding the Columns dropdown, re-rendered when visible columns change. */
	private columnsSlot?: HTMLElement;
	/** Selection sets keyed by selection-column key. */
	private readonly selection = new Map<string, Set<number>>();
	/** Header master checkbox per selection-column key. */
	private readonly selectionMasters = new Map<string, HTMLInputElement>();
	/** Ids of the rows passing the current filter — the scope the selection masters act on. */
	private currentFilteredIds: number[] = [];

	constructor(opts: GridViewOptions<TRow>) {
		this.opts = opts;
		this.visibleKeys = opts.visibleKeys ?? new Set();
		for (const [key, ids] of Object.entries(opts.initialSelection ?? {})) {
			this.selection.set(key, new Set(ids));
		}
		this.state = new GridFilterState(opts.defaultSort, opts.pageSize ?? 10000, opts.initialQuery, opts.defaultFilters);
		this.build();
		this.buildToolbar();
		this.state.subscribe(asyncCallback('grid state change', (q) => this.onStateChange(q)));

		if (opts.initialData) {
			this.currentFilteredIds = opts.initialData.filteredIds;
			this.lastQuery = this.state.snapshot();
			this.lastResponse = opts.initialData;
			this.renderBody(opts.initialData.rows);
			this.lockColumnWidths();
			this.updateCount(opts.initialData);
			this.updateAllSelectionMasters();
			this.opts.onData?.(opts.initialData, this.state.snapshot());
		} else {
			void this.refresh();
		}
	}

	/** The selection set for a key (created on first use). */
	private selectionSet(key: string): Set<number> {
		let set = this.selection.get(key);
		if (!set) {
			set = new Set();
			this.selection.set(key, set);
		}
		return set;
	}

	/** Columns currently shown (optional columns gated by visibleKeys), in the user's persisted order. */
	private visibleColumns(): GridColumn<TRow>[] {
		const visible = this.opts.columns.filter((c) => !c.optionalKey || this.visibleKeys.has(c.optionalKey));
		return this.columnOrder ? this.columnOrder.apply(visible) : visible;
	}

	/** Reads a column's current filter value (e.g. to mirror the owner scope into a saved segment). */
	getFilter(key: string): GridFilterValue | undefined {
		return this.state.getFilter(key);
	}

	/**
	 * Sets/clears a column's filter value programmatically (e.g. to restore an edited segment's owner scope).
	 * Drives one grid refresh, and — via onData — a downstream map reload, exactly like a user edit would.
	 */
	setFilter(key: string, value: GridFilterValue): void {
		this.state.setFilter(key, value);
		// Rebuild the filter row so the controls reflect the new state — they snapshot state at build
		// time (text inputs + captured `selected` Sets), so without this the UI shows the old value and
		// the user's next edit in that column would push the stale captured value back over this one.
		this.renderHeader();
	}

	/**
	 * Re-runs the current query through the data source, without changing the query state.
	 *
	 * For when an *external* input the data source reads (outside the column-filter state) changed — e.g. the
	 * activities grid's map-drawn shape filter, folded into the API request by its `buildParams`. Drives one
	 * refresh, and via onData a downstream map update, exactly like a filter edit would.
	 */
	reload(): void {
		void this.refresh();
	}

	/**
	 * Re-renders the rows currently on screen, in place.
	 *
	 * For when the row DATA changed but the query did not — e.g. the race results table repaints a
	 * filtered group and its colour swatches must catch up. A full `refresh()` would re-filter and re-sort
	 * to reach the same set of rows in the same order; this just redraws them.
	 */
	redrawRows(): void {
		this.renderBody(this.lastRows);
	}

	/**
	 * Clears a selection set entirely (all ids, regardless of the current filter) and re-renders.
	 *
	 * The header master only toggles the *filtered* rows; this is the global "unselect all" a caller needs
	 * when picks may include rows filtered out of view. Fires onSelectionChange with the now-empty set.
	 */
	clearSelection(key: string): void {
		this.selectionSet(key).clear();
		this.syncSelectionCheckboxes(key);
		this.updateSelectionMaster(key);
		this.opts.onSelectionChange?.(key, new Set(), this.currentFilteredIds);
	}

	/**
	 * Syncs every rendered selection checkbox for `key` against its selection set, in place.
	 * Selection state lives only in the checkboxes' `checked` — rebuilding the whole tbody
	 * (every formatter re-run per row) just to flip them was the cost of select-all on big grids.
	 */
	private syncSelectionCheckboxes(key: string): void {
		const set = this.selectionSet(key);
		for (const cb of this.tbody.querySelectorAll<HTMLInputElement>('.grid-sel-cb')) {
			if (readData(cb, 'selKey') === key) cb.checked = set.has(Number(readData(cb, 'rowId')));
		}
	}

	/** Updates which optional columns are shown and re-renders. */
	setVisibleKeys(keys: Set<string>): void {
		// Only an *added* column needs a re-measure pass (it must be fitted in — otherwise it overflows until
		// a full reload). Removing columns keeps the survivors' widths, skipping the layout-thrash measure.
		const added = [...keys].some((k) => !this.visibleKeys.has(k));
		// A hidden column loses its filter-row cell, so keeping its filter would constrain the rows
		// invisibly (nothing to see or clear). Clear it before the header rebuild reads the state.
		for (const col of this.opts.columns) {
			if (col.optionalKey && this.visibleKeys.has(col.optionalKey) && !keys.has(col.optionalKey)) {
				this.state.setFilter(col.key, {});
			}
		}
		this.visibleKeys = new Set(keys);
		if (added) this.sizer?.clearMeasuredWidths();
		this.renderHeader();
		void this.refresh();
	}

	private build(): void {
		this.table.className = `grid-table ${this.opts.tableClass ?? ''}`.trim();
		this.table.append(this.thead, this.tbody);

		this.opts.mount.innerHTML = '';
		this.opts.mount.appendChild(this.table);

		if (this.opts.resizableColumns !== false) {
			this.sizer = new GridColumnSizer<TRow>(this.table, this.opts.persistWidthsKey);
		}
		if (this.opts.reorderableColumns !== false) {
			this.columnOrder = new GridColumnOrder(this.opts.persistWidthsKey);
		}

		// One delegated listener for all per-row selection checkboxes (they're re-created on every body render).
		this.tbody.addEventListener('change', (e) => {
			const cb = (e.target as HTMLElement).closest<HTMLInputElement>('.grid-sel-cb');
			if (!cb) return;
			const key = readData(cb, 'selKey');
			const id = Number(readData(cb, 'rowId'));
			if (!key || Number.isNaN(id)) return;
			const set = this.selectionSet(key);
			if (cb.checked) set.add(id);
			else set.delete(id);
			this.updateSelectionMaster(key);
			this.opts.onSelectionChange?.(key, new Set(set), this.currentFilteredIds);
		});

		// One delegated listener for whole-row clicks. Delegated, not per-row: the body is rebuilt on
		// every render, so per-row handlers would leak one listener per row per keystroke.
		if (this.opts.onRowClick) {
			this.tbody.addEventListener('click', (e) => {
				const target = e.target as HTMLElement;
				// The cell owns its controls. Without this a click on a selection checkbox would ALSO be a
				// row click, and every toggle would fire two different actions.
				if (target.closest('input, button, a, select, textarea, label')) return;

				const tr = target.closest<HTMLTableRowElement>('tr[data-row-id]');
				if (!tr || !this.tbody.contains(tr)) return;

				const id = Number(readData(tr, 'rowId'));
				const row = this.lastRows.find((r) => this.opts.getRowId?.(r) === id);
				if (Number.isNaN(id) || row === undefined) return;

				this.opts.onRowClick?.(id, row);
			});
		}

		this.renderHeader();
	}

	private renderHeader(): void {
		closeOpenPopups(this.thead); // wiping an open popup without closing it leaks its global listeners
		this.thead.innerHTML = '';
		this.sortIcons.clear();
		this.headerCells = [];
		const cols = this.visibleColumns();

		this.selectionMasters.clear();
		const headerRow = document.createElement('tr');
		cols.forEach((col, i) => {
			const th = document.createElement('th');
			if (col.align === 'end') th.classList.add('text-end');
			if (col.cellClass) th.classList.add(col.cellClass);

			// Selection columns render a master control instead of a label/sort/resize/reorder header.
			if (col.selection) {
				this.renderSelectionHeader(th, col.selection);
				this.headerCells.push(th);
				headerRow.appendChild(th);
				return;
			}

			th.append(document.createTextNode(col.header + ' '));

			if (col.sortable) {
				th.classList.add('grid-th-sort');
				if (this.opts.multiSort) th.title = 'Click to sort · Shift-click to add a sort level';
				const icon = document.createElement('span');
				icon.className = 'grid-sort-icon';
				th.appendChild(icon);
				this.sortIcons.set(col.key, icon);
				th.addEventListener('click', (ev) =>
					this.state.toggleSort(col.key, this.opts.multiSort === true && (ev.shiftKey || ev.ctrlKey)),
				);
			}
			// Resize handle on every column except the last (no column to its right to push against).
			if (this.sizer && i < cols.length - 1) this.sizer.attachHandle(th, col, i);
			if (this.columnOrder) this.attachReorder(th, col);
			this.headerCells.push(th);
			headerRow.appendChild(th);
		});

		this.thead.appendChild(headerRow);
		this.thead.appendChild(buildFilterRow(cols, this.state));
		this.sizer?.syncColumns(cols);
		this.updateSortIcons();
		this.updateAllSelectionMasters();
	}

	/** Builds a selection column's header: an icon over a tristate master checkbox scoped to the filtered rows. */
	private renderSelectionHeader(th: HTMLElement, sel: GridColumnSelection): void {
		const wrap = document.createElement('span');
		wrap.className = 'grid-sel-master';
		if (sel.title) wrap.title = sel.title;
		if (sel.headerIcon) {
			const icon = document.createElement('span');
			icon.className = 'grid-sel-master-icon';
			// eslint-disable-next-line no-unsanitized/property -- headerIcon is app-authored config markup by grid contract (all consumers verified in S2 census)
			icon.innerHTML = sel.headerIcon;
			wrap.appendChild(icon);
		}
		const master = document.createElement('input');
		master.type = 'checkbox';
		master.className = 'grid-sel-master-cb';
		// preventDefault so we drive checked/indeterminate ourselves from the (possibly partial) filtered set.
		master.addEventListener('click', (e) => {
			e.preventDefault();
			this.toggleSelectAll(sel.key);
		});
		wrap.appendChild(master);
		th.appendChild(wrap);
		this.selectionMasters.set(sel.key, master);
	}

	/** Master click: if every filtered row is already selected, clear them; otherwise select them all. */
	private toggleSelectAll(key: string): void {
		if (this.refreshing) return; // filtered ids are stale while a refresh is in flight
		const set = this.selectionSet(key);
		const allSelected = this.currentFilteredIds.length > 0 && this.currentFilteredIds.every((id) => set.has(id));
		for (const id of this.currentFilteredIds) {
			if (allSelected) set.delete(id);
			else set.add(id);
		}
		this.syncSelectionCheckboxes(key); // flip the per-row checkboxes in place — no tbody rebuild
		this.updateSelectionMaster(key);
		this.opts.onSelectionChange?.(key, new Set(set), this.currentFilteredIds);
	}

	/** Reflects the filtered rows' selection in a master checkbox: all → checked, none → unchecked, some → indeterminate. */
	private updateSelectionMaster(key: string): void {
		const master = this.selectionMasters.get(key);
		if (!master) return;
		const set = this.selectionSet(key);
		const total = this.currentFilteredIds.length;
		const checked = this.currentFilteredIds.reduce((n, id) => n + (set.has(id) ? 1 : 0), 0);
		master.checked = total > 0 && checked === total;
		master.indeterminate = checked > 0 && checked < total;
	}

	private updateAllSelectionMasters(): void {
		for (const key of this.selectionMasters.keys()) this.updateSelectionMaster(key);
	}

	/**
	 * Add a drag grip to a header cell so the user can reorder columns. The grip (not the whole header) is the
	 * drag source so a normal header click still sorts; the header cell itself is the drop target. Order is
	 * applied client-side (re-render from cached rows) — no server round-trip — and persisted per-table.
	 */
	private attachReorder(th: HTMLElement, col: GridColumn<TRow>): void {
		// Mark reorderable headers so the touch path can hit-test drop targets via elementFromPoint.
		setData(th, 'colKey', col.key);
		const grip = document.createElement('span');
		grip.className = 'grid-col-grip';
		grip.setAttribute('aria-hidden', 'true');
		grip.title = 'Drag to reorder column';
		grip.textContent = '⠿';
		grip.draggable = true;
		// Don't let grabbing the grip trigger the header's sort.
		grip.addEventListener('click', (e) => e.stopPropagation());
		grip.addEventListener('dragstart', (e) => {
			this.dragKey = col.key;
			th.classList.add('grid-col-dragging');
			e.dataTransfer?.setData('text/plain', col.key);
			if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
		});
		grip.addEventListener('dragend', () => {
			th.classList.remove('grid-col-dragging');
			this.clearDropMarkers();
			this.dragKey = undefined;
		});
		this.attachTouchReorder(grip, th, col);
		// Inline at the start of the header (before the label) so it never overlaps the sort icon or resize handle.
		th.insertBefore(grip, th.firstChild);

		th.addEventListener('dragover', (e) => {
			if (!this.dragKey || this.dragKey === col.key) return;
			e.preventDefault();
			if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
			this.markDropTarget(th, this.isBefore(th, e.clientX));
		});
		th.addEventListener('dragleave', () => th.classList.remove('grid-col-drop-before', 'grid-col-drop-after'));
		th.addEventListener('drop', (e) => {
			if (!this.dragKey || this.dragKey === col.key) return;
			e.preventDefault();
			this.reorderColumn(this.dragKey, col.key, this.isBefore(th, e.clientX));
		});
	}

	/**
	 * Touch/pen reorder: native HTML5 drag events never fire from touch input, so the grip also runs a
	 * pointer-capture drag — drop targets are hit-tested via elementFromPoint against the header cells
	 * marked with data-col-key. Mouse input keeps the native DnD path above.
	 */
	private attachTouchReorder(grip: HTMLElement, th: HTMLElement, col: GridColumn<TRow>): void {
		grip.addEventListener('pointerdown', (e) => {
			if (e.pointerType === 'mouse') return;
			e.preventDefault();
			e.stopPropagation();
			grip.setPointerCapture(e.pointerId);
			this.dragKey = col.key;
			th.classList.add('grid-col-dragging');
			let target: HTMLElement | null = null;
			let before = false;

			const onMove = (ev: PointerEvent): void => {
				const hit = document
					.elementFromPoint(ev.clientX, ev.clientY)
					?.closest<HTMLElement>('th[data-col-key]');
				if (!hit || readData(hit, 'colKey') === col.key) {
					if (target) this.clearDropMarkers();
					target = null;
					return;
				}
				target = hit;
				before = this.isBefore(hit, ev.clientX);
				this.markDropTarget(hit, before);
			};
			const cleanup = (): void => {
				grip.removeEventListener('pointermove', onMove);
				grip.removeEventListener('pointerup', onUp);
				grip.removeEventListener('pointercancel', onCancel);
				th.classList.remove('grid-col-dragging');
				this.clearDropMarkers();
				this.dragKey = undefined;
			};
			const onUp = (): void => {
				const targetKey = target ? readData(target, 'colKey') : null;
				const dropBefore = before;
				cleanup();
				if (targetKey) this.reorderColumn(col.key, targetKey, dropBefore);
			};
			const onCancel = (): void => cleanup();

			grip.addEventListener('pointermove', onMove);
			grip.addEventListener('pointerup', onUp);
			grip.addEventListener('pointercancel', onCancel);
		});
	}

	/** True when pointer x is on the left half of the header cell (→ drop before it, else after). */
	private isBefore(th: HTMLElement, clientX: number): boolean {
		const rect = th.getBoundingClientRect();
		return clientX < rect.left + rect.width / 2;
	}

	private markDropTarget(th: HTMLElement, before: boolean): void {
		this.clearDropMarkers();
		th.classList.add(before ? 'grid-col-drop-before' : 'grid-col-drop-after');
	}

	private clearDropMarkers(): void {
		this.thead
			.querySelectorAll('.grid-col-drop-before, .grid-col-drop-after')
			.forEach((el) => el.classList.remove('grid-col-drop-before', 'grid-col-drop-after'));
	}

	/** Move `fromKey` to just before/after `targetKey`, persist the new order, and re-render in place. */
	private reorderColumn(fromKey: string, targetKey: string, before: boolean): void {
		if (!this.columnOrder || fromKey === targetKey) return;
		// Reorder over *all* columns (incl. hidden) so a hidden column keeps its slot if toggled on later.
		const keys = this.columnOrder.apply(this.opts.columns).map((c) => c.key).filter((k) => k !== fromKey);
		let idx = keys.indexOf(targetKey);
		if (idx === -1) return;
		if (!before) idx += 1;
		keys.splice(idx, 0, fromKey);
		this.columnOrder.set(keys);

		this.renderHeader();
		this.renderBody(this.lastRows);
		this.lockColumnWidths();
	}

	/** After a render with real rows, lock/measure column widths so later filters don't reflow them. */
	private lockColumnWidths(): void {
		this.sizer?.ensureWidths(this.visibleColumns(), this.headerCells);
	}

	private updateSortIcons(): void {
		const sorts = this.state.currentSorts;
		const rank = new Map(sorts.map((s, i) => [s.column, { dir: s.direction, order: i }]));
		const multi = sorts.length > 1;
		for (const [key, el] of this.sortIcons) {
			const active = rank.get(key);
			if (!active) {
				el.className = 'grid-sort-icon grid-sort-icon--none';
				el.textContent = '⇅';
			} else {
				const arrow = active.dir === 'asc' ? '↑' : '↓';
				el.className = `grid-sort-icon grid-sort-icon--${active.dir}`;
				// Show a 1-based order badge only when more than one column is sorted (keeps single-sort unchanged).
				// eslint-disable-next-line no-unsanitized/property -- arrow is an internal literal; order is numeric
				el.innerHTML = multi
					? `${arrow}<sup class="grid-sort-rank">${active.order + 1}</sup>`
					: arrow;
			}
		}
	}

	private async onStateChange(q: GridQuery): Promise<void> {
		this.updateSortIcons();
		if (this.trySortFastPath(q)) return;
		await this.refresh();
	}

	/**
	 * Sort-only fast path: when only the sort changed and the full filtered set is already loaded,
	 * reorder the loaded rows client-side (schema `getValue` comparators) and move the existing row
	 * nodes — no data-source round-trip, no HTML re-parse. Returns false → caller does a full refresh.
	 */
	private trySortFastPath(q: GridQuery): boolean {
		const prev = this.lastQuery;
		const res = this.lastResponse;
		if (!prev || !res) return false;
		if (this.refreshing) return false; // baseline is about to be replaced — let the fetch win
		if (res.rows.length < res.total) return false; // paged: the server owns the order
		if (q.page !== prev.page || q.pageSize !== prev.pageSize) return false;
		if (JSON.stringify(q.filters) !== JSON.stringify(prev.filters)) return false;
		if (!canSortClientSide(this.opts.columns, q.sorts)) return false;

		const sorted = sortRowsBySchema(this.opts.columns, res.rows, q.sorts);
		if (!this.reorderBody(sorted)) return false;

		const sortedRes = { ...res, rows: sorted };
		this.lastQuery = q;
		this.lastResponse = sortedRes;
		this.opts.onData?.(sortedRes, q);
		this.opts.onQueryChange?.(q);
		return true;
	}

	private async refresh(): Promise<void> {
		const token = ++this.reqToken;
		const q = this.state.snapshot();
		this.refreshing = true;
		this.renderLoading();
		try {
			const res = await this.opts.dataSource.fetch(q);
			if (token !== this.reqToken) return; // a newer request superseded this one
			this.refreshing = false;
			this.tbody.classList.remove('grid-tbody--loading');
			this.currentFilteredIds = res.filteredIds;
			this.lastQuery = q;
			this.lastResponse = res;
			this.renderBody(res.rows);
			this.lockColumnWidths();
			this.updateCount(res);
			this.updateAllSelectionMasters();
			this.opts.onData?.(res, q);
			this.opts.onQueryChange?.(q); // mirror only queries whose data actually rendered
		} catch (err) {
			if (token !== this.reqToken) return;
			this.refreshing = false;
			if ((err as DOMException)?.name === 'AbortError') return; // superseded request — stay silent
			this.tbody.classList.remove('grid-tbody--loading');
			this.renderMessage(`Error loading data: ${err instanceof Error ? err.message : String(err)}`, true);
			this.opts.onError?.(err, q);
		}
	}

	/**
	 * Non-destructive while data rows are already on screen: we keep them in place (just dim via a class) so
	 * the table height — and therefore the page scroll position — stays stable across a filter/sort refresh.
	 * Only when there's nothing to keep (first load, or after an empty/message state) do we show "Loading…".
	 */
	private renderLoading(): void {
		if (this.opts.externalResults) return; // results render outside the table; no in-table loading row
		const hasRows = this.tbody.querySelector('tr:not(.grid-row-message)') !== null;
		if (hasRows) this.tbody.classList.add('grid-tbody--loading');
		else this.renderMessage('Loading…', false);
	}

	private renderMessage(text: string, isError: boolean): void {
		const cols = this.visibleColumns().length;
		// eslint-disable-next-line no-unsanitized/property -- text escapeHtml'd; cols is a numeric length; class ternary is literal
		this.tbody.innerHTML = `<tr class="grid-row-message${isError ? ' grid-row-error' : ''}"><td colspan="${cols}">${escapeHtml(text)}</td></tr>`;
	}

	private renderBody(rows: TRow[]): void {
		this.lastRows = rows;
		// Controls-only mode: keep the rows cached (for in-place reorder re-renders) but render nothing in the
		// table body — the consumer's `onData` paints the external results (cards / minimaps).
		if (this.opts.externalResults) return;
		const cols = this.visibleColumns();
		if (rows.length === 0) {
			this.renderMessage(this.opts.emptyMessage ?? 'No results.', false);
			return;
		}
		const getId = this.opts.getRowId;
		this.rowEls = new WeakMap();
		const frag = document.createDocumentFragment();
		for (const row of rows) {
			const tr = document.createElement('tr');
			// The row's id on the ROW, not just on its selection checkbox. Row-level interactions used to
			// need a button inside a cell to hang a data-id off; a whole-row click (race results → follow
			// that rider on the map) has nowhere to read it from otherwise.
			if (getId) setData(tr, 'rowId', getId(row));
			for (const col of cols) {
				const td = document.createElement('td');
				if (col.align === 'end') td.classList.add('text-end');
				if (col.cellClass) td.classList.add(col.cellClass);
				if (col.selection) {
					const id = getId ? getId(row) : NaN;
					const checked = this.selectionSet(col.selection.key).has(id) ? ' checked' : '';
					const title = col.selection.title ? ` title="${escapeHtml(col.selection.title)}"` : '';
					// eslint-disable-next-line no-unsanitized/property -- key/title escapeHtml'd; id numeric; checked is a literal ternary
					td.innerHTML =
						`<input type="checkbox" class="grid-sel-cb" data-sel-key="${escapeHtml(col.selection.key)}" data-row-id="${id}"${checked}${title}>`;
				} else {
					// eslint-disable-next-line no-unsanitized/property -- formatter output is safe-by-contract: formatters own escaping (all 4 grid consumers verified, S2 census)
					td.innerHTML = col.formatter ? col.formatter(row) : '';
				}
				tr.appendChild(td);
			}
			if (typeof row === 'object' && row !== null) this.rowEls.set(row, tr);
			frag.appendChild(tr);
		}
		this.tbody.innerHTML = '';
		this.tbody.appendChild(frag);
	}

	/**
	 * Moves the already-rendered `<tr>` nodes into `rows` order (row content is unchanged on a pure
	 * re-sort, so moving nodes beats rebuilding: no formatter re-runs, no HTML parsing, checkbox and
	 * button state preserved). False when any row has no live node — caller falls back to a full render.
	 */
	private reorderBody(rows: TRow[]): boolean {
		if (this.opts.externalResults) {
			this.lastRows = rows;
			return true; // no in-table body — onData reorders the external cards
		}
		if (rows.length === 0) return false;
		const els: HTMLTableRowElement[] = [];
		for (const row of rows) {
			const el = typeof row === 'object' && row !== null ? this.rowEls.get(row) : undefined;
			if (!el || el.parentElement !== this.tbody) return false;
			els.push(el);
		}
		this.lastRows = rows;
		for (const el of els) this.tbody.appendChild(el);
		return true;
	}

	// ── toolbar (reusable: count + reset + columns) ────────────────────────────────

	/** Builds the toolbar into the page-provided `toolbarMount` (no-op when not configured). */
	private buildToolbar(): void {
		const mount = this.opts.toolbarMount;
		const cfg = this.opts.toolbar;
		if (!mount || !cfg) return;

		mount.classList.add('grid-toolbar');
		mount.innerHTML = '';

		const actions = document.createElement('div');
		actions.className = 'grid-toolbar-actions';

		if (cfg.resetFilters) {
			const btn = document.createElement('button');
			btn.type = 'button';
			btn.className = 'grid-toolbar-btn grid-toolbar-btn--icon';
			// eslint-disable-next-line no-unsanitized/property -- FILTER_CLEAR_ICON is a module-const SVG literal (funnel with an ✕ = clear filters)
			btn.innerHTML = FILTER_CLEAR_ICON;
			btn.title = 'Clear all column filters and reset sorting';
			btn.setAttribute('aria-label', 'Clear filters');
			btn.addEventListener('click', () => this.resetFilters());
			actions.appendChild(btn);
		}

		if (cfg.resetAll) {
			const btn = document.createElement('button');
			btn.type = 'button';
			btn.className = 'grid-toolbar-btn grid-toolbar-btn--icon';
			btn.innerHTML = '<i class="bi bi-arrow-counterclockwise"></i>';
			btn.title = 'Reset everything to defaults (filters, sort, columns, widths)';
			btn.setAttribute('aria-label', 'Reset all');
			btn.addEventListener('click', () => this.resetAll());
			actions.appendChild(btn);
		}

		if (cfg.columns) {
			this.columnsSlot = document.createElement('span');
			this.columnsSlot.className = 'grid-toolbar-cols';
			actions.appendChild(this.columnsSlot);
			this.renderColumnsControl();
		}

		mount.appendChild(actions);

		// The count sits after the buttons, still on the left.
		if (cfg.count) {
			this.countEl = document.createElement('span');
			this.countEl.className = 'grid-toolbar-count';
			mount.appendChild(this.countEl);
		}
	}

	/** (Re)builds the Columns dropdown from the schema's optional, non-hidden columns, checked per visibleKeys. */
	private renderColumnsControl(): void {
		const slot = this.columnsSlot;
		if (!slot) return;

		const toggleable = this.opts.columns.flatMap((c) =>
			c.optionalKey && !c.pickerHidden ? [{ key: c.optionalKey, header: c.header }] : []);
		const toggleableKeys = new Set(toggleable.map((t) => t.key));
		const options = toggleable.map((t) => ({ value: t.key, label: t.header }));
		const selected = new Set(toggleable.filter((t) => this.visibleKeys.has(t.key)).map((t) => t.key));

		const onChange = (): void => {
			// Keep any non-toggleable visible keys (e.g. owner, shown by mode) and apply the picked optionals.
			const next = new Set<string>();
			for (const k of this.visibleKeys) if (!toggleableKeys.has(k)) next.add(k);
			for (const k of selected) next.add(k);
			this.setVisibleKeys(next);
			this.opts.onVisibleKeysChange?.(next);
		};

		const renderSummary = (summary: HTMLElement): void => {
			summary.innerHTML = '<i class="bi bi-table"></i> Columns';
		};

		const details = buildCheckboxPopup(options, selected, renderSummary, onChange);
		details.classList.add('grid-toolbar-cols-popup');
		closeOpenPopups(slot); // see renderHeader — same leak on replacing an open Columns popup
		slot.innerHTML = '';
		slot.appendChild(details);
	}

	/** Updates the toolbar count: filtered total of the unfiltered grand total. */
	private updateCount(res: GridDataResponse<TRow>): void {
		if (!this.countEl) return;
		const filtered = res.total;
		const total = res.unfilteredTotal ?? res.total;
		this.countEl.textContent = this.opts.countFormatter
			? this.opts.countFormatter(filtered, total)
			: filtered === total
				? total.toLocaleString()
				: `${filtered.toLocaleString()}/${total.toLocaleString()}`;
	}

	/** Clears all column filters and resets sort to the default. The state change drives one refresh. */
	resetFilters(): void {
		this.state.clearFilters();
		this.state.resetSort();
		this.renderHeader(); // rebuild the filter row from the now-empty state + reset sort icons
	}

	/** Resets everything to defaults: filters, sort, column widths, column order, and visible columns. */
	resetAll(): void {
		const defaults = new Set(this.opts.defaultVisibleKeys ?? []);
		this.state.silentReset(); // no notify — we drive a single re-render + refresh below
		this.sizer?.reset();
		this.columnOrder?.reset();
		this.visibleKeys = new Set(defaults);
		this.opts.onVisibleKeysChange?.(new Set(defaults));

		this.renderHeader();
		this.renderColumnsControl();
		void this.refresh(); // fires onQueryChange after the cleared query's data renders
	}
}
