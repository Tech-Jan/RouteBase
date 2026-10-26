/**
 * Segment Statistics — "All Efforts" grid.
 *
 * Rebuilds the former bespoke efforts table on the reusable client grid (core/grid), gaining a filter
 * row, a column show/hide picker, and drag resize/reorder. Two **selection columns** drive the page:
 *   - visibility: which effort tracks/lines show on the map + charts ("filtered view drives the map" —
 *     the effective set is the checked rows INTERSECTED with the current table filter).
 *   - statsbox:   which efforts appear in the hover readout box / are drawn full-opacity (selection is
 *     the single source of truth — independent of the filter).
 *
 * Sorting/filtering/visibility only update the table + the Apply button's dirty state (cheap, in-memory) —
 * they no longer poke the charts/map on every keystroke or sort click. The charts/map only resync, and
 * pending row removals only take effect, when the user clicks Apply (segment-stats-apply-controller.ts).
 */

import { readData } from '../core/data-attrs';
import type { EffortRow } from './segment-stats-effort-types';
import type { GridColumn, GridDataResponse, GridFilterValue } from '../core/grid/grid-types';
import { GridView, escapeHtml } from '../core/grid/grid-view';
import { InMemoryGridDataSource } from '../core/grid/grid-data-source';
import { matchesText } from '../core/grid/grid-predicates';
import { attachTopScrollbar } from '../core/grid/grid-top-scrollbar';
import { attachScrollChain } from '../core/grid/grid-scroll-chain';
import { N0, N1, N2, pad2, formatDuration } from '../core/grid/grid-format';
import { OWNER_ME_TOKEN, OWNER_OTHERS_TOKEN, OWNER_EVERYONE_TOKEN, OWNER_KEYWORD_OPTIONS } from '../core/grid/owner-tokens';
import { ctx } from './segment-statistics';
import { createApplyController, type ApplyController } from './segment-stats-apply-controller';
import { must } from '../core/invariant';

let applyCtl: ApplyController | null = null;

// ── value formatting (shared helpers in core/grid/grid-format; date deliberately locale-styled) ──
function formatDate(iso: string | null): string {
	if (!iso) return '';
	const d = new Date(iso);
	if (Number.isNaN(d.getTime())) return '';
	return d.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: '2-digit' });
}

/** Pace in min/km from speed (m/s); '--' when unknown. */
function formatPace(speedMs: number | null): string {
	const kmh = (speedMs ?? 0) * 3.6;
	if (kmh <= 0) return '<span class="seg-dim">--</span>';
	const total = Math.round((60 / kmh) * 60); // seconds per km
	return `${Math.floor(total / 60)}:${pad2(total % 60)} /km`;
}

// ── KPI strip efforts count ──────────────────────────────────────────────────
/** Update the "<shown> / <total>" efforts count as the filter changes. The distance/elevation/time
 *  "/ Total" readouts are server-rendered from precomputed aggregates (matching the segment list)
 *  and are NOT summed client-side, so they stay fixed regardless of the active filter. */
function updateKpiTotals(rows: EffortRow[]): void {
	const el = document.getElementById('kpiEffortsShown');
	if (el) el.textContent = N0.format(rows.length);
}

// ── cell renderers ───────────────────────────────────────────────────────────
function rankCell(r: EffortRow): string {
	const colors = ctx.effortColors;
	const color = colors.length ? colors[r.colorIndex % colors.length] : '#888';
	const dot = `<span class="effort-color-dot" style="background:${color}"></span>`;
	const rank = r.effortIndex + 1;
	const medal =
		rank === 1
			? '<span class="rank-medal rank-gold">1</span>'
			: rank === 2
				? '<span class="rank-medal rank-silver">2</span>'
				: rank === 3
					? '<span class="rank-medal rank-bronze">3</span>'
					: `<span>${rank}</span>`;
	return dot + medal;
}

function matchCell(r: EffortRow): string {
	const cls = r.matchPct >= 90 ? 'seg-match-high' : r.matchPct >= 75 ? 'seg-match-med' : 'seg-match-low';
	return `<span class="seg-match-badge ${cls}">${r.matchPct}%</span>`;
}

function riderCell(r: EffortRow): string {
	return r.isOwn
		? '<small class="seg-rider-me">Me</small>'
		: `<small class="seg-rider-other"><i class="bi bi-person"></i> ${escapeHtml(r.userDisplayName)}</small>`;
}

// Item 25: an excluded effort shows an "Excluded" badge; an active effort shows nothing in this column.
function excludedCell(r: EffortRow): string {
	return r.excluded ? '<span class="seg-match-badge seg-match-low">Excluded</span>' : '';
}

