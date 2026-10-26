/**
 * Pluggable grid data sources — the seam that lets one grid-view serve both server-paged grids
 * (HTTP) and fully-loaded client grids (in-memory) without changing the view.
 */

import type { GridColumn, GridDataResponse, GridDataSource, GridQuery } from './grid-types';
import { filterRowsBySchema, sortRowsBySchema } from './grid-client-query';

/**
 * Server-paged source over POST — for grids whose filter state outgrows a query string (the
 * activities grid sends its map-drawn shape restriction as rule-document JSON; at ~30 zones a GET
 * URL exceeds the 8 KB request line). The grid-specific body DTO is built by `buildBody` (supplied
 * by the page integration). Each fetch aborts the previous in-flight request — a click burst issues
 * at most one live query (grid-view additionally drops stale responses via its request token).
 */
export class HttpPostGridDataSource<TRow> implements GridDataSource<TRow> {
	private ctrl?: AbortController;

	constructor(
		private readonly url: string,
		private readonly buildBody: (query: GridQuery) => unknown,
	) {}

	async fetch(query: GridQuery): Promise<GridDataResponse<TRow>> {
		this.ctrl?.abort();
		this.ctrl = new AbortController();
		const res = await fetch(this.url, {
			method: 'POST',
			headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
			body: JSON.stringify(this.buildBody(query)),
			signal: this.ctrl.signal,
		});
		if (!res.ok) throw new Error(`HTTP ${res.status}`);
		const body: GridDataResponse<TRow> = await res.json();
		return body;
	}
}

/**
 * In-memory source for fully-loaded, unpaged grids (e.g. segment efforts). Filtering and sorting are
 * derived from the column schema (`getValue` + filter type, or a column's `clientFilter`) — see
 * grid-client-query.ts — so integrations only declare columns, no per-page predicate switches.
 */
export class InMemoryGridDataSource<TRow> implements GridDataSource<TRow> {
	constructor(
		private readonly rows: TRow[],
		private readonly opts: {
			getId: (row: TRow) => number;
			columns: GridColumn<TRow>[];
		},
	) {}

	fetch(query: GridQuery): Promise<GridDataResponse<TRow>> {
		const { columns, getId } = this.opts;
		const filtered = filterRowsBySchema(columns, this.rows, query.filters);
		const sorted = sortRowsBySchema(columns, filtered, query.sorts.length ? query.sorts : [query.sort]);
		return Promise.resolve({
			rows: sorted,
			filteredIds: filtered.map(getId),
			total: filtered.length,
			unfilteredTotal: this.rows.length,
		});
	}
}
