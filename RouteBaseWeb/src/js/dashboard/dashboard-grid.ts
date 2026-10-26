/**
 * Dashboard feed — sort/filter controls that drive the activity cards.
 *
 * Same pattern as the Segments page: the reusable client grid runs in controls-only mode
 * (externalResults) — its header + filter row are the strip; `onData` reorders/hides the
 * server-rendered `.activity-card` elements via the shared card binding. All activity rows
 * arrive via the #dashboardGridBootstrap JSON island (rendered on Pages/Index.cshtml, read through
 * readIsland), so sorting +
 * filtering happen entirely client-side over the full set — no page reloads, no pagination.
 *
 * The same filtered rows are handed to `onRows` so the KPI / volume / calendar widgets describe
 * exactly the cards on screen. Wired by dashboard.ts, which owns the page.
 */

import type { DashboardGridRow, DashboardGridBootstrap } from './dashboard-grid-types';
import type { GridColumn, GridQuery, GridSort } from '../core/grid/grid-types';
import { GridView, escapeHtml } from '../core/grid/grid-view';
import { InMemoryGridDataSource } from '../core/grid/grid-data-source';
import { bindCardsToGrid } from '../core/grid/grid-card-binding';
import { clearGenericFilterParams, parseFilters, serializeFilters } from '../core/grid/grid-url';
import { formatDateDMY } from '../core/grid/grid-format';
import { VISIBILITY_FILTER_OPTIONS } from '../core/grid/visibility-options';
import { SHARED_FILTER_OPTIONS, sharedCellText, sharedFilterValue } from '../core/grid/shared-options';
import { readIsland } from '../core/page-bootstrap';

type FeedRow = DashboardGridRow;

const DEFAULT_SORT: GridSort = { column: 'date', direction: 'desc' };

// ── column schema ────────────────────────────────────────────────────────────────
// Formatters are minimal: the table body is hidden (controls-only) — only headers (sort)
// and the filter row show. `getValue` drives the schema-derived client filter/sort, in the
// unit the user types into the filter (km, minutes, km/h, …).

