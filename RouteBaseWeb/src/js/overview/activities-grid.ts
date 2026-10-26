/**
 * Activities listing grid — integration of the reusable client grid-view.
 *
 * Defines the activity column schema (with formatters that reproduce the former Razor cells), maps
 * the generic grid query ⇄ the `/api/activities/grid` request and the shareable page URL, and wires
 * the filtered result set to the overview map + the "Showing X of Y" count.
 *
 * Server data arrives via the #activitiesGridBootstrap JSON island (first page + options), rendered on
 * Activities.cshtml and read through readIsland. The filter row, sorting and paging then update with no
 * page reload.
 */

import { readData } from '../core/data-attrs';
import type { GridColumn, GridDataResponse, GridFilterValues, GridQuery } from '../core/grid/grid-types';
import { GridView, escapeHtml } from '../core/grid/grid-view';
import { HttpPostGridDataSource } from '../core/grid/grid-data-source';
import type { FilterCodec, FilterCodecMap } from '../core/grid/grid-url';
import { clearGenericFilterParams, legacyBoundsToExpr, parseFilters, serializeFilters } from '../core/grid/grid-url';
import { attachTopScrollbar } from '../core/grid/grid-top-scrollbar';
import { attachScrollChain } from '../core/grid/grid-scroll-chain';
import { createScrollRestore } from '../core/scroll-restore';
import { N0, N1, N2, num, formatDateDMY, formatDuration } from '../core/grid/grid-format';
import { OWNER_ME_TOKEN } from '../core/grid/owner-tokens';
import { VISIBILITY_FILTER_OPTIONS } from '../core/grid/visibility-options';
import { createDeleteController, type DeleteController } from './activities-delete-controller';
import { createBulkController, type BulkController } from './activities-bulk-controller';
import { createExportController } from './activities-export-controller';
import { registerGridFilterApi } from './overview-grid-bridge';
import { registerPickPublisher, publishPickedIds } from './overview-pick-bridge';
import { setOverviewHighlight } from './activities-overview';
import { createMapDisplayController, setupMapDisplayModeToggle } from './activities-map-display';
import { collectGridFilters, toColumnFilterWire, type GridColumnFilterWire } from './activities-filter-wire';
import { readIsland } from '../core/page-bootstrap';
import type { ActivitiesGridBootstrap } from './overview-types';
import type { ActivityGridRequest, ActivityGridResponse, ActivityRowDto } from '../generated/contracts';

// The editor front-door mode (track-edit slice links `/Activities?editorPick=1`; the literal mirrors
// entry-picker-core's EDITOR_PICK_PARAM — overview must NOT import track-edit, so it's inlined + grep-able,
// same C1 precedent as `pickFor`). In this mode the grid shows a checkbox column to pick tracks for the editor.
const EDITOR_PICK_PARAM = 'editorPick';
const EDITOR_PICK_SELECTION_KEY = 'editorpick';

// Bulk data-management "Select" mode (?select=1): a leading checkbox column + a toolbar bulk bar
// (visibility / type / delete). Distinct from editor-pick and mutually exclusive with it. Toggled via
// the URL param + reload — the same precedent as EDITOR_PICK_PARAM, so the checkbox column stays leftmost
// under its own persisted order (a fresh persistWidthsKey; see init()).
const MANAGE_PARAM = 'select';
const MANAGE_SELECTION_KEY = 'manage';

// Module-level so the actions-cell formatter can reflect each row's pending-delete state on re-render
// (paging/sorting rebuilds rows); created in init() before the first render.
let deleteCtl: DeleteController | null = null;

// Module-level so the type/visibility formatter can overlay each row's STAGED edit (Select mode) on every
// re-render, exactly as the actions cell reads deleteCtl. Non-null only in manage mode; created in init().
let bulkCtl: BulkController | null = null;

// Generated from C# ActivityRowDto (Models/ActivityRowDto.cs); rows arrive via the grid response /
// ActivitiesGridBootstrap.initialData.
type ActivityRow = ActivityRowDto;

// ── value formatting (parity with the former Razor cells; shared helpers in core/grid/grid-format) ──

