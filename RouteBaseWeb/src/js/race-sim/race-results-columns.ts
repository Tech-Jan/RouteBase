/**
 * The results table's column schema. Pure — it declares columns, it does not render a grid.
 *
 * The grid (core/grid) derives filtering and sorting FROM this schema (`getValue` + the filter type), so
 * there are no per-column predicate switches anywhere: declaring a column is the whole integration.
 *
 * One rule inherited from the other grids and easy to get wrong: **`getValue` must be in the unit the
 * user types into the filter**. Distances expose km because the range filter reads km; leg times expose
 * MINUTES for the same reason — `20-40` is a sentence a person can write, `1200-2400` is not — while the
 * formatter still renders `1:24:56`.
 */

import type { GridColumn, FilterOption } from '../core/grid/grid-types';
import { escapeHtml } from '../core/html';
import { formatElapsed } from '../core/format';
import { REAL_COLOR, SIMULATED_COLOR } from './race-build';
import { finishStatusCell, finishStatusOptions } from './race-finish-status';
import type { RaceLegColumn, RaceResultRow } from './race-results-row';

/**
 * The colour a rider's swatch shows. The constants come from race-build so the table and the map cannot
 * drift apart — a swatch that disagrees with the dot on the map is worse than no swatch.
 */
export function defaultColorFor(row: RaceResultRow): string {
	return row.color ?? (row.isReal ? REAL_COLOR : SIMULATED_COLOR);
}

const SECONDS_PER_MINUTE = 60;

/** Seconds → the minutes the range filter is written in. Null stays null: "no time" is not "zero". */
function minutes(seconds: number | null): number | null {
	return seconds == null ? null : seconds / SECONDS_PER_MINUTE;
}

const EMPTY_CELL = '<span class="race-dim">--</span>';

function elapsedCell(seconds: number | null): string {
	return seconds == null ? EMPTY_CELL : formatElapsed(seconds);
}

/**
 * The gap between the two clocks, signed and classed.
 *
 *   over  (+) — the legs sum PAST the published finish: a forced start overlapped two legs, so the
 *               published clock is compressed. The rider is replayed on his LEGS, so he finishes later
 *               here than the published result says. 36 of 508 Ski to Sea teams, by up to 2.3 h.
 *   under (−) — time passed that no leg was counting: an untimed transition between legs.
 */
function deltaCell(row: RaceResultRow): string {
	if (row.deltaSeconds == null || row.agreement == null) return EMPTY_CELL;
	if (row.agreement === 'match') return '<span class="race-dim">±0</span>';

	const sign = row.deltaSeconds > 0 ? '+' : '−';
	const magnitude = formatElapsed(Math.abs(row.deltaSeconds));
	return `<span class="race-delta race-delta-${row.agreement}">${sign}${magnitude}</span>`;
}

function swatchCell(row: RaceResultRow): string {
	// A background-color built from a value the SERVER validated to #rrggbb (RaceColors). The escape is
	// belt-and-braces: this string is interpolated into a style attribute, so it must not be trusted here
	// either, and a formatter that trusts its input is how the next injection gets in.
	const color = defaultColorFor(row);
	const painted = row.color != null ? ' race-swatch-painted' : '';
	// Emphasis has no colour of its own, so the swatch is the only place the table can show it — without
	// it the map has three fat dots and nothing on screen says which rows they are.
	const emphasised = row.emphasised ? ' race-swatch-emphasised' : '';
	// Same argument for the opposite switch: a rider whose call-out is hidden is missing from the map
	// labels and the speed chart, and the owner panel is the only other surface that says so.
	const hushed = row.calloutOff ? ' race-swatch-hushed' : '';
	const title = row.calloutOff
		? ' title="Highlight hidden — no name on the map, no line in the speed chart"'
		: row.emphasised ? ' title="Emphasised — bigger, drawn in front"' : '';
	return `<span class="race-swatch${painted}${emphasised}${hushed}" style="background:${escapeHtml(color)}"${title}></span>`;
}

