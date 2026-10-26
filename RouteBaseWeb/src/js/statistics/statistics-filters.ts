/**
 * Statistics filter header: owner / type / date controls that drive every widget on the page (summary,
 * chart, by-type boxes). Reuses the shared checkbox popup and the generic grid-url serialization, but
 * renders a standalone bar (no grid) whose single source of truth is a {@link GridFilterState}.
 *
 * URL sync mirrors the Activities grid: a fresh load with no owner param defaults to Me, and every change
 * is written back to the query string via `history.replaceState` so the page is shareable/bookmarkable.
 */

import { GridFilterState } from '../core/grid/grid-filter-state';
import { must } from '../core/invariant';
import { buildCheckboxPopup } from '../core/grid/grid-filter-row';
import { clearGenericFilterParams, parseFilters, serializeFilters } from '../core/grid/grid-url';
import { normalizeOption, type GridColumn, type GridFilterValues, type GridQuery } from '../core/grid/grid-types';
import { OWNER_KEYWORD_OPTIONS, OWNER_ME_TOKEN } from '../core/grid/owner-tokens';
import { VISIBILITY_FILTER_OPTIONS } from '../core/grid/visibility-options';
import { buildStatsColumns, defaultStatsFilters, DATE_KEY, OWNER_KEY, TYPE_KEY, VISIBILITY_KEY, type OwnerOption } from './statistics-filter-schema';

export interface StatsFilterBoot {
	multiUser: boolean;
	typeOptions: string[];
	ownerOptions: OwnerOption[];
}

// The bar carries no sort/page — GridFilterState requires a default sort + page size, so pass inert values.
const INERT_SORT = { column: DATE_KEY, direction: 'desc' as const };

/** Builds the filter bar into `mount` and returns the shared state the data layer + widgets subscribe to. */
export function createStatsFilterBar(mount: HTMLElement, boot: StatsFilterBoot): GridFilterState {
	const columns = buildStatsColumns(boot.typeOptions, boot.ownerOptions);
	const initial = parseInitialFilters(columns, boot.multiUser);
	const state = new GridFilterState(INERT_SORT, 0, { filters: initial }, defaultStatsFilters(boot.multiUser));

	render(mount, state, boot);

	// Mirror state → URL on every change (owner/type/date). Re-render only when filters change out-of-band
	// (Reset), which is handled inline in the Reset button — normal edits keep their control in sync already.
	state.subscribe((q) => syncUrl(columns, q));

	return state;
}

// ── URL <-> state ────────────────────────────────────────────────────────────

function parseInitialFilters(columns: GridColumn<unknown>[], multiUser: boolean): GridFilterValues {
	const p = new URLSearchParams(location.search);
	const filters = parseFilters(columns, p);
	// Fresh load (no owner param) defaults to Me in multi-user mode — identical to a Reset.
	if (multiUser && !filters[OWNER_KEY]) filters[OWNER_KEY] = { selected: [OWNER_ME_TOKEN] };
	return filters;
}

function syncUrl(columns: GridColumn<unknown>[], q: GridQuery): void {
	const p = new URLSearchParams(location.search);
	clearGenericFilterParams(columns, p);
	serializeFilters(columns, q.filters, p);
	const qs = p.toString();
	history.replaceState(history.state, '', qs ? `?${qs}` : location.pathname);
}

// ── Rendering ──────────────────────────────────────────────────────────────────

function render(mount: HTMLElement, state: GridFilterState, boot: StatsFilterBoot): void {
	// ST-08: close any open filter popup natively (fires buildCheckboxPopup's toggle-close cleanup)
	// before wiping the mount — otherwise its document/window listeners leak on a detached <details>.
	mount.querySelectorAll<HTMLElement>('details[open] > summary').forEach((s) => s.click());
	mount.textContent = '';
	mount.className = 'stats-filter-bar';

	if (boot.multiUser) {
		mount.appendChild(field('Owner', ownerControl(state, boot)));
	}
	mount.appendChild(field('Type', typeControl(state, boot)));
	mount.appendChild(field('Visibility', visibilityControl(state)));
	mount.appendChild(field('Date', dateControl(state)));

	const reset = document.createElement('button');
	reset.type = 'button';
	reset.className = 'stats-filter-reset';
	reset.textContent = 'Reset';
	reset.addEventListener('click', () => {
		state.clearFilters();
		render(mount, state, boot); // restore controls to the (Me) default
	});
	mount.appendChild(reset);
}