// `marker` carries the Select-mode staged-edit attribute (data-pending-edit / -remove); the row-level
// `tr:has([data-pending-*])` CSS rules pick it up wherever it sits, so per-chip markers still style the row.
function visChip(vis: string, marker = ''): string {
	const cls =
		vis === 'private' ? 'act-vis-chip--private' : vis === 'public' ? 'act-vis-chip--public' : 'act-vis-chip--friends';
	const icon = vis === 'private' ? 'bi-lock' : vis === 'public' ? 'bi-globe' : 'bi-people';
	const title = vis === 'private' ? 'Private' : vis === 'public' ? 'Public' : 'Friends';
	return `<span class="act-vis-chip ${cls}"${marker} title="${title}"><i class="bi ${icon}"></i></span>`;
}

// Select-mode staged-edit marker for a chip: greyed when the row is staged for delete, highlighted when the
// given field carries a pending edit, empty otherwise. bulkCtl is null outside manage mode (no marker).
function stagedMarker(deleted: boolean | undefined, fieldStaged: boolean): string {
	return deleted ? ' data-pending-remove' : fieldStaged ? ' data-pending-edit' : '';
}

// ── column schema ──────────────────────────────────────────────────────────────

/** Leading checkbox column for editor-pick mode (header master selects/clears all filtered rows). */
function pickSelectionColumn(): GridColumn<ActivityRow> {
	return {
		key: EDITOR_PICK_SELECTION_KEY, header: '', width: 40, cellClass: 'grid-sel-cell',
		selection: {
			key: EDITOR_PICK_SELECTION_KEY,
			headerIcon: '<i class="bi bi-scissors"></i>',
			title: 'Pick tracks for the editor (header: select/clear all filtered rows)',
		},
	};
}

/** Leading checkbox column for Select (bulk-management) mode (header master selects/clears all filtered rows). */
function manageSelectionColumn(): GridColumn<ActivityRow> {
	return {
		key: MANAGE_SELECTION_KEY, header: '', width: 40, cellClass: 'grid-sel-cell',
		selection: {
			key: MANAGE_SELECTION_KEY,
			headerIcon: '<i class="bi bi-check2-square"></i>',
			title: 'Select activities for a bulk action (header: select/clear all filtered rows)',
		},
	};
}