function buildColumns(b: DashboardGridBootstrap): GridColumn<FeedRow>[] {
	const cols: GridColumn<FeedRow>[] = [
		{
			key: 'date', header: 'Date', sortable: true, width: 96, cellClass: 'text-nowrap',
			filter: { type: 'daterange' },
			getValue: (r) => r.dateIso,
			formatter: (r) => formatDateDMY(r.dateIso),
		},
		{
			key: 'name', header: 'Name', sortable: true, width: 200,
			filter: { type: 'text', placeholder: '*name*' },
			getValue: (r) => r.name,
			formatter: (r) => escapeHtml(r.name),
		},
		{
			key: 'type', header: 'Type', sortable: true, width: 130,
			filter: { type: 'combo', options: b.typeOptions, placeholder: '*type*' },
			getValue: (r) => r.activityType,
			formatter: (r) => escapeHtml(r.activityType ?? ''),
		},
		{
			key: 'visibility', header: 'Visibility', sortable: true, width: 120,
			filter: { type: 'multiselect', options: VISIBILITY_FILTER_OPTIONS },
			getValue: (r) => r.visibility,
			formatter: (r) => escapeHtml(r.visibility),
		},
		{
			// "Is this published, or was it?" — the filter twin of the card's share badge.
			key: 'shared', header: 'Shared', sortable: true, width: 110,
			filter: { type: 'multiselect', options: SHARED_FILTER_OPTIONS },
			getValue: (r) => sharedFilterValue(r.isShared, r.wasShared),
			formatter: (r) => sharedCellText(r.isShared, r.wasShared),
		},
		{
			key: 'distance', header: 'Distance (km)', sortable: true, align: 'end', width: 130,
			dataType: 'number', filterable: true,
			getValue: (r) => (r.distanceMeters == null ? null : r.distanceMeters / 1000),
			formatter: (r) => (r.distanceMeters == null ? '' : (r.distanceMeters / 1000).toFixed(2)),
		},
		{
			// Filter unit: minutes (matches the Activities table's Time filter).
			key: 'time', header: 'Time (min)', sortable: true, align: 'end', width: 110,
			dataType: 'number', filterable: true,
			getValue: (r) => (r.timeTotalSeconds == null ? null : r.timeTotalSeconds / 60),
			formatter: (r) => (r.timeTotalSeconds == null ? '' : String(Math.round(r.timeTotalSeconds / 60))),
		},
		{
			key: 'ascent', header: 'Elev (m)', sortable: true, align: 'end', width: 110,
			dataType: 'number', filterable: true,
			getValue: (r) => r.ascentMeters,
			formatter: (r) => (r.ascentMeters == null ? '' : String(Math.round(r.ascentMeters))),
		},
		{
			key: 'avgspeed', header: 'Avg Speed (km/h)', sortable: true, align: 'end', width: 140,
			dataType: 'number', filterable: true,
			getValue: (r) => (r.speedAvgMs == null ? null : r.speedAvgMs * 3.6),
			formatter: (r) => (r.speedAvgMs == null ? '' : (r.speedAvgMs * 3.6).toFixed(1)),
		},
		{
			key: 'avghr', header: 'Avg HR', sortable: true, align: 'end', width: 100,
			dataType: 'number', filterable: true,
			getValue: (r) => r.heartRateAvg,
			formatter: (r) => (r.heartRateAvg == null ? '' : String(Math.round(r.heartRateAvg))),
		},
	];

	if (b.multiUser) {
		cols.push({
			key: 'owner', header: 'Owner', sortable: true, width: 150,
			filter: { type: 'combo', options: b.ownerOptions, placeholder: '*name*' },
			getValue: (r) => r.owner,
			formatter: (r) => escapeHtml(r.owner),
		});
	}
	return cols;
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

function parseInitialQuery(columns: GridColumn<FeedRow>[]): Partial<GridQuery> {
	const p = new URLSearchParams(location.search);
	return {
		filters: parseFilters(columns, p),
		sorts: parseSorts(p.get('Sort')) ?? [DEFAULT_SORT],
		page: 1,
	};
}

function buildPageUrl(columns: GridColumn<FeedRow>[], q: GridQuery): string {
	const p = new URLSearchParams(location.search);
	// Legacy full-reload filter params owned by the old feed UI — clear so they don't linger.
	['page', 'pageSize', 'OwnerUserId', 'ActivityTypeFilter', 'DateRange'].forEach((k) => p.delete(k));
	clearGenericFilterParams(columns, p);
	serializeFilters(columns, q.filters, p);
	p.set('Sort', q.sorts.map((s) => `${s.column}:${s.direction}`).join(','));
	return p.toString();
}

// ── wiring ──────────────────────────────────────────────────────────────────────

/** Builds the feed's controls grid. `onRows` receives the filtered+sorted rows on every query. */
export function initDashboardGrid(onRows: (rows: FeedRow[]) => void): void {
	const boot = readIsland<DashboardGridBootstrap | null>('dashboardGridBootstrap', null);
	const mount = document.getElementById('dashboardGrid');
	const feed = document.querySelector<HTMLElement>('.feed-list');
	if (!boot || !mount || !feed) return;

	const emptyState = document.getElementById('dashFeedEmpty');
	const columns = buildColumns(boot);
	const initialQuery = parseInitialQuery(columns);

	const dataSource = new InMemoryGridDataSource<FeedRow>(boot.rows, {
		getId: (r) => r.id,
		columns, // filtering + sorting derived from the schema (getValue + filter types)
	});

	// Controls-only grid: the result rows are the server-rendered `.activity-card`s,
	// reordered/hidden by the shared card binding (CSS order — minimaps stay intact).
	const bindCards = bindCardsToGrid<FeedRow>({
		container: feed,
		getId: (r) => r.id,
		idAttribute: 'data-track-id',
		cardSelector: '.activity-card[data-track-id]',
		hiddenClass: 'is-hidden',
		emptyState,
	});

	new GridView<FeedRow>({
		mount,
		columns,
		dataSource,
		defaultSort: DEFAULT_SORT,
		pageSize: boot.rows.length || 1, // in-memory source ignores paging; show all
		initialQuery,
		multiSort: true,
		externalResults: true,
		persistWidthsKey: 'dashboard-feed-v1',
		toolbarMount: document.getElementById('dashboardGridToolbar') ?? undefined,
		toolbar: { count: true, resetFilters: true, resetAll: true },
		tableClass: 'dash-controls-table',
		onData: (res, q) => {
			bindCards(res, q);
			onRows(res.rows); // in-memory source → the full filtered set, unpaged
		},
		onQueryChange: (q) => {
			// Preserve the entry's state. GridView's constructor refreshes, so this fires one microtask
			// after initDashboardGrid() returns — passing null here wiped the feed's saved scroll anchor
			// (core/scroll-restore.ts) before it could be read. Idiom from edit-mode-chrome.ts.
			history.replaceState(history.state, '', `?${buildPageUrl(columns, q)}`);
		},
	});
}