function actionsCell(r: EffortRow): string {
	// Item 25: excluded rows offer Restore (delete the exclusion on Apply); active rows offer Exclude.
	if (r.excluded) {
		const marked = applyCtl?.isRestoreMarked(r.gpsInSegmentId) ?? false;
		if (marked) {
			return `<button class="btn-cockpit btn-cockpit-ghost btn-cockpit-xs" data-grid-action="restore" data-id="${r.gpsInSegmentId}" data-pending-restore title="Undo restore"><i class="bi bi-x-circle"></i></button>`;
		}
		return `<button class="btn-cockpit btn-cockpit-ghost btn-cockpit-xs" data-grid-action="restore" data-id="${r.gpsInSegmentId}" title="Restore this effort (applied on Apply)"><i class="bi bi-arrow-counterclockwise"></i></button>`;
	}
	const marked = applyCtl?.isMarked(r.gpsInSegmentId) ?? false;
	if (marked) {
		return `<button class="btn-cockpit btn-cockpit-ghost btn-cockpit-xs" data-grid-action="remove" data-id="${r.gpsInSegmentId}" data-pending-remove title="Undo removal"><i class="bi bi-arrow-counterclockwise"></i></button>`;
	}
	return `<button class="btn-cockpit btn-cockpit-danger btn-cockpit-xs" data-grid-action="remove" data-id="${r.gpsInSegmentId}" title="Exclude from totals/charts (applied on Apply)"><i class="bi bi-x-circle"></i></button>`;
}

// Optional columns shown by default (others stay hidden until toggled in the Columns picker).
const DEFAULT_VISIBLE_KEYS = new Set<string>(['type', 'distance', 'avgspeed', 'elevation']);

/** Distinct, sorted activity types across the effort rows → combo options for the Type column. */
function buildTypeOptions(rows: EffortRow[]): { value: string; label: string }[] {
	const types = new Set<string>();
	for (const r of rows) {
		if (r.activityType) types.add(r.activityType);
	}
	return Array.from(types)
		.sort((a, b) => a.localeCompare(b))
		.map((t) => ({ value: t, label: t }));
}

// Rider combo keywords — the shared Me/Others/Everyone tokens (core/grid/owner-tokens), matched
// client-side against EffortRow.isOwn via the column's `clientFilter`, OR'd with the name search.
function matchesRider(r: EffortRow, f: GridFilterValue | undefined): boolean {
	const selected = f?.selected ?? [];
	const text = f?.text?.trim();
	if (selected.length === 0 && !text) return true; // no rider constraint
	if (selected.includes(OWNER_EVERYONE_TOKEN)) return true;
	if (selected.includes(OWNER_ME_TOKEN) && r.isOwn) return true;
	if (selected.includes(OWNER_OTHERS_TOKEN) && !r.isOwn) return true;
	if (text && matchesText(r.userDisplayName, text)) return true;
	return false;
}

// ── column schema ──────────────────────────────────────────────────────────────
/**
 * Capabilities this render offers. Server-supplied per ROUTE, not per identity — a share view turns them
 * all off even for the segment's owner.
 *
 * These controls are built CLIENT-side, so no server-side render guard can see them; the vitest cases in
 * segment-stats-efforts-table.test.ts are the only thing pinning them.
 */
export interface EffortTableCaps {
	canExcludeEfforts: boolean;
	showRiderIdentity: boolean;
	canLinkToActivities: boolean;
}

