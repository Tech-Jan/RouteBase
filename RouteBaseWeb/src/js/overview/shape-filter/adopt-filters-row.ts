/**
 * shape-filter/adopt-filters-row.ts — the "Merge table ↔ shapes" control for section "1. Which tracks?".
 *
 * The single two-way merge toggle. It owns the non-spatial (column-filter) half of the merge — adopting the
 * activities table's rich column filters (date, name, type, distance, HR, …) as live segment rules — while
 * the orchestrator reads {@link isActive} to drive the spatial half (pushing the drawn shape to the grid so
 * the table restricts to shape ∩ columns). On: table, map highlight and any saved segment all reflect the
 * same set — WYSIWYG. One checkbox drives three modes:
 *   - off    → no merge: the segment carries no non-spatial filters and the table is not shape-restricted.
 *   - live   → every preview / save re-reads the grid's current filters (via the window bridge); grid edits
 *              refresh the preview automatically, and the drawn shape restricts the table.
 *   - stored → editing a saved segment: the filters embedded in the document are shown frozen ("saved"), so
 *              the user's current table state can't silently rewrite the segment. Unchecking drops them;
 *              re-checking switches to live and pulls the current table.
 *
 * Needs the grid bridge (overview-grid-bridge: getActivitiesGridFilters / isGridFilterApiRegistered);
 * the row hides itself when absent (pages without the activities grid). The activities-grid module
 * registers *after* this one (script order), so the bridge may be unregistered at construction — the
 * row subscribes to onGridFilterApiRegistered and reveals itself the moment the grid boots, with no
 * user interaction needed.
 */

import { formatGridFilters } from './grid-filters-format';
import type { GridFiltersMap } from './rule-document';
import {
	getActivitiesGridFilters,
	isGridFilterApiRegistered,
	onGridFilterApiRegistered,
} from '../overview-grid-bridge';

export type AdoptMode = 'off' | 'live' | 'stored';

export interface AdoptFiltersRowDeps {
	/** The effective adopted filters changed (toggle, or a live grid edit) — refresh preview + summary. */
	onChange: () => void;
}

export class AdoptFiltersRow {
	readonly root: HTMLDivElement;

	private readonly checkbox: HTMLInputElement;
	private readonly statusEl: HTMLSpanElement;
	private mode: AdoptMode = 'off';
	private storedFilters: GridFiltersMap | null = null;

	constructor(private readonly deps: AdoptFiltersRowDeps) {
		this.root = document.createElement('div');
		this.root.className = 'shape-row shape-row--fill shape-adopt-row';
		this.root.hidden = !this.hasBridge();

		const label = document.createElement('label');
		label.className = 'shape-adopt-label';
		this.checkbox = document.createElement('input');
		this.checkbox.type = 'checkbox';
		this.checkbox.className = 'shape-adopt-check';
		const text = document.createElement('span');
		text.textContent = 'Merge table ↔ shapes';
		label.title = 'Two-way merge: the drawn shapes filter the activities table, and the table’s column filters '
			+ '(date, name, type, distance, HR…) narrow the shapes — table, map and any saved segment show the same set';
		label.append(this.checkbox, text);

		this.statusEl = document.createElement('span');
		this.statusEl.className = 'shape-adopt-status';
		this.root.append(label, this.statusEl);

		this.checkbox.addEventListener('change', () => {
			// Any user toggle leaves stored mode: checked → live (pull the current table), unchecked → off.
			this.mode = this.checkbox.checked ? 'live' : 'off';
			this.storedFilters = null;
			this.renderStatus();
			this.deps.onChange();
		});

		// The grid module inits after this one — reveal as soon as it registers, not on the first filter edit.
		onGridFilterApiRegistered(() => { this.root.hidden = false; });

		window.addEventListener('activitiesGridFiltersChanged', () => {
			// In live mode a grid filter edit changes the effective rules — refresh preview + summary.
			if (this.mode === 'live') { this.renderStatus(); this.deps.onChange(); }
		});

		this.renderStatus();
	}

	private hasBridge(): boolean {
		return isGridFilterApiRegistered();
	}

	/** Filters to embed in the doc / preview: live → current grid, stored → frozen, off → null. */
	getEffective(): GridFiltersMap | null {
		if (this.mode === 'off') return null;
		if (this.mode === 'stored') return this.storedFilters;
		return getActivitiesGridFilters();
	}

	/**
	 * Should the drawn shape restrict the activities table? Only in `live` mode — the orchestrator gates the
	 * shape→table push on this. Deliberately NOT `stored`: while editing a saved segment the embedded column
	 * filters are frozen, but the table applies the user's *live* columns, so restricting it would show
	 * shape ∩ live-columns while the map/segment show shape ∩ stored-columns — a divergence. In edit mode the
	 * table stays the user's browsing view; unchecking→rechecking switches to live and engages the merge.
	 */
	restrictsTable(): boolean {
		return this.mode === 'live';
	}

	/** Hydrate from a loaded segment (edit mode). Non-null → checked + stored/frozen; null → off. */
	setStored(filters: GridFiltersMap | null): void {
		if (filters && Object.keys(filters).length > 0) {
			this.mode = 'stored';
			this.storedFilters = filters;
			this.checkbox.checked = true;
		} else {
			this.mode = 'off';
			this.storedFilters = null;
			this.checkbox.checked = false;
		}
		this.renderStatus();
	}

	/** Short badge next to the checkbox: "(saved)" when frozen, "(no table filters)" when live+empty. */
	private renderStatus(): void {
		if (this.mode === 'stored') {
			this.statusEl.textContent = '(saved)';
		} else if (this.mode === 'live') {
			const text = formatGridFilters(getActivitiesGridFilters());
			this.statusEl.textContent = text ? '' : '(no table filters set)';
		} else {
			this.statusEl.textContent = '';
		}
	}
}