function buildColumns(
	typeOptions: string[],
	ownerOptions: { value: string; label: string }[],
	manageMode: boolean,
): GridColumn<ActivityRow>[] {
	return [
		{
			// Narrow default width: the two date inputs collapse to just their calendar-icon boxes; drag the
			// column border wider to reveal the full date text fields.
			key: 'date', header: 'Date', sortable: true, cellClass: 'text-nowrap', width: 96,
			filter: { type: 'daterange' },
			getValue: (r) => r.startLocal,
			formatter: (r) => formatDateDMY(r.startLocal),
		},
		{
			key: 'name', header: 'Name', sortable: true,
			filter: { type: 'text', placeholder: '*name*' },
			getValue: (r) => r.name,
			formatter: (r) => `<a class="act-name-link" href="/Activities/Details/${r.trackId}">${escapeHtml(r.name)}</a>`,
		},
		{
			// Combo filter: a wildcard text box AND a checkbox picker of the known types, OR-combined
			// (e.g. type `*ru*` + check Bike → runs OR bikes). Checks match exactly; text uses wildcards.
			key: 'type', header: 'Type', sortable: true,
			filter: { type: 'combo', options: typeOptions, placeholder: '*type*' },
			getValue: (r) => r.activityType,
			// Overlays any STAGED type edit (Select mode): shows the pending type and marks the chip dirty
			// (`data-pending-edit` → highlight) or pending-delete (`data-pending-remove` → grey). bulkCtl is
			// null outside manage mode, so normal rendering is unchanged.
			formatter: (r) => {
				const ov = bulkCtl?.getOverride(r.trackId);
				const type = ov?.activityType ?? r.activityType;
				const marker = stagedMarker(ov?.deleted, ov?.activityType !== undefined);
				return `<span class="act-type-chip"${marker}>${escapeHtml(type)}</span>`;
			},
		},
		{
			// Visibility (private/friends/public) as its own filterable column — the chip formerly rendered
			// beside the type. Multiselect filter → the server's `visibility` grid column (ActivityGridColumns).
			key: 'visibility', header: 'Visibility', sortable: true, align: 'center', cellClass: 'act-vis-cell',
			filter: { type: 'multiselect', options: VISIBILITY_FILTER_OPTIONS },
			getValue: (r) => r.visibility,
			formatter: (r) => {
				const ov = bulkCtl?.getOverride(r.trackId);
				const vis = ov?.visibility ?? r.visibility;
				return visChip(vis, stagedMarker(ov?.deleted, ov?.visibility !== undefined));
			},
		},
		{
			// Owner combo (always shown, defaults to "Me"): Me/Others/Everyone keyword checkboxes + free-text
			// name search, OR-combined. Resolved to a UserId set server-side (see OwnerFilterResolver). Shown
			// even with no friends so non-friends' visible public activities can be filtered out.
			key: 'owner', header: 'Owner', sortable: true, optionalKey: 'owner', pickerHidden: true, cellClass: 'act-owner-cell',
			filter: ownerOptions.length ? { type: 'combo', options: ownerOptions, placeholder: '*name*' } : undefined,
			getValue: (r) => r.owner,
			formatter: (r) => escapeHtml(r.owner),
		},
		{
			key: 'distance', header: 'Distance (km)', sortable: true, align: 'end',
			dataType: 'number', filterable: true,
			getValue: (r) => r.distanceMeters,
			formatter: (r) => num(r.distanceMeters, N2, 0.001),
		},
		{
			key: 'ascent', header: 'Ascent (m)', sortable: true, align: 'end',
			dataType: 'number', filterable: true,
			getValue: (r) => r.ascentMeters,
			formatter: (r) => num(r.ascentMeters, N0),
		},
		{
			key: 'time', header: 'Time', sortable: true, align: 'end', cellClass: 'text-nowrap',
			// Cell shows HH:MM:SS; the range filter is in minutes (backend scales ×60).
			dataType: 'number', filterable: true,
			getValue: (r) => r.timeTotalSeconds,
			formatter: (r) => formatDuration(r.timeTotalSeconds),
		},
		// Optional metric columns — `dataType: 'number'` + `filterable` gives each a numeric range filter
		// automatically (no per-column wiring). Speed is entered in km/h, durations in minutes; the backend
		// column registry applies the unit scale. `getValue` enables the client-side sort fast path.
		{ key: 'avgspeed', header: 'Avg Speed (km/h)', sortable: true, align: 'end', optionalKey: 'avgspeed', dataType: 'number', filterable: true, getValue: (r) => r.speedAvgMs, formatter: (r) => num(r.speedAvgMs, N1, 3.6) },
		{ key: 'maxspeed', header: 'Max Speed (km/h)', sortable: true, align: 'end', optionalKey: 'maxspeed', dataType: 'number', filterable: true, getValue: (r) => r.speedMaxMs, formatter: (r) => num(r.speedMaxMs, N1, 3.6) },
		{ key: 'descent', header: 'Descent (m)', sortable: true, align: 'end', optionalKey: 'descent', dataType: 'number', filterable: true, getValue: (r) => r.descentMeters, formatter: (r) => num(r.descentMeters, N0) },
		{ key: 'avghr', header: 'Avg HR', sortable: true, align: 'end', optionalKey: 'avghr', dataType: 'number', filterable: true, getValue: (r) => r.heartRateAvg, formatter: (r) => num(r.heartRateAvg, N0) },
		{ key: 'maxhr', header: 'Max HR', sortable: true, align: 'end', optionalKey: 'maxhr', dataType: 'number', filterable: true, getValue: (r) => r.heartRateMax, formatter: (r) => num(r.heartRateMax, N0) },
		{ key: 'avgcadence', header: 'Avg Cadence', sortable: true, align: 'end', optionalKey: 'avgcadence', dataType: 'number', filterable: true, getValue: (r) => r.cadenceAvg, formatter: (r) => num(r.cadenceAvg, N0) },
		{ key: 'avgpower', header: 'Avg Power (W)', sortable: true, align: 'end', optionalKey: 'avgpower', dataType: 'number', filterable: true, getValue: (r) => r.powerAvg, formatter: (r) => num(r.powerAvg, N0) },
		{ key: 'movingtime', header: 'Moving Time', sortable: true, align: 'end', optionalKey: 'movingtime', dataType: 'number', filterable: true, cellClass: 'text-nowrap', getValue: (r) => r.timeMovingSeconds, formatter: (r) => formatDuration(r.timeMovingSeconds) },
		// Photos count (opt-in). Sort/filter dispatch to the backend "photos" CountColumn (COUNT subquery);
		// getValue drives the client-side sort fast path. Zero renders blank to keep the column uncluttered.
		{ key: 'photos', header: 'Photos', sortable: true, align: 'end', optionalKey: 'photos', dataType: 'number', filterable: true, getValue: (r) => r.photoCount, formatter: (r) => (r.photoCount > 0 ? String(r.photoCount) : '') },
		// Checked out into an open edit session — the ride has unsaved edits waiting on its Details page.
		// Always visible (an unfinished edit is not an opt-in detail) and blank for every free ride. Not
		// sortable/filterable: that would need a backend grid column, and this is a flag the rows already carry.
		{
			key: 'inedit', header: '', cellClass: 'text-center',
			formatter: (r) => (r.isInEdit
				? '<span class="badge text-bg-warning" title="Being edited — open it to resume or discard"><i class="bi bi-pencil-fill"></i></span>'
				: ''),
		},
		{
			key: 'actions', header: 'Actions',
			cellClass: 'act-actions',
			formatter: (r) => {
				// In Select mode the bulk bar owns delete — the per-row trash button would be a second,
				// redundant delete path, so the actions cell stays empty.
				if (manageMode) return '';
				if (!r.canDelete) return '';
				// Mark-for-deletion toggle (committed on the toolbar Delete button); undo button while marked.
				// (Details link removed — the Name column links to the activity now.)
				const marked = deleteCtl?.isMarked(r.trackId) ?? false;
				return marked
					? `<button class="btn-cockpit btn-cockpit-ghost btn-cockpit-sm" data-grid-action="delete" data-id="${r.trackId}" data-pending-remove title="Undo delete"><i class="bi bi-arrow-counterclockwise"></i></button>`
					: `<button class="btn-cockpit btn-cockpit-danger btn-cockpit-sm" data-grid-action="delete" data-id="${r.trackId}" title="Mark for deletion (applied on Delete)"><i class="bi bi-trash"></i></button>`;
			},
		},
	];
}

