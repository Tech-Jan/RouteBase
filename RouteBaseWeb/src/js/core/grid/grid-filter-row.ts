/**
 * Builds the per-column filter row (`<tr>`) from the grid's visible columns and wires each control
 * to the shared {@link GridFilterState}. Text/number inputs apply debounced (Enter / search icon
 * apply immediately); multiselect uses a self-contained `<details>` popover.
 *
 * The caller passes the already-visible columns so the filter row's cell count matches the header.
 */

import type { FilterOption, GridColumn, GridColumnFilter } from './grid-types';
import { normalizeOption, resolveColumnFilter } from './grid-types';
import type { GridFilterState } from './grid-filter-state';

const DEBOUNCE_MS = 250;

function debounce<T extends (...args: never[]) => void>(fn: T, ms: number): T {
	let timer: ReturnType<typeof setTimeout> | undefined;
	return ((...args: never[]) => {
		if (timer) clearTimeout(timer);
		timer = setTimeout(() => fn(...args), ms);
	}) as T;
}

// Small intrinsic size so a text/number filter input can't dictate the column width under the grid's
// auto-measure pass (table-layout: auto). It still fills the column via `width: 100%` in fixed layout.
const FILTER_INPUT_SIZE = 2;

export function buildFilterRow<TRow>(columns: GridColumn<TRow>[], state: GridFilterState): HTMLTableRowElement {
	const tr = document.createElement('tr');
	tr.className = 'grid-filter-row';

	for (const col of columns) {
		const th = document.createElement('th');
		th.className = 'grid-filter-cell';
		if (col.align === 'end') th.classList.add('text-end');
		if (col.cellClass) th.classList.add(col.cellClass);

		const filter = resolveColumnFilter(col);
		if (filter) {
			switch (filter.type) {
				case 'text':
					buildTextFilter(th, col, filter, state);
					break;
				case 'numberrange':
					buildNumberExprFilter(th, col, filter, state);
					break;
				case 'daterange':
					buildDateRangeFilter(th, col, state);
					break;
				case 'multiselect':
					buildMultiselectFilter(th, col, filter, state);
					break;
				case 'combo':
					buildComboFilter(th, col, filter, state);
					break;
			}
		}
		tr.appendChild(th);
	}
	return tr;
}

function buildTextFilter<TRow>(
	th: HTMLElement, col: GridColumn<TRow>, filter: GridColumnFilter, state: GridFilterState,
): void {
	const input = document.createElement('input');
	input.type = 'text';
	input.className = 'grid-filter-input';
	input.size = FILTER_INPUT_SIZE;
	input.placeholder = filter.placeholder ?? '';
	input.value = state.getFilter(col.key)?.text ?? '';
	input.setAttribute('aria-label', `Filter ${col.header}`);

	const apply = () => state.setFilter(col.key, { text: input.value });
	input.addEventListener('input', debounce(apply, DEBOUNCE_MS));
	input.addEventListener('keydown', (e) => {
		if ((e as KeyboardEvent).key === 'Enter') apply();
	});

	th.appendChild(input);
}

// Numeric filter: a single expression box supporting ranges/multi-range/open-ended (e.g. `3-6, 8-12`, `>5`).
// The whole expression is the column's `expr` value; the server (or in-memory matcher) parses it.
function buildNumberExprFilter<TRow>(
	th: HTMLElement, col: GridColumn<TRow>, filter: GridColumnFilter, state: GridFilterState,
): void {
	const input = document.createElement('input');
	input.type = 'text';
	input.className = 'grid-filter-input';
	input.size = FILTER_INPUT_SIZE;
	input.placeholder = filter.placeholder ?? '3-6,>20';
	input.value = state.getFilter(col.key)?.expr ?? '';
	input.setAttribute('aria-label', `Filter ${col.header}`);

	const apply = () => state.setFilter(col.key, { expr: input.value });
	input.addEventListener('input', debounce(apply, DEBOUNCE_MS));
	input.addEventListener('keydown', (e) => {
		if ((e as KeyboardEvent).key === 'Enter') apply();
	});

	th.appendChild(input);
}

function buildDateRangeFilter<TRow>(th: HTMLElement, col: GridColumn<TRow>, state: GridFilterState): void {
	const wrap = document.createElement('div');
	wrap.className = 'grid-filter-range';

	const current = state.getFilter(col.key);
	const from = makeDateInput(current?.from ?? '', 'from', col);
	const to = makeDateInput(current?.to ?? '', 'to', col);

	const apply = () => state.setFilter(col.key, { from: from.value, to: to.value });
	for (const inp of [from, to]) {
		inp.addEventListener('change', apply);
		inp.addEventListener('keydown', (e) => {
			if ((e as KeyboardEvent).key === 'Enter') apply();
		});
	}

	wrap.append(from, to);
	th.appendChild(wrap);
}

function makeDateInput<TRow>(value: string, bound: string, col: GridColumn<TRow>): HTMLInputElement {
	const inp = document.createElement('input');
	inp.type = 'date';
	inp.className = 'grid-filter-input grid-filter-range-input';
	inp.value = value;
	inp.setAttribute('aria-label', `${col.header} ${bound}`);
	return inp;
}

/**
 * Closes every open checkbox popup under `root`. Call **before** wiping/replacing DOM that may contain
 * an open popup (e.g. a header re-render): removal alone never fires the `<details>` `toggle` event, so
 * the popup's document/window listeners would leak; closing while still attached runs the normal cleanup.
 */
