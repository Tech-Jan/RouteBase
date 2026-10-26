/**
 * Segments listing — sort/filter controls that drive the minimap preview cards.
 *
 * Reuses the generic client grid-view, but the cards (not table rows) are the results: the grid's
 * header + filter row act as a controls strip (its data body is hidden via the `.seg-controls-table`
 * class), and `onData` reorders / hides the server-rendered `.seg-card` elements to match the query.
 *
 * All segment rows arrive via the #segmentsGridBootstrap JSON island (rendered on Segments/Index.cshtml,
 * read through readIsland), so
 * sorting + filtering happen entirely client-side over the full set (no pagination, no round-trips).
 * Multi-column sort is enabled (shift-click a header to add a secondary level).
 */

import { readData, setData } from '../core/data-attrs';
import type { SegmentsGridRow, SegmentsGridBootstrap } from './overview-types';
import type { GridColumn, GridQuery, GridSort } from '../core/grid/grid-types';
import { GridView, escapeHtml } from '../core/grid/grid-view';
import { InMemoryGridDataSource } from '../core/grid/grid-data-source';
import { bindCardsToGrid } from '../core/grid/grid-card-binding';
import { clearGenericFilterParams, parseFilters, serializeFilters } from '../core/grid/grid-url';
import { formatDateDMY } from '../core/grid/grid-format';
import { SHARED_FILTER_OPTIONS, sharedCellText, sharedFilterValue } from '../core/grid/shared-options';
import { readIsland } from '../core/page-bootstrap';

// Row + bootstrap shapes are imported from overview-types (SegmentsGridRow / SegmentsGridBootstrap); the
// payload rides the #segmentsGridBootstrap JSON island, no longer a window global.
type SegmentRow = SegmentsGridRow;

const DEFAULT_SORT: GridSort = { column: 'created', direction: 'desc' };

// ── column schema ────────────────────────────────────────────────────────────────
// Formatters are intentionally minimal: the table body is hidden, so only the headers (sort) and the
// filter row are visible. Each column's `key` is what the filter/sort accessors below switch on.

// Optional (Columns-picker–toggleable) columns. Shown by default (see DEFAULT_VISIBLE_KEYS) — the picker
// lets the user hide/re-add them, mirroring the Activities grid's optional metric columns.
const OPTIONAL_KEYS = ['ascent', 'descent', 'efforts', 'visibility', 'shared'] as const;
const DEFAULT_VISIBLE_KEYS = new Set<string>(OPTIONAL_KEYS);

