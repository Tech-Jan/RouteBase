/**
 * Trash feed — sort/filter controls that drive the trash cards.
 *
 * Same recipe as the dashboard feed (dashboard/dashboard-grid.ts): the reusable client grid runs in
 * controls-only mode (externalResults) — its header + filter row are the strip; `onData` reorders/hides
 * the server-rendered `.trash-card` wrappers via the shared card binding. Rows arrive with the page
 * (the #trashBootstrap JSON island, read by trash.ts), so filtering and sorting are entirely client-side.
 *
 * The columns are the trash's own: what it was, when you threw it away, and why.
 */

import type { GridColumn, GridSort } from '../core/grid/grid-types';
import { GridView, escapeHtml } from '../core/grid/grid-view';
import { InMemoryGridDataSource } from '../core/grid/grid-data-source';
import { bindCardsToGrid } from '../core/grid/grid-card-binding';
import { formatDateDMY } from '../core/grid/grid-format';
import { reasonCategory } from './trash-model';
import type { TrashedActivity, TrashGridBootstrap } from '../generated/contracts';

// Newest thrown away first — the order the server renders the cards in.
const DEFAULT_SORT: GridSort = { column: 'trashed', direction: 'desc' };

/** The reason buckets present in this trash, for the Reason filter's combo. */
export function reasonOptions(rows: ReadonlyArray<TrashedActivity>): string[] {
	return Array.from(new Set(rows.map(reasonCategory))).sort();
}

export function buildColumns(boot: TrashGridBootstrap): GridColumn<TrashedActivity>[] {
	return [
		{
			key: 'trashed', header: 'Trashed', sortable: true, width: 100, cellClass: 'text-nowrap',
			filter: { type: 'daterange' },
			getValue: (r) => r.trashedAt,
			formatter: (r) => formatDateDMY(r.trashedAt),
		},
		{
			key: 'date', header: 'Date', sortable: true, width: 96, cellClass: 'text-nowrap',
			filter: { type: 'daterange' },
			getValue: (r) => r.startedIso,
			formatter: (r) => formatDateDMY(r.startedIso),
		},
		{
			key: 'name', header: 'Name', sortable: true, width: 200,
			filter: { type: 'text', placeholder: '*name*' },
			getValue: (r) => r.name,
			formatter: (r) => escapeHtml(r.name),
		},
		{
			key: 'type', header: 'Type', sortable: true, width: 130,
			filter: { type: 'combo', options: boot.typeOptions, placeholder: '*type*' },
			getValue: (r) => r.activityType,
			formatter: (r) => escapeHtml(r.activityType ?? ''),
		},
		{
			key: 'distance', header: 'Distance (km)', sortable: true, align: 'end', width: 130,
			dataType: 'number', filterable: true,
			getValue: (r) => (r.distanceMeters == null ? null : r.distanceMeters / 1000),
			formatter: (r) => (r.distanceMeters == null ? '' : (r.distanceMeters / 1000).toFixed(2)),
		},
		{
			key: 'ascent', header: 'Elev (m)', sortable: true, align: 'end', width: 110,
			dataType: 'number', filterable: true,
			getValue: (r) => r.ascentMeters,
			formatter: (r) => (r.ascentMeters == null ? '' : String(Math.round(r.ascentMeters))),
		},
		{
			// The merge product's name is deliberately NOT part of the filter value — one bucket per merged-into
			// ride would make the combo useless. The card's why-line still names it.
			key: 'reason', header: 'Reason', sortable: true, width: 170,
			filter: { type: 'combo', options: reasonOptions(boot.rows), placeholder: '*reason*' },
			getValue: (r) => reasonCategory(r),
			formatter: (r) => escapeHtml(reasonCategory(r)),
		},
	];
}

/** Mounts the filter strip and binds it to the cards. No-op when the page has no trash. */
export function initTrashGrid(boot: TrashGridBootstrap): void {
	const mount = document.getElementById('trashGrid');
	const cards = document.getElementById('trashCards');
	if (!mount || !cards) return;

	const columns = buildColumns(boot);

	new GridView<TrashedActivity>({
		mount,
		columns,
		dataSource: new InMemoryGridDataSource<TrashedActivity>(boot.rows, {
			getId: (r) => r.trackId,
			columns,
		}),
		defaultSort: DEFAULT_SORT,
		pageSize: boot.rows.length || 1, // in-memory source ignores paging; show all
		multiSort: true,
		externalResults: true,
		persistWidthsKey: 'trash-feed-v1',
		toolbarMount: document.getElementById('trashGridToolbar') ?? undefined,
		toolbar: { count: true, resetFilters: true, resetAll: true },
		tableClass: 'dash-controls-table',
		onData: bindCardsToGrid<TrashedActivity>({
			container: cards,
			getId: (r) => r.trackId,
			idAttribute: 'data-track-id',
			cardSelector: '.trash-card[data-track-id]',
			hiddenClass: 'is-hidden',
			emptyState: document.getElementById('trashFilteredEmpty'),
		}),
	});
}