/** Wraps a control with its label into one filter field. */
function field(label: string, control: HTMLElement): HTMLElement {
	const wrap = document.createElement('div');
	wrap.className = 'stats-filter-field';
	const lbl = document.createElement('span');
	lbl.className = 'stats-filter-label mono';
	lbl.textContent = label;
	wrap.append(lbl, control);
	return wrap;
}

function ownerControl(state: GridFilterState, boot: StatsFilterBoot): HTMLElement {
	const options = [...OWNER_KEYWORD_OPTIONS, ...boot.ownerOptions];
	const labelOf = new Map(options.map((o) => [o.value, o.label]));
	const selected = new Set(state.getFilter(OWNER_KEY)?.selected ?? []);

	const renderSummary = (summary: HTMLElement) => {
		summary.textContent = summarize(selected, labelOf, 'Owner');
	};
	const onChange = () => state.setFilter(OWNER_KEY, { selected: Array.from(selected) });
	return buildCheckboxPopup(options, selected, renderSummary, onChange);
}

function typeControl(state: GridFilterState, boot: StatsFilterBoot): HTMLElement {
	const selected = new Set(state.getFilter(TYPE_KEY)?.selected ?? []);
	const renderSummary = (summary: HTMLElement) => {
		summary.textContent = selected.size ? `Type (${selected.size})` : 'All';
	};
	const onChange = () => state.setFilter(TYPE_KEY, { selected: Array.from(selected) });
	return buildCheckboxPopup(boot.typeOptions, selected, renderSummary, onChange);
}

function visibilityControl(state: GridFilterState): HTMLElement {
	const labelOf = new Map(VISIBILITY_FILTER_OPTIONS.map((o) => [normalizeOption(o).value, normalizeOption(o).label]));
	const selected = new Set(state.getFilter(VISIBILITY_KEY)?.selected ?? []);
	const renderSummary = (summary: HTMLElement) => {
		summary.textContent = summarize(selected, labelOf, 'Visibility');
	};
	const onChange = () => state.setFilter(VISIBILITY_KEY, { selected: Array.from(selected) });
	return buildCheckboxPopup(VISIBILITY_FILTER_OPTIONS, selected, renderSummary, onChange);
}

function dateControl(state: GridFilterState): HTMLElement {
	const wrap = document.createElement('div');
	wrap.className = 'stats-filter-daterange';

	const current = state.getFilter(DATE_KEY);
	const from = dateInput(current?.from ?? '', 'From');
	const to = dateInput(current?.to ?? '', 'To');
	const apply = () => state.setFilter(DATE_KEY, { from: from.value, to: to.value });
	for (const inp of [from, to]) {
		inp.addEventListener('change', apply);
		inp.addEventListener('keydown', (e) => {
			if ((e as KeyboardEvent).key === 'Enter') apply();
		});
	}

	wrap.append(from, to);
	return wrap;
}

function dateInput(value: string, aria: string): HTMLInputElement {
	const inp = document.createElement('input');
	inp.type = 'date';
	inp.className = 'grid-filter-input stats-filter-date';
	inp.value = value;
	inp.setAttribute('aria-label', `Date ${aria}`);
	return inp;
}

// Popup summary text: nothing selected → the field name; one selection → its label; more → a count.
function summarize(selected: Set<string>, labelOf: Map<string, string>, empty: string): string {
	if (selected.size === 0) return empty;
	if (selected.size === 1) {
		const only = must(selected.values().next().value, 'size === 1 (guarded)');
		return labelOf.get(only) ?? only;
	}
	return `${empty} (${selected.size})`;
}