// ── query ⇄ API request / page URL mapping ─────────────────────────────────────
//
// The API request is a POST body (buildApiBody → C# ActivityGridRequest): filters as a plain
// key→filter map, date as startDate/endDate. The PAGE URL keeps the generic `f.<key>.*` param format
// (see core/grid/grid-url) with the date column overridden to the legacy `StartDate`/`EndDate` params
// so the filter row and the map stay in sync. The type combo uses the generic `f.type.in`/`f.type.text`
// format; the legacy `SelectedActivityTypes`/`Types` params are still read as a fallback (see
// parseInitialQuery) so old bookmarks keep working, and cleared from the URL on write (OWNED_URL_KEYS).

// In multi-user mode the grid's "home" state shows only the current user's activities (OWNER_ME_TOKEN,
// shared with the server via core/grid/owner-tokens); Others/Everyone/name-search are explicit opt-ins.
// This same default is what the reset buttons restore to (see GridView.defaultFilters), so a fresh load
// and a reset land on the identical state.
function defaultOwnerFilters(multiUser: boolean): GridFilterValues {
	return multiUser ? { owner: { selected: [OWNER_ME_TOKEN] } } : {};
}

const dateCodec: FilterCodec = {
	write: (v, p) => {
		if (v.from) p.set('StartDate', v.from);
		if (v.to) p.set('EndDate', v.to);
	},
	read: (p) => {
		const from = p.get('StartDate');
		const to = p.get('EndDate');
		return from || to ? { from: from ?? '', to: to ?? '' } : undefined;
	},
};

const urlOverrides: FilterCodecMap = { date: dateCodec };

// Old per-column URL params (pre-generic format) — cleared on write so they don't linger, and read as a
// fallback so old bookmarks still hydrate the grid (then get rewritten to f.* on the next state change).
const LEGACY_URL_KEYS = ['fName', 'fDistMin', 'fDistMax', 'fAscMin', 'fAscMax'];
const OWNED_URL_KEYS = [...LEGACY_URL_KEYS, 'SelectedActivityTypes', 'StartDate', 'EndDate', 'SortColumn', 'SortDirection'];

// Map-drawn shape filter ("Merge table ↔ shapes"), as rule-document JSON — set by the shape-filter panel via
// the grid bridge (setSpatialFilter). Read here so a reload folds it into the API request; the server matches
// it and restricts rows + filteredIds to shape ∩ columns. null = no shape (the common case).
let spatialFilterJson: string | null = null;

/** POST body of `/api/activities/grid` — composed over the request-relaxed generated ActivityGridRequest
 *  (D26): the always-sent fields stay Required<> (client policy), the conditional ones keep the generated
 *  optionality. `filters` stays a hand map: C# GridFilterCollection serializes via a custom JsonConverter
 *  (plain column-key→ColumnFilter dict), which reflection can't see — its generated schema is `{}`.
 *  The date column travels as startDate/endDate, shared with the top filter panel and the map. */