function buildColumns(b: SegmentsGridBootstrap): GridColumn<SegmentRow>[] {
	const cols: GridColumn<SegmentRow>[] = [
		// Creation date with a date-range filter (two datepickers), mirroring the Activities Date column.
		// Legacy segments have no timestamp (createdIso null) → blank cell, sorted last. Narrow default
		// width: the two date inputs collapse to their calendar-icon boxes; drag wider to reveal the text.
		{
			key: 'created', header: 'Created', sortable: true, width: 96, cellClass: 'text-nowrap',
			filter: { type: 'daterange' },
			// ISO timestamp: lexicographic order = chronological; legacy segments (null) sort last.
			getValue: (r) => r.createdIso,
			formatter: (r) => formatDateDMY(r.createdIso),
		},
		{
			key: 'name', header: 'Name', sortable: true, width: 200,
			filter: { type: 'text', placeholder: '*name*' },
			getValue: (r) => r.name,
			formatter: (r) => escapeHtml(r.name),
		},
		{
			key: 'kind', header: 'Kind', sortable: true, width: 110,
			filter: { type: 'multiselect', options: [{ value: 'polyline', label: 'Drawn' }, { value: 'shapes', label: 'Shapes' }] },
			getValue: (r) => r.kind,
			formatter: (r) => (r.kind === 'shapes' ? 'Shapes' : 'Drawn'),
		},
		{
			key: 'distance', header: 'Distance (km)', sortable: true, align: 'end', width: 130,
			dataType: 'number', filterable: true,
			// Filter unit is km (the displayed unit), so the accessor scales from stored meters.
			getValue: (r) => (r.distanceMeters == null ? null : r.distanceMeters / 1000),
			formatter: (r) => (r.distanceMeters == null ? '' : (r.distanceMeters / 1000).toFixed(2)),
		},
		{
			key: 'ascent', header: 'Ascent (m)', sortable: true, align: 'end', width: 120, optionalKey: 'ascent',
			dataType: 'number', filterable: true,
			getValue: (r) => r.ascentMeters,
			// Rounded for display: ascent is a real double since J2 (whole-metre truncation removed).
			formatter: (r) => (r.ascentMeters == null ? '' : String(Math.round(r.ascentMeters))),
		},
		{
			key: 'descent', header: 'Descent (m)', sortable: true, align: 'end', width: 120, optionalKey: 'descent',
			dataType: 'number', filterable: true,
			getValue: (r) => r.descentMeters,
			formatter: (r) => (r.descentMeters == null ? '' : r.descentMeters.toFixed(0)),
		},
		{
			key: 'efforts', header: 'Efforts', sortable: true, align: 'end', width: 100, optionalKey: 'efforts',
			dataType: 'number', filterable: true,
			getValue: (r) => r.effortCount,
			formatter: (r) => String(r.effortCount),
		},
	];

	// Visibility: shown for every viewer (it explains why a friend's segment is or is not listed), but
	// only the OWNER of a row gets the inline select — the server gates the PATCH on OwnedSegments anyway.
	cols.push({
		key: 'visibility', header: 'Visibility', sortable: true, width: 120, optionalKey: 'visibility',
		filter: { type: 'combo', options: b.visibilityOptions, placeholder: '*visibility*' },
		getValue: (r) => r.visibility,
		formatter: (r) => (r.isOwn ? visibilitySelect(r) : escapeHtml(r.visibility ?? '')),
	});

	// "Is this published, or was it?" — the filter twin of the card's share badge. Always false for a
	// friend's segment, so this filter only ever narrows to the viewer's own published set.
	cols.push({
		key: 'shared', header: 'Shared', sortable: true, width: 110, optionalKey: 'shared',
		filter: { type: 'multiselect', options: SHARED_FILTER_OPTIONS },
		getValue: (r) => sharedFilterValue(r.isShared, r.wasShared),
		formatter: (r) => sharedCellText(r.isShared, r.wasShared),
	});

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

/** Inline owner-only visibility picker. Change handling is delegated from the grid mount below. */
function visibilitySelect(r: SegmentRow): string {
	const current = r.visibility ?? 'friends';
	const opts = ['private', 'friends', 'public']
		.map((v) => `<option value="${v}"${v === current ? ' selected' : ''}>${v}</option>`)
		.join('');
	// data-previous seeds the revert value: at change-event time sel.value is ALREADY the new value, so
	// without it the first failed PATCH had nothing to revert to and the UI kept the refused choice.
	return `<select class="seg-visibility-select det-select" data-segment-visibility="${r.id}" data-previous="${escapeHtml(current)}">${opts}</select>`;
}

/** One delegated listener for the whole grid — rows are re-rendered on sort/filter, so per-row
 *  listeners would leak and go stale. */
function wireVisibilityDelegation(mount: HTMLElement, findRow: (id: number) => SegmentRow | undefined): void {
	mount.addEventListener('change', (e) => {
		const sel = (e.target as HTMLElement).closest<HTMLSelectElement>('[data-segment-visibility]');
		if (!sel) return;
		const id = Number(readData(sel, 'segmentVisibility'));
		const previous = readData(sel, 'previous') ?? sel.value;
		sel.disabled = true;
		void fetch(`/api/SegmentsApi/${id}/visibility`, {
			method: 'PATCH',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({ visibility: sel.value }),
		})
			.then((resp) => {
				if (resp.ok || resp.status === 204) {
					setData(sel, 'previous', sel.value);
					// The row model backs every re-render plus the Visibility filter/sort — without this,
					// the next sort/filter redrew the OLD value and the column filtered on stale data.
					// Re-narrow the raw <select> string against the wire union (D3 stamp) — the select
					// only offers these values, so the find can only miss if the markup drifts.
					const row = findRow(id);
					const v = (['private', 'friends', 'public'] as const).find((x) => x === sel.value);
					if (row && v) row.visibility = v;
					return;
				}
				sel.value = previous;   // server refused — do not leave the UI claiming a change that did not happen
				alert("Couldn't change the segment's visibility. Please try again."); // FUX-06: the revert was silent
			})
			.catch(() => {
				sel.value = previous;
				alert("Couldn't change the segment's visibility. Please try again.");
			})
			.finally(() => {
				sel.disabled = false;
			});
	});
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

function parseInitialQuery(columns: GridColumn<SegmentRow>[]): Partial<GridQuery> {
	const p = new URLSearchParams(location.search);
	return {
		filters: parseFilters(columns, p),
		sorts: parseSorts(p.get('Sort')) ?? [DEFAULT_SORT],
		page: 1,
	};
}

function buildPageUrl(columns: GridColumn<SegmentRow>[], q: GridQuery): string {
	const p = new URLSearchParams(location.search);
	clearGenericFilterParams(columns, p);
	serializeFilters(columns, q.filters, p);
	p.set('Sort', q.sorts.map((s) => `${s.column}:${s.direction}`).join(','));
	return p.toString();
}

// ── wiring ──────────────────────────────────────────────────────────────────────

function init(): void {
	const boot = readIsland<SegmentsGridBootstrap | null>('segmentsGridBootstrap', null);
	const mount = document.getElementById('segmentsGrid');
	const grid = document.querySelector<HTMLElement>('.seg-card-grid');
	if (!boot || !mount || !grid) return;

	const emptyState = document.getElementById('segEmptyState');
	const columns = buildColumns(boot);

	const dataSource = new InMemoryGridDataSource<SegmentRow>(boot.rows, {
		getId: (r) => r.id,
		columns, // filtering + sorting derived from the schema (getValue + filter types)
	});

	// Controls-only grid: header (sort) + filter row act as the controls strip; the result rows are the
	// server-rendered `.seg-card` minimaps, reordered/hidden by the shared card binding.
	const onData = bindCardsToGrid<SegmentRow>({
		container: grid,
		getId: (r) => r.id,
		// Scope to the card anchors — the inner `.segment-minimap` also carries `data-segment-id`.
		cardSelector: '.seg-card[data-segment-id]',
		hiddenClass: 'is-hidden',
		emptyState,
	});

	new GridView<SegmentRow>({
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
		persistWidthsKey: 'segments-v1',
		toolbarMount: document.getElementById('segmentsGridToolbar') ?? undefined,
		toolbar: { count: true, resetFilters: true, resetAll: true, columns: true },
		tableClass: 'seg-controls-table',
		onData,
		// Preserve the entry's state — this fires a microtask after the grid is built and would otherwise
		// wipe the card list's saved scroll anchor (core/scroll-restore.ts). Idiom from edit-mode-chrome.ts.
		onQueryChange: (q) => history.replaceState(history.state, '', `?${buildPageUrl(columns, q)}`),
	});

	wireVisibilityDelegation(mount, (id) => boot.rows.find((r) => r.id === id));
}

if (document.readyState === 'loading') {
	document.addEventListener('DOMContentLoaded', init);
} else {
	init();
}

export {};
