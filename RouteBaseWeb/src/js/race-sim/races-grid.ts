/**
 * /Races list — sort/filter controls that drive the course-minimap race cards.
 *
 * Same controls-only pattern as the Segments page (overview/segments-grid.ts): the generic client grid's
 * header + filter row are the controls strip (its data body hidden via `.race-controls-table`), and `onData`
 * reorders / hides the server-rendered `.race-card` elements to match the query. All rows arrive via
 * the #racesGridBootstrap JSON island (rendered on Races/Index.cshtml, read through readIsland), so filtering + sorting are client-side over
 * the full set — no pagination, no round-trips. Shift-click a header for a secondary sort level.
 */

import type { RacesGridRow, RacesGridBootstrap } from './races-grid-types';
import type { GridColumn, GridQuery, GridSort } from '../core/grid/grid-types';
import { GridView, escapeHtml } from '../core/grid/grid-view';
import { InMemoryGridDataSource } from '../core/grid/grid-data-source';
import { bindCardsToGrid } from '../core/grid/grid-card-binding';
import { clearGenericFilterParams, parseFilters, serializeFilters } from '../core/grid/grid-url';
import { formatDateDMY } from '../core/grid/grid-format';
import { VISIBILITY_FILTER_OPTIONS } from '../core/grid/visibility-options';
import { readIsland } from '../core/page-bootstrap';
import { SHARED_FILTER_OPTIONS, sharedCellText, sharedFilterValue } from '../core/grid/shared-options';

const DEFAULT_SORT: GridSort = { column: 'date', direction: 'desc' };

// Optional (Columns-picker–toggleable) columns, shown by default — mirrors Segments' optional metrics.
const OPTIONAL_KEYS = ['sections', 'distance', 'shared'] as const;
const DEFAULT_VISIBLE_KEYS = new Set<string>(OPTIONAL_KEYS);

// The table body is hidden (controls-only), so only the headers (sort) and the filter row are visible.
function buildColumns(): GridColumn<RacesGridRow>[] {
	return [
		{
			key: 'date', header: 'Date', sortable: true, width: 96, cellClass: 'text-nowrap',
			filter: { type: 'daterange' },
			// ISO date: lexicographic order = chronological; undated races (null) sort last.
			getValue: (r) => r.dateIso,
			formatter: (r) => formatDateDMY(r.dateIso),
		},
		{
			key: 'name', header: 'Name', sortable: true, width: 220,
			filter: { type: 'text', placeholder: '*name*' },
			getValue: (r) => r.name,
			formatter: (r) => escapeHtml(r.name ?? ''),
		},
		{
			key: 'visibility', header: 'Visibility', sortable: true, width: 120,
			filter: { type: 'multiselect', options: VISIBILITY_FILTER_OPTIONS },
			getValue: (r) => r.visibility,
			formatter: (r) => escapeHtml(r.visibility),
		},
		{
			// "Is this published, or was it?" — the filter twin of the card's share badge. Always false
			// for a friend's race, so this filter only ever narrows to the viewer's own published set.
			key: 'shared', header: 'Shared', sortable: true, width: 110, optionalKey: 'shared',
			filter: { type: 'multiselect', options: SHARED_FILTER_OPTIONS },
			getValue: (r) => sharedFilterValue(r.isShared, r.wasShared),
			formatter: (r) => sharedCellText(r.isShared, r.wasShared),
		},
		{
			key: 'field', header: 'Field', sortable: true, align: 'end', width: 110,
			dataType: 'number', filterable: true,
			getValue: (r) => r.field,
			formatter: (r) => String(r.field),
		},
		{
			key: 'sections', header: 'Sections', sortable: true, align: 'end', width: 110, optionalKey: 'sections',
			dataType: 'number', filterable: true,
			getValue: (r) => r.sections,
			formatter: (r) => String(r.sections),
		},
		{
			key: 'distance', header: 'Distance (km)', sortable: true, align: 'end', width: 130, optionalKey: 'distance',
			dataType: 'number', filterable: true,
			// Filter unit is km (the displayed unit), so the accessor scales from stored metres.
			getValue: (r) => (r.distanceMeters == null ? null : r.distanceMeters / 1000),
			formatter: (r) => (r.distanceMeters == null ? '' : (r.distanceMeters / 1000).toFixed(1)),
		},
	];
}

// ── URL sync (filters via the generic codec; multi-sort via a `Sort=col:dir,...` param) ────────────

function parseSorts(param: string | null): GridSort[] | undefined {
	if (!param) return undefined;
	const sorts = param
		.split(',')
		.map((tok) => tok.split(':'))
		.filter(([col]) => col)
		.map(([col, dir]) => ({ column: col, direction: dir === 'desc' ? 'desc' : 'asc' } as GridSort));
	return sorts.length ? sorts : undefined;
}

function parseInitialQuery(columns: GridColumn<RacesGridRow>[]): Partial<GridQuery> {
	const p = new URLSearchParams(location.search);
	return {
		filters: parseFilters(columns, p),
		sorts: parseSorts(p.get('Sort')) ?? [DEFAULT_SORT],
		page: 1,
	};
}

function buildPageUrl(columns: GridColumn<RacesGridRow>[], q: GridQuery): string {
	const p = new URLSearchParams(location.search);
	clearGenericFilterParams(columns, p);
	serializeFilters(columns, q.filters, p);
	p.set('Sort', q.sorts.map((s) => `${s.column}:${s.direction}`).join(','));
	return p.toString();
}

// ── wiring ──────────────────────────────────────────────────────────────────────

function init(): void {
	const boot = readIsland<RacesGridBootstrap | null>('racesGridBootstrap', null);
	const mount = document.getElementById('raceListGrid');
	const grid = document.querySelector<HTMLElement>('.race-card-grid');
	if (!boot || !mount || !grid) return;

	const emptyState = document.getElementById('raceEmptyFiltered');
	const columns = buildColumns();

	const dataSource = new InMemoryGridDataSource<RacesGridRow>(boot.rows, {
		getId: (r) => r.id,
		columns, // filtering + sorting derived from the schema (getValue + filter types)
	});

	// Controls-only grid: header (sort) + filter row are the strip; the results are the server-rendered
	// `.race-card` minimaps, reordered/hidden by the shared card binding.
	const onData = bindCardsToGrid<RacesGridRow>({
		container: grid,
		getId: (r) => r.id,
		idAttribute: 'data-race-id',
		cardSelector: '.race-card[data-race-id]',
		hiddenClass: 'is-hidden',
		emptyState,
	});

	new GridView<RacesGridRow>({
		mount,
		columns,
		dataSource,
		defaultSort: DEFAULT_SORT,
		pageSize: boot.rows.length || 1, // in-memory source ignores paging; show all
		initialQuery: parseInitialQuery(columns),
		multiSort: true,
		externalResults: true,
		visibleKeys: new Set(DEFAULT_VISIBLE_KEYS),
		defaultVisibleKeys: DEFAULT_VISIBLE_KEYS,
		persistWidthsKey: 'races-v1',
		toolbarMount: document.getElementById('raceListGridToolbar') ?? undefined,
		toolbar: { count: true, resetFilters: true, resetAll: true, columns: true },
		tableClass: 'race-controls-table',
		onData,
		onQueryChange: (q) => history.replaceState(history.state, '', `?${buildPageUrl(columns, q)}`),
	});
}

if (document.readyState === 'loading') {
	document.addEventListener('DOMContentLoaded', init);
} else {
	init();
}

export {};