export function closeOpenPopups(root: ParentNode): void {
	root.querySelectorAll<HTMLDetailsElement>('details.grid-filter-multi[open]').forEach((d) => (d.open = false));
}

/**
 * Builds a `<details>` checkbox popover for a set of options, shared by the `multiselect`/`combo` filters and
 * the toolbar's Columns picker. The caller owns the `selected` Set and is notified via `onChange` after every
 * toggle; it also renders the summary content (text count, an icon trigger, …). The menu is `position: fixed`
 * (see grid-filter.css) so it escapes the table's `overflow` clipping — it is anchored under the summary on
 * open and re-anchored on scroll/resize, and closes on an outside click.
 */
export function buildCheckboxPopup(
	options: FilterOption[],
	selected: Set<string>,
	renderSummary: (summary: HTMLElement) => void,
	onChange: () => void,
): HTMLDetailsElement {
	const details = document.createElement('details');
	details.className = 'grid-filter-multi';

	const summary = document.createElement('summary');
	summary.className = 'grid-filter-multi-summary';
	const refreshSummary = () => renderSummary(summary);
	refreshSummary();

	const menu = document.createElement('div');
	menu.className = 'grid-filter-multi-menu';
	for (const opt of options) {
		const { value, label: text } = normalizeOption(opt);
		const label = document.createElement('label');
		label.className = 'grid-filter-multi-option';
		const cb = document.createElement('input');
		cb.type = 'checkbox';
		cb.value = value;
		cb.checked = selected.has(value);
		cb.addEventListener('change', () => {
			if (cb.checked) selected.add(value);
			else selected.delete(value);
			refreshSummary();
			onChange();
		});
		label.append(cb, document.createTextNode(' ' + text));
		menu.appendChild(label);
	}

	details.append(summary, menu);

	const positionMenu = () => {
		const r = summary.getBoundingClientRect();
		menu.style.top = `${r.bottom + 2}px`;
		// Right-align to the summary when overflowing the right edge, but never past the left edge —
		// on narrow (mobile) viewports the menu clamps into view (width capped via CSS max-width).
		let left = r.left;
		if (left + menu.offsetWidth > window.innerWidth - 4) left = r.right - menu.offsetWidth;
		menu.style.left = `${Math.max(4, left)}px`;
	};
	const onOutsideClick = (e: MouseEvent) => {
		if (!details.contains(e.target as Node)) details.open = false;
	};
	details.addEventListener('toggle', () => {
		if (details.open) {
			positionMenu();
			window.addEventListener('scroll', positionMenu, true);
			window.addEventListener('resize', positionMenu);
			document.addEventListener('click', onOutsideClick);
		} else {
			window.removeEventListener('scroll', positionMenu, true);
			window.removeEventListener('resize', positionMenu);
			document.removeEventListener('click', onOutsideClick);
		}
	});

	return details;
}

function buildMultiselectFilter<TRow>(
	th: HTMLElement, col: GridColumn<TRow>, filter: GridColumnFilter, state: GridFilterState,
): void {
	const selected = new Set(state.getFilter(col.key)?.selected ?? []);
	const renderSummary = (summary: HTMLElement) =>
		(summary.textContent = selected.size ? `${col.header} (${selected.size})` : 'All');
	const onChange = () => state.setFilter(col.key, { selected: Array.from(selected) });

	th.appendChild(buildCheckboxPopup(filter.options ?? [], selected, renderSummary, onChange));
}

/**
 * Combo filter: a wildcard text box AND a checkbox popover over the same column. The typed text and the
 * checked options are OR-combined by the server (and the in-memory matcher), so e.g. typing `*ru*` and
 * checking Bike matches runs OR bikes. The value carries both `text` and `selected`.
 */
function buildComboFilter<TRow>(
	th: HTMLElement, col: GridColumn<TRow>, filter: GridColumnFilter, state: GridFilterState,
): void {
	const current = state.getFilter(col.key);
	const selected = new Set(current?.selected ?? []);

	const wrap = document.createElement('div');
	wrap.className = 'grid-filter-combo';

	const input = document.createElement('input');
	input.type = 'text';
	input.className = 'grid-filter-input';
	input.size = FILTER_INPUT_SIZE;
	input.placeholder = filter.placeholder ?? '';
	input.value = current?.text ?? '';
	input.setAttribute('aria-label', `Filter ${col.header}`);

	const apply = () => state.setFilter(col.key, { text: input.value, selected: Array.from(selected) });
	input.addEventListener('input', debounce(apply, DEBOUNCE_MS));
	input.addEventListener('keydown', (e) => {
		if ((e as KeyboardEvent).key === 'Enter') apply();
	});

	// The popover trigger is a chevron icon; it highlights while any option is checked.
	const renderSummary = (summary: HTMLElement) => {
		summary.classList.toggle('grid-filter-combo-trigger--active', selected.size > 0);
		summary.title = selected.size ? `${selected.size} selected` : 'Choose options';
	};
	const details = buildCheckboxPopup(filter.options ?? [], selected, renderSummary, apply);
	details.classList.add('grid-filter-combo-popup');
	details.querySelector('summary')?.classList.add('grid-filter-combo-trigger');

	wrap.append(input, details);
	th.appendChild(wrap);
}