/** exported for tests */
export function buildColumns(hasForeignRiders: boolean, typeOptions: { value: string; label: string }[], caps: EffortTableCaps): GridColumn<EffortRow>[] {
	const cols: GridColumn<EffortRow>[] = [
		{ key: 'visibility', header: '', width: 40, cellClass: 'grid-sel-cell',
			selection: { key: 'visibility', headerIcon: '<i class="bi bi-eye"></i>', title: 'Visible on map/chart (header: select all/none of the filtered rows)' } },
		{ key: 'statsbox', header: '', width: 40, cellClass: 'grid-sel-cell',
			selection: { key: 'statsbox', headerIcon: '<i class="bi bi-card-list"></i>', title: 'Show value in hover statsbox (header: select all/none of the filtered rows)' } },
		{ key: 'rank', header: 'Rank', sortable: true, width: 70, getValue: (r) => r.effortIndex, formatter: rankCell },
		{ key: 'activity', header: 'Activity', sortable: true,
			filter: { type: 'text', placeholder: '*name*' },
			getValue: (r) => r.activityName,
			// Plain text, not an anchor, when the route cannot link into the app: a share view must render
			// zero hrefs, and a link to /Activities/Details would be a dead drop into the login flow.
			formatter: (r) => `<div>${caps.canLinkToActivities
				? `<a class="seg-eff-name-link" href="/Activities/Details/${r.activityId}">${escapeHtml(r.activityName)}</a>`
				: escapeHtml(r.activityName)}</div><small class="seg-act-type">${escapeHtml(r.activityType)}</small>` },
		{ key: 'type', header: 'Type', sortable: true, width: 130, optionalKey: 'type',
			filter: { type: 'combo', options: typeOptions, placeholder: '*type*' },
			getValue: (r) => r.activityType,
			formatter: (r) => escapeHtml(r.activityType ?? '') },
	];
	// Rider identity is a route capability AND a data question: both must hold.
	if (hasForeignRiders && caps.showRiderIdentity) {
		cols.push({ key: 'rider', header: 'Rider', sortable: true, filter: { type: 'combo', options: OWNER_KEYWORD_OPTIONS, placeholder: '*rider*' },
			getValue: (r) => r.userDisplayName, clientFilter: (r, v) => matchesRider(r, v), formatter: riderCell });
	}
	// Numeric getValue accessors are in the displayed unit — the unit the user types into the range filter.
	cols.push(
		{ key: 'date', header: 'Date', sortable: true, cellClass: 'text-nowrap', width: 96, filter: { type: 'daterange' }, getValue: (r) => r.dateIso, formatter: (r) => formatDate(r.dateIso) },
		{ key: 'match', header: 'Match %', sortable: true, align: 'end', dataType: 'number', filterable: true, getValue: (r) => r.matchPct, formatter: matchCell },
		{ key: 'time', header: 'Time', sortable: true, align: 'end', cellClass: 'text-nowrap', dataType: 'number', filterable: true, getValue: (r) => r.elapsedSec, formatter: (r) => `<strong>${formatDuration(r.elapsedSec)}</strong>` },
		{ key: 'distance', header: 'Distance (km)', sortable: true, align: 'end', optionalKey: 'distance', dataType: 'number', filterable: true, getValue: (r) => r.distanceM / 1000, formatter: (r) => `${N2.format(r.distanceM / 1000)} km` },
		{ key: 'avgspeed', header: 'Avg Speed (km/h)', sortable: true, align: 'end', optionalKey: 'avgspeed', dataType: 'number', filterable: true, getValue: (r) => (r.avgSpeedMs ?? 0) * 3.6, formatter: (r) => `${N1.format((r.avgSpeedMs ?? 0) * 3.6)} km/h` },
		{ key: 'avgpace', header: 'Avg Pace', sortable: true, align: 'end', optionalKey: 'avgpace', cellClass: 'text-nowrap', getValue: (r) => (r.avgSpeedMs && r.avgSpeedMs > 0 ? 1 / r.avgSpeedMs : null), formatter: (r) => formatPace(r.avgSpeedMs) },
		{ key: 'avghr', header: 'Avg HR', sortable: true, align: 'end', optionalKey: 'avghr', dataType: 'number', filterable: true, getValue: (r) => r.avgHr, formatter: (r) => (r.avgHr != null ? `${N0.format(r.avgHr)} bpm` : '<span class="seg-dim">--</span>') },
		{ key: 'avgpower', header: 'Avg Power (W)', sortable: true, align: 'end', optionalKey: 'avgpower', dataType: 'number', filterable: true, getValue: (r) => r.avgPower, formatter: (r) => (r.avgPower != null ? `${N0.format(r.avgPower)} W` : '<span class="seg-dim">--</span>') },
		{ key: 'elevation', header: 'Elevation (m)', sortable: true, align: 'end', optionalKey: 'elevation', dataType: 'number', filterable: true, getValue: (r) => r.elevationM, formatter: (r) => `${N0.format(r.elevationM)} m` },
	);
	// The exclusion status column + removal/restore actions + their delegation are owner-only: the
	// server DELETE/restore gate on OwnedSegments, so a non-owner marking rows only produces a failed
	// batch + pointless reload (SS-01, I-8a). Non-owners never see exclusion state.
	if (caps.canExcludeEfforts) {
		cols.push({ key: 'excluded', header: 'Excluded', width: 96, getValue: (r) => (r.excluded ? 1 : 0), formatter: excludedCell });
		cols.push({ key: 'actions', header: '', cellClass: 'seg-eff-actions', formatter: actionsCell });
	}
	return cols;
}

// ── wiring ───────────────────────────────────────────────────────────────────
/** Marking for removal is instant + local: toggle state, swap the clicked button in place, let the
 *  `:has([data-pending-remove])` CSS rule grey the row. Actual deletion waits for Apply. */