function nameCell(row: RaceResultRow): string {
	const real = row.isReal ? ' <span class="race-badge-real" title="A real ride, on its own GPS">real</span>' : '';
	return `<span class="race-name-cell">${escapeHtml(row.name)}</span>${real}`;
}

/** Distinct non-null values of a field, sorted — the options for a combo filter. */
export function optionsOf(
	rows: readonly RaceResultRow[], pick: (r: RaceResultRow) => string | null,
): FilterOption[] {
	const values = new Set<string>();
	for (const r of rows) {
		const v = pick(r);
		if (v) values.add(v);
	}
	return [...values].sort((a, b) => a.localeCompare(b)).map((v) => ({ value: v, label: v }));
}

/**
 * Can this column narrow the field? Null counts as a value, so "some riders have a club, some do not"
 * partitions just as "these two clubs" does. Short-circuits at two, so a column that varies costs O(1).
 *
 * A column that reads the same on every row cannot narrow anything: its filter would offer either
 * nothing or one option matching everyone. Both are dead controls that read as a bug.
 */
function partitions(rows: readonly RaceResultRow[], pick: (r: RaceResultRow) => unknown): boolean {
	const seen = new Set<unknown>();
	for (const r of rows) {
		seen.add(pick(r));
		if (seen.size > 1) return true;
	}
	return false;
}

/**
 * The schema plus which of its optional columns start shown.
 *
 * Two decisions that used to be one. **Relevance** is data-driven — can this column partition THIS
 * race's field? **Preference** is the user's, via the Columns picker, and is what `visibleKeys`
 * carries. Relevance seeds the default; preference overrides it.
 *
 * Every column therefore EXISTS on every race, which is what makes the persisted column order
 * (keyed globally, `GridColumnOrder`) mean the same thing from one race to the next. A race with no
 * clubs simply starts with Club hidden rather than having no Club column at all.
 *
 * The coupling that makes this load-bearing: grid-view builds the filter row from the VISIBLE
 * columns (`visibleColumns()` → `buildFilterRow`), so a column that does not ship shown ships with
 * no filter control. Default visibility is therefore not cosmetic — it decides what can be filtered
 * at all, which for this page decides what can be painted.
 */
export interface ResultSchema {
	readonly columns: GridColumn<RaceResultRow>[];
	/** Optional columns that start SHOWN — the ones whose data can partition this race's field. */
	readonly defaultVisibleKeys: Set<string>;
}

/**
 * The schema. `legs` is dynamic — one column per leg, and a leg IS the interval between two consecutive
 * checkpoints, so a race with no intermediate checkpoints yields exactly one column with no special path.
 *
 * `visibleKeys` is the SHOWN set, never the hidden one: this used to be a constant holding `official`
 * and `delta` under a comment reading "columns that start hidden" — the two columns someone wanted
 * HIDDEN, written into the VISIBLE set. The polarity was inverted. Those two are audit columns and are
 * still the only ones deliberately withheld from a race whose data would otherwise earn them a place.
 */