type ActivityGridRequestBody =
	Required<Pick<ActivityGridRequest, 'sortColumn' | 'sortDirection' | 'page' | 'pageSize'>>
	& Pick<ActivityGridRequest, 'startDate' | 'endDate' | 'ownerUserId' | 'shape'>
	& { filters: Record<string, GridColumnFilterWire> };

// POST, not GET: the shape restriction is rule-document JSON that outgrows a query string (FE-06).
function buildApiBody(columns: GridColumn<ActivityRow>[], q: GridQuery): ActivityGridRequestBody {
	const filters: Record<string, GridColumnFilterWire> = {};
	for (const col of columns) {
		if (col.key === 'date') continue; // travels as startDate/endDate below
		const v = q.filters[col.key];
		if (!v) continue;
		const dto = toColumnFilterWire(v);
		if (dto) filters[col.key] = dto;
	}
	const date = q.filters['date'];
	const owner = new URLSearchParams(location.search).get('OwnerUserId');
	return {
		filters,
		...(date?.from ? { startDate: date.from } : {}),
		...(date?.to ? { endDate: date.to } : {}),
		...(owner ? { ownerUserId: owner } : {}),
		sortColumn: q.sort.column,
		sortDirection: q.sort.direction,
		page: q.page,
		pageSize: q.pageSize,
		...(spatialFilterJson ? { shape: spatialFilterJson } : {}),
	};
}

function buildPageUrl(columns: GridColumn<ActivityRow>[], q: GridQuery): string {
	const p = new URLSearchParams(location.search);
	clearGenericFilterParams(columns, p);
	OWNED_URL_KEYS.forEach((k) => p.delete(k));
	serializeFilters(columns, q.filters, p, urlOverrides);
	p.set('SortColumn', q.sort.column);
	p.set('SortDirection', q.sort.direction);
	return p.toString();
}

function parseInitialQuery(
	columns: GridColumn<ActivityRow>[],
	defaultSort: { column: string; direction: 'asc' | 'desc' },
	pageSize: number,
	multiUser: boolean,
): Partial<GridQuery> {
	const p = new URLSearchParams(location.search);
	const filters = parseFilters(columns, p, urlOverrides);

	// Legacy fallback: hydrate the type combo's checks from the old repeated `SelectedActivityTypes` param
	// when the new f.type.* keys are absent (old bookmarks). Rewritten to f.type.* on the next state change.
	if (!filters.type) {
		const selected = p.getAll('SelectedActivityTypes');
		if (selected.length) filters.type = { selected };
	}

	// Legacy fallback: hydrate name/distance/ascent from the old param names when the new f.* keys are absent.
	// Old numeric two-bound params become a range expression (then get rewritten to f.<key>.range on change).
	if (!filters.name) {
		const name = p.get('fName');
		if (name) filters.name = { text: name };
	}
	if (!filters.distance) {
		const expr = legacyBoundsToExpr(p.get('fDistMin'), p.get('fDistMax'));
		if (expr) filters.distance = { expr };
	}
	if (!filters.ascent) {
		const expr = legacyBoundsToExpr(p.get('fAscMin'), p.get('fAscMax'));
		if (expr) filters.ascent = { expr };
	}

	// Default the owner filter to "Me" on a fresh load (no owner param) in multi-user mode. Selecting Everyone
	// is an explicit choice serialized as f.owner.in=__everyone__, so it hydrates into filters.owner and is
	// left untouched here — only a truly absent owner param falls back to Me, keeping fresh-load and reset
	// behaviour identical.
	if (multiUser && !filters.owner) filters.owner = { selected: [OWNER_ME_TOKEN] };

	const column = p.get('SortColumn') || defaultSort.column;
	const direction = (p.get('SortDirection') as 'asc' | 'desc') || defaultSort.direction;
	return { filters, sort: { column, direction }, page: 1, pageSize };
}

// ── wiring ─────────────────────────────────────────────────────────────────────

function visibleKeysFor(extraColumns: string[], multiUser: boolean): Set<string> {
	const keys = new Set(extraColumns);
	if (multiUser) keys.add('owner');
	return keys;
}