function setupRemoveDelegation(mount: HTMLElement): void {
	mount.addEventListener('click', (e) => {
		const target = e.target as HTMLElement;
		if (!applyCtl) return;

		// Item 25: excluded rows toggle a pending-restore instead of a pending-removal.
		const restoreBtn = target.closest<HTMLElement>('[data-grid-action="restore"]');
		if (restoreBtn) {
			const id = Number(readData(restoreBtn, 'id'));
			const marked = applyCtl.toggleRestore(id);
			if (marked) {
				restoreBtn.setAttribute('data-pending-restore', '');
				restoreBtn.title = 'Undo restore';
				restoreBtn.innerHTML = '<i class="bi bi-x-circle"></i>';
			} else {
				restoreBtn.removeAttribute('data-pending-restore');
				restoreBtn.title = 'Restore this effort (applied on Apply)';
				restoreBtn.innerHTML = '<i class="bi bi-arrow-counterclockwise"></i>';
			}
			return;
		}

		const btn = target.closest<HTMLElement>('[data-grid-action="remove"]');
		if (!btn) return;
		const id = Number(readData(btn, 'id'));
		const marked = applyCtl.toggleRemoval(id);
		if (marked) {
			btn.setAttribute('data-pending-remove', '');
			btn.title = 'Undo removal';
			btn.classList.remove('btn-cockpit-danger');
			btn.classList.add('btn-cockpit-ghost');
			btn.innerHTML = '<i class="bi bi-arrow-counterclockwise"></i>';
		} else {
			btn.removeAttribute('data-pending-remove');
			btn.title = 'Exclude from totals/charts (applied on Apply)';
			btn.classList.remove('btn-cockpit-ghost');
			btn.classList.add('btn-cockpit-danger');
			btn.innerHTML = '<i class="bi bi-x-circle"></i>';
		}
	});
}

function init(): void {
	// segment-statistics (shared chunk) parses the #segmentStatisticsConfig JSON tag; ctx is
	// null! on config-less pages, so guard before touching it.
	const config = ctx?.config;
	const mount = document.getElementById('segEffortsGrid');
	const rows = config?.effortRows;
	if (!config || !mount || !rows || rows.length === 0) return;

	applyCtl = createApplyController();

	const caps: EffortTableCaps = {
		canExcludeEfforts: config.canExcludeEfforts === true,
		showRiderIdentity: config.showRiderIdentity === true,
		canLinkToActivities: config.canLinkToActivities === true,
	};
	const columns = buildColumns(config.hasForeignRiders === true, buildTypeOptions(rows), caps);
	const dataSource = new InMemoryGridDataSource<EffortRow>(rows, {
		getId: (r) => r.effortIndex,
		columns, // filtering + sorting derived from the schema (getValue + filter types / clientFilter)
	});

	// The effective map/chart set = checked ∩ filtered. Recomputed on both selection AND filter changes,
	// but only pushed to the charts/map when the user clicks Apply (applyCtl.noteTableState tracks it).
	let filteredIds = rows.map((r) => r.effortIndex);
	const visibleSet = new Set<number>(ctx.effortVisibility);

	new GridView<EffortRow>({
		mount,
		columns,
		dataSource,
		defaultSort: { column: 'rank', direction: 'asc' },
		visibleKeys: new Set(DEFAULT_VISIBLE_KEYS),
		defaultVisibleKeys: DEFAULT_VISIBLE_KEYS,
		getRowId: (r) => r.effortIndex,
		initialSelection: {
			visibility: Array.from(ctx.effortVisibility),
			statsbox: Array.from(ctx.effortStats),
		},
		toolbarMount: document.getElementById('segEffortsToolbar') ?? undefined,
		toolbar: { count: true, resetFilters: true, resetAll: true, columns: true },
		persistWidthsKey: 'seg-efforts-v1',
		emptyMessage: 'No efforts match the current filters.',
		onData: (res: GridDataResponse<EffortRow>) => {
			filteredIds = res.filteredIds;
			must(applyCtl, 'created before the grid mounts').noteTableState(filteredIds, visibleSet);
			updateKpiTotals(res.rows);
		},
		onSelectionChange: (key, selectedIds, ids) => {
			if (key === 'visibility') {
				visibleSet.clear();
				selectedIds.forEach((id) => visibleSet.add(id));
				filteredIds = ids;
				must(applyCtl, 'created before the grid mounts').noteTableState(filteredIds, visibleSet);
			} else if (key === 'statsbox') {
				ctx.effortStats.clear();
				selectedIds.forEach((id) => ctx.effortStats.add(id));
				ctx._applyStatsDimming?.();
			}
		},
	});

	const toolbarEl = document.getElementById('segEffortsToolbar');
	if (toolbarEl) applyCtl.mountButton(toolbarEl); // markup drift = no toolbar, same as toolbarMount's ?? undefined
	if (caps.canExcludeEfforts) setupRemoveDelegation(mount);

	const wrapper = mount.closest<HTMLElement>('.table-responsive');
	const table = mount.querySelector('table');
	if (wrapper && table) attachTopScrollbar(wrapper, table);
	if (wrapper) attachScrollChain(wrapper);
}

if (document.readyState === 'loading') {
	document.addEventListener('DOMContentLoaded', init);
} else {
	init();
}

export {};