export function buildResultSchema(
	rows: readonly RaceResultRow[], legs: readonly RaceLegColumn[],
): ResultSchema {
	const defaultVisibleKeys = new Set<string>();
	/** Declare an optional column and let its data decide whether it starts shown. */
	const optional = (key: string, pick: (r: RaceResultRow) => unknown): string => {
		if (partitions(rows, pick)) defaultVisibleKeys.add(key);
		return key;
	};

	const columns: GridColumn<RaceResultRow>[] = [
		{
			key: 'color', header: '', width: 34, sortable: false, cellClass: 'race-swatch-cell',
			formatter: swatchCell,
		},
		{
			key: 'pos', header: '#', sortable: true, align: 'end', width: 60,
			dataType: 'number', filterable: true,
			getValue: (r) => r.position,
			formatter: (r) => (r.position == null ? EMPTY_CELL : String(r.position)),
		},
		{
			key: 'name', header: 'Team', sortable: true,
			filter: { type: 'text', placeholder: '*name*' },
			getValue: (r) => r.name,
			formatter: nameCell,
		},
		{
			key: 'bib', header: 'Bib', sortable: true, width: 70,
			optionalKey: optional('bib', (r) => r.bib),
			filter: { type: 'text', placeholder: '*bib*' },
			getValue: (r) => r.bib,
			formatter: (r) => escapeHtml(r.bib ?? ''),
		},
		{
			key: 'category', header: 'Division', sortable: true,
			optionalKey: optional('category', (r) => r.category),
			filter: { type: 'combo', options: optionsOf(rows, (r) => r.category), placeholder: '*division*' },
			getValue: (r) => r.category,
			formatter: (r) => escapeHtml(r.category ?? ''),
		},
		// An event running several races off one start list — the Schauinsland file has eleven. Ships
		// hidden on the races that have one discipline, which is most of them, by the same relevance rule
		// as every other optional column.
		{
			key: 'discipline', header: 'Discipline', sortable: true,
			optionalKey: optional('discipline', (r) => r.discipline),
			filter: { type: 'combo', options: optionsOf(rows, (r) => r.discipline), placeholder: '*discipline*' },
			getValue: (r) => r.discipline,
			formatter: (r) => escapeHtml(r.discipline ?? ''),
		},
		{
			key: 'gender', header: 'Gender', sortable: true, width: 100,
			optionalKey: optional('gender', (r) => r.gender),
			filter: { type: 'combo', options: optionsOf(rows, (r) => r.gender), placeholder: '*gender*' },
			getValue: (r) => r.gender,
			formatter: (r) => escapeHtml(r.gender ?? ''),
		},
		{
			key: 'club', header: 'Club', sortable: true,
			optionalKey: optional('club', (r) => r.club),
			filter: { type: 'combo', options: optionsOf(rows, (r) => r.club), placeholder: '*club*' },
			getValue: (r) => r.club,
			formatter: (r) => escapeHtml(r.club ?? ''),
		},
	];

	columns.push(
		// Next to the clock whose `--` it explains. No `if` guard: a field where everyone finished
		// cannot partition on this, so `optional` leaves it out of the shown set on its own.
		//
		// The options are built from THIS race's own statuses, not from the ten-value vocabulary: a
		// filter offering `Missing punch` on a road race is a control that matches nothing, which is the
		// exact failure race-finish-status.ts exists to prevent — seen from the other side.
		{
			key: 'status', header: 'Status', sortable: true, width: 110,
			optionalKey: optional('status', (r) => r.status),
			filter: { type: 'multiselect', options: finishStatusOptions(rows.map((r) => r.status)) },
			getValue: (r) => r.status,
			formatter: (r) => finishStatusCell(r.status, r.statusRaw),
		},
		{
			key: 'time', header: 'Time', sortable: true, align: 'end', cellClass: 'text-nowrap',
			dataType: 'number', filterable: true,
			// The accumulated leg sum — the clock he is actually replayed on.
			getValue: (r) => minutes(r.accumulatedSeconds),
			formatter: (r) => `<strong>${elapsedCell(r.accumulatedSeconds)}</strong>`,
		},
		{
			key: 'official', header: 'Published', sortable: true, align: 'end', optionalKey: 'official',
			cellClass: 'text-nowrap', dataType: 'number', filterable: true,
			getValue: (r) => minutes(r.officialSeconds),
			formatter: (r) => elapsedCell(r.officialSeconds),
		},
		{
			key: 'delta', header: 'Δ', sortable: true, align: 'end', optionalKey: 'delta',
			cellClass: 'text-nowrap', dataType: 'number', filterable: true,
			getValue: (r) => minutes(r.deltaSeconds),
			formatter: deltaCell,
		},
	);

	// One column per leg. The header is the leg's name, and its swatch is the colour the leg is drawn in
	// on the map and the profile — so the table and the course read as one object.
	legs.forEach((leg, i) => {
		columns.push({
			key: `leg-${leg.checkpointId}`,
			header: leg.label,
			sortable: true,
			align: 'end',
			cellClass: 'text-nowrap',
			dataType: 'number',
			filterable: true,
			getValue: (r) => minutes(r.legSeconds[i] ?? null),
			formatter: (r) => elapsedCell(r.legSeconds[i] ?? null),
		});
	});

	return { columns, defaultVisibleKeys };
}