// Persists the user's chosen optional columns to the cookie the server reads to pre-render the next load.
// The owner column is mode-driven (not a user choice), so it's excluded from the stored extras.
function persistExtraColumns(keys: Set<string>): void {
	const extras = Array.from(keys).filter((k) => k !== 'owner');
	document.cookie = `activityExtraColumns=${encodeURIComponent(extras.join(','))};path=/;max-age=${365 * 86400}`;
}

// Marking is instant + local: toggle state, swap the clicked button in place, let the
// `:has([data-pending-remove])` CSS rule grey the row. Deletion waits for the toolbar Delete button.
function setupDeleteDelegation(mount: HTMLElement): void {
	mount.addEventListener('click', (e) => {
		const btn = (e.target as HTMLElement).closest<HTMLElement>('[data-grid-action="delete"]');
		if (!btn || !deleteCtl) return;
		const marked = deleteCtl.toggleRemoval(Number(readData(btn, 'id')));
		if (marked) {
			btn.setAttribute('data-pending-remove', '');
			btn.title = 'Undo delete';
			btn.classList.remove('btn-cockpit-danger');
			btn.classList.add('btn-cockpit-ghost');
			btn.innerHTML = '<i class="bi bi-arrow-counterclockwise"></i>';
		} else {
			btn.removeAttribute('data-pending-remove');
			btn.title = 'Mark for deletion (applied on Delete)';
			btn.classList.remove('btn-cockpit-ghost');
			btn.classList.add('btn-cockpit-danger');
			btn.innerHTML = '<i class="bi bi-trash"></i>';
		}
	});
}

// Flips the ?select=1 URL param and reloads — the same precedent as the editor-pick front door — so the
// grid rebuilds with (or without) the checkbox column under its own persisted order. Current filters live
// in the URL already, so they survive the reload.
function setManageParam(on: boolean): void {
	const p = new URLSearchParams(location.search);
	if (on) p.set(MANAGE_PARAM, '1');
	else p.delete(MANAGE_PARAM);
	location.search = p.toString();
}

/** Leave Select mode (Cancel/Done route here via the bulk controller's `exit` hook). */
function exitSelectMode(): void {
	setManageParam(false);
}

/** The normal-mode "Select" button that enters manage mode. Manage mode instead shows the bulk bar,
 *  which owns its own Done/Cancel — so this button is browsing-only. */
function mountSelectToggle(toolbar: HTMLElement): void {
	const btn = document.createElement('button');
	btn.type = 'button';
	btn.className = 'grid-toolbar-btn act-select-toggle';
	btn.innerHTML = '<i class="bi bi-check2-square"></i> Select';
	btn.title = 'Select multiple activities to bulk-edit or delete';
	btn.addEventListener('click', () => setManageParam(true));
	toolbar.appendChild(btn);
}

