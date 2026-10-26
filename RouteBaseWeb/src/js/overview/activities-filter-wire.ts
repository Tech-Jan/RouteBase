/**
 * Column-filter wire mapping — grid filter values → the server ColumnFilter shape, plus the
 * adopted-filters collection for the shape-segment paint UI. Split out of activities-grid.ts
 * (file-size cap); behaviour unchanged.
 */

import type { GridColumn, GridFilterValue } from '../core/grid/grid-types';
import type { ColumnFilter } from '../generated/contracts';

// ── adopted grid-filters bridge (Activities grid → shape-segment paint UI) ──────
//
// The shape-filter panel's "Adopt filters from table" option matches only tracks passing the grid's
// current column filters, embedding them into the saved segment as live rules. Owner is folded in
// server-side (never a track rule) and `actions` has no filter, so both are excluded.

const ADOPT_EXCLUDED_KEYS = new Set(['owner', 'actions']);

/** The grid emits a subset of the wire ColumnFilter (no legacy min/max) — narrow view (D4) over the
 *  request-relaxed generated type (D26). */
export type GridColumnFilterWire = Pick<ColumnFilter, 'text' | 'in' | 'range' | 'from' | 'to'>;

/** One column filter value → the wire ColumnFilter shape (mirror of C# Models/GridFilter/ColumnFilter);
 *  empty fields dropped, null when nothing is set. `expr` (the grid's numberrange) maps to `range`. */
export function toColumnFilterWire(v: GridFilterValue): GridColumnFilterWire | null {
	const dto: GridColumnFilterWire = {};
	if (v.text && v.text.trim()) dto.text = v.text;
	if (v.selected && v.selected.length) dto.in = [...v.selected];
	if (v.expr && v.expr.trim()) dto.range = v.expr;
	if (v.from && v.from.trim()) dto.from = v.from;
	if (v.to && v.to.trim()) dto.to = v.to;
	return Object.keys(dto).length ? dto : null;
}

/** Current adopted filters keyed by column key (owner/actions excluded, empties dropped). */
export function collectGridFilters<TRow>(
	columns: GridColumn<TRow>[],
	read: (key: string) => GridFilterValue | undefined,
): Record<string, GridColumnFilterWire> {
	const out: Record<string, GridColumnFilterWire> = {};
	for (const col of columns) {
		if (ADOPT_EXCLUDED_KEYS.has(col.key)) continue;
		const v = read(col.key);
		if (!v) continue;
		const dto = toColumnFilterWire(v);
		if (dto) out[col.key] = dto;
	}
	return out;
}