function init(): void {
	const boot = readIsland<ActivitiesGridBootstrap | null>('activitiesGridBootstrap', null);
	const mount = document.getElementById('activitiesGrid');
	if (!boot || !mount) return;

	const defaultSort = boot.sort ?? { column: 'date', direction: 'desc' as const };

	// Editor front-door: a leading checkbox column to pick tracks for the track editor. Only in this mode do
	// we add the selection column + getRowId + onSelectionChange — normal /Activities is byte-identical.
	const pickMode = new URLSearchParams(location.search).get(EDITOR_PICK_PARAM) === '1';
	// Select (bulk-management) mode — mutually exclusive with editor-pick. Adds the checkbox column + bulk bar.
	const manageMode = !pickMode && new URLSearchParams(location.search).get(MANAGE_PARAM) === '1';

	// Manage mode replaces the per-row mark-delete with the bulk bar, so no delete-controller there.
	deleteCtl = manageMode ? null : createDeleteController();
	bulkCtl = manageMode ? createBulkController(boot.typeOptions ?? []) : null;
	const baseColumns = buildColumns(boot.typeOptions ?? [], boot.ownerOptions ?? [], manageMode);
	const columns = pickMode
		? [pickSelectionColumn(), ...baseColumns]
		: manageMode
			? [manageSelectionColumn(), ...baseColumns]
			: baseColumns;
	// The map boots with the server's owner-scoped universe — activities-page-init reads THIS island and
	// applies the same precedence, so the two cannot start from different sets. Every grid response
	// re-declares it as `ownerIds`, so owner changes always redraw the map exactly.
	const initialUniverse = boot.initialData?.ownerIds ?? boot.initialData?.filteredIds ?? [];
	const mapDisplay = createMapDisplayController(initialUniverse, boot.initialData?.filteredIds ?? []);
	setupMapDisplayModeToggle(mapDisplay);

	// Latest filtered id set (all pages) — the bulk bar's hidden-rows guard reads it at commit time,
	// because the Select-mode selection deliberately survives filter narrowing (FE-07).
	let visibleFilteredIds = new Set<number>(boot.initialData?.filteredIds ?? []);

	// Header "Export GPX (N)": follows the same filtered set, refreshed in onData below.
	const exportCtl = createExportController('actExportGpxBtn');
	exportCtl.setFiltered(boot.initialData?.filteredIds ?? [], boot.initialData?.ownedFilteredCount ?? 0);

	// Serialized adopted-filter set from the last onData — the change guard for activitiesGridFiltersChanged.
	let lastGridFiltersKey = '';

	const grid = new GridView<ActivityRow>({
		mount,
		columns,
		dataSource: new HttpPostGridDataSource<ActivityRow>('/api/activities/grid', (q) => buildApiBody(columns, q)),
		defaultSort,
		pageSize: boot.pageSize,
		initialData: boot.initialData as GridDataResponse<ActivityRow>,
		initialQuery: parseInitialQuery(columns, defaultSort, boot.pageSize, boot.multiUser),
		defaultFilters: defaultOwnerFilters(boot.multiUser),
		visibleKeys: visibleKeysFor(boot.extraColumns ?? [], boot.multiUser),
		// Selection wiring (pick or manage mode): checkbox selection → the mode's consumer. Editor-pick writes
		// the top-priority 'pick' map source (bright picks, dim rest) + publishes to the front-door bar; Select
		// feeds the bulk bar's action set. Normal /Activities sets neither, so it stays byte-identical.
		...(pickMode || manageMode
			? {
				getRowId: (r: ActivityRow) => r.trackId,
				onSelectionChange: (key: string, ids: Set<number>) => {
					if (key === EDITOR_PICK_SELECTION_KEY) {
						const arr = [...ids];
						publishPickedIds(arr); // cache first, so the bar reads fresh marks when the highlight fires
						setOverviewHighlight('pick', arr.length ? arr : null);
					} else if (key === MANAGE_SELECTION_KEY) {
						bulkCtl?.setSelection(ids);
					}
				},
			}
			: {}),
		toolbarMount: document.getElementById('activitiesGridToolbar') ?? undefined,
		toolbar: { count: true, resetFilters: true, resetAll: true, columns: true },
		defaultVisibleKeys: visibleKeysFor([], boot.multiUser),
		onVisibleKeysChange: persistExtraColumns,
		emptyMessage: 'No activities found matching the selected filters.',
		tableClass: 'act-table',
		// Separate persist key per selection mode: the normal-mode saved column order has no checkbox-column
		// entry, and GridColumnOrder ranks unknown keys last — which would shove the checkbox column to the far
		// right. A distinct key keeps each mode's order/widths fresh (checkbox stays leftmost) and isolated.
		persistWidthsKey: pickMode ? 'activities-pick-v1' : manageMode ? 'activities-manage-v1' : 'activities-v2',
		onData: (res, q) => {
			visibleFilteredIds = new Set(res.filteredIds);
			// Same narrowing idiom as activities-map-display's ownerIds read: the core grid response is
			// generic, the activities-specific counts ride along on it.
			exportCtl.setFiltered(res.filteredIds, (res as ActivityGridResponse).ownedFilteredCount ?? 0);
			// Non-owner column filters (date/name/distance/…) are the "dimming" filters; owner is excluded
			// because it defines which tracks exist on the map (its universe), not which are highlighted.
			const colFilters = collectGridFilters(columns, (k) => q.filters[k]);
			// Drives the instant map filter: a debounced redraw/re-highlight per the display mode.
			// The initial map paint is done separately by activities-page-init.
			mapDisplay.onGridData(res, Object.keys(colFilters).length > 0 || spatialFilterJson !== null); // shape narrows filteredIds too → must dim
			// Notify the shape-filter's "Adopt filters from table" (live mode) — but only when the filter
			// set actually changed, so sort/page (which reuse the same filters) don't spam preview refreshes.
			const key = JSON.stringify(colFilters);
			if (key !== lastGridFiltersKey) {
				lastGridFiltersKey = key;
				window.dispatchEvent(new CustomEvent('activitiesGridFiltersChanged'));
			}
		},
		// Preserve the entry's state — passing null here wipes the table's saved scroll offsets
		// (core/scroll-restore.ts) on the first query change. Idiom from edit-mode-chrome.ts.
		onQueryChange: (q) => history.replaceState(history.state, '', `?${buildPageUrl(columns, q)}`),
	});

	// Owner scope bridge for shape segments: the grid owns the owner filter (Me/Others/Everyone/names), so the
	// shape-filter paint UI reads it on save (so the matched owners equal the owners shown on the map) and
	// restores it when editing a scoped segment. Wire format mirrors the server ColumnFilter ({ in, text }).
	// Adopted-filters bridge: the shape-filter panel's "Adopt filters from table" reads the grid's full
	// current column filters (date, name, type, distance, HR, …) to embed as live segment rules. Read on
	// demand for preview/save; live edits notify via the activitiesGridFiltersChanged event (see onData).
	registerGridFilterApi({
		getOwnerFilter: () => {
			const v = grid.getFilter('owner');
			if (!v) return null;
			return { in: v.selected ?? [], text: v.text ?? '' };
		},
		setOwnerFilter: (filter) => {
			grid.setFilter('owner', { selected: filter?.in ?? [], text: filter?.text ?? '' });
		},
		getGridFilters: () => {
			const map = collectGridFilters(columns, (k) => grid.getFilter(k));
			return Object.keys(map).length ? map : null;
		},
		// "Merge table ↔ shapes": the panel pushes its drawn shape here; the grid folds it into the API request
		// (buildApiParams) and reloads, so the table restricts to shape ∩ columns and the map follows filteredIds.
		// Guard on the serialized doc so a live column edit re-firing with the same shape can't loop reloads.
		setSpatialFilter: (rules) => {
			const json = rules ? JSON.stringify(rules) : null;
			if (json === spatialFilterJson) return;
			spatialFilterJson = json;
			grid.reload();
		},
	});

	// Editor front-door: the bar's global Clear routes back here (the header master only clears the filtered
	// scope; picks may include rows filtered out of view).
	if (pickMode) registerPickPublisher({ clearPicks: () => grid.clearSelection(EDITOR_PICK_SELECTION_KEY) });

	setupDeleteDelegation(mount); // no-op in manage mode (deleteCtl null + actions cell empty)
	const toolbar = document.getElementById('activitiesGridToolbar');
	if (toolbar) deleteCtl?.mountButton(toolbar);
	// Select mode: browsing shows the "Select" entry button; manage shows the staged-edit bar, which owns
	// its own Done/Cancel and repaints staged rows via the grid. Editor-pick owns its own front-door.
	if (toolbar && !pickMode) {
		if (manageMode) {
			bulkCtl?.attach({ redraw: () => grid.redrawRows(), exit: exitSelectMode, visibleIds: () => visibleFilteredIds });
			bulkCtl?.mountBar(toolbar);
		} else {
			mountSelectToggle(toolbar);
		}
	}

	// Mirror the table's horizontal scroll in a second scrollbar above it (the wrapper is the scroll container).
	const wrapper = mount.closest<HTMLElement>('.act-table-wrapper');
	const table = mount.querySelector('table');
	if (wrapper && table) attachTopScrollbar(wrapper, table);
	if (wrapper) attachScrollChain(wrapper);

	restoreScrollOnBack(wrapper);
}

/**
 * Put both scrollers back where they were on browser Back. This page has TWO: the page itself
 * (.cockpit-scroll) and the table's constant-size window (.act-table-wrapper).
 *
 * Pixel-only, not anchored: the rows render synchronously from `boot.initialData` at a fixed height
 * into a window whose height is a CSS clamp, so nothing above or inside the table can shift between
 * capture and restore. (Anchoring is not available anyway — GridView only stamps `data-row-id` when
 * `getRowId` is set, which normal /Activities deliberately does not do.)
 *
 * Page first, then the table: attachScrollChain flips the wrapper's overflow-y off the page's scroll
 * position, so restoring the page first leaves the wrapper in the state the user actually left it in.
 */
function restoreScrollOnBack(wrapper: HTMLElement | null): void {
	const page = document.querySelector<HTMLElement>('.cockpit-scroll');
	if (page) createScrollRestore({ scroller: page, stateKey: 'actPageScroll' }).restore();
	if (wrapper) createScrollRestore({ scroller: wrapper, stateKey: 'actTableScroll' }).restore();
}

if (document.readyState === 'loading') {
	document.addEventListener('DOMContentLoaded', init);
} else {
	init();
}

export {};
