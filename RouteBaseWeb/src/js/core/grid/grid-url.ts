/**
 * Generic, reusable serialization of a grid's filter values to/from query parameters.
 *
 * Every filterable column is read from its `dataType`/`filter` and mapped to the wire format
 * `f.<key>.<suffix>` (`.text`, `.in` repeated, `.min`/`.max`, `.from`/`.to`) — so a grid gains a column's
 * filter param with no per-column code. A per-key {@link FilterCodec} override lets a specific column use a
 * different spelling (e.g. activity type/date sharing the legacy top-panel params).
 */

import type { GridColumn, GridColumnFilter, GridFilterValue, GridFilterValues } from './grid-types';
import { isFilterValueEmpty, resolveColumnFilter } from './grid-types';

/** Custom param spelling for one column (read + write), overriding the generic `f.<key>.*` format. */
export interface FilterCodec {
	write(value: GridFilterValue, params: URLSearchParams): void;
	read(params: URLSearchParams): GridFilterValue | undefined;
}

export type FilterCodecMap = Record<string, FilterCodec>;

/** The generic suffixes one column key can own, so they can be cleared before re-writing. */
const SUFFIXES = ['text', 'in', 'range', 'min', 'max', 'from', 'to'] as const;

/** Builds a range expression from legacy two-bound params (e.g. `5`+`20`→`5-20`, `5`→`>=5`, `20`→`<=20`).
 *  Single bounds use the operator spellings — the `-` prefix/suffix forms break for negative numbers
 *  (`-10` as a max would render `--10`, which both parsers drop). */
export function legacyBoundsToExpr(min: string | null, max: string | null): string | undefined {
	if (min && max) return `${min}-${max}`;
	if (min) return `>=${min}`;
	if (max) return `<=${max}`;
	return undefined;
}

function writeGeneric(key: string, type: GridColumnFilter['type'], v: GridFilterValue, p: URLSearchParams): void {
	switch (type) {
		case 'text':
			if (v.text) p.set(`f.${key}.text`, v.text);
			break;
		case 'multiselect':
			(v.selected ?? []).forEach((s) => p.append(`f.${key}.in`, s));
			break;
		case 'combo':
			if (v.text) p.set(`f.${key}.text`, v.text);
			(v.selected ?? []).forEach((s) => p.append(`f.${key}.in`, s));
			break;
		case 'numberrange':
			if (v.expr) p.set(`f.${key}.range`, v.expr);
			break;
		case 'daterange':
			if (v.from) p.set(`f.${key}.from`, v.from);
			if (v.to) p.set(`f.${key}.to`, v.to);
			break;
	}
}

function readGeneric(key: string, type: GridColumnFilter['type'], p: URLSearchParams): GridFilterValue | undefined {
	switch (type) {
		case 'text': {
			const text = p.get(`f.${key}.text`);
			return text ? { text } : undefined;
		}
		case 'multiselect': {
			const selected = p.getAll(`f.${key}.in`);
			return selected.length ? { selected } : undefined;
		}
		case 'combo': {
			const text = p.get(`f.${key}.text`);
			const selected = p.getAll(`f.${key}.in`);
			return text || selected.length ? { text: text ?? '', selected } : undefined;
		}
		case 'numberrange': {
			const range = p.get(`f.${key}.range`);
			if (range) return { expr: range };
			// Legacy fallback: synthesize an expression from old two-bound params so old links still work.
			const expr = legacyBoundsToExpr(p.get(`f.${key}.min`), p.get(`f.${key}.max`));
			return expr ? { expr } : undefined;
		}
		case 'daterange': {
			const from = p.get(`f.${key}.from`);
			const to = p.get(`f.${key}.to`);
			return from || to ? { from: from ?? '', to: to ?? '' } : undefined;
		}
	}
}

/** Writes each non-empty column filter into `params` (generic format, or the column's codec override). */
export function serializeFilters<TRow>(
	columns: GridColumn<TRow>[],
	filters: GridFilterValues,
	params: URLSearchParams,
	overrides: FilterCodecMap = {},
): void {
	for (const col of columns) {
		const filter = resolveColumnFilter(col);
		if (!filter) continue;
		const value = filters[col.key];
		if (!value || isFilterValueEmpty(value)) continue;

		const codec = overrides[col.key];
		if (codec) codec.write(value, params);
		else writeGeneric(col.key, filter.type, value, params);
	}
}

/** Rebuilds the filter values from `params` (generic format, or the column's codec override). */
export function parseFilters<TRow>(
	columns: GridColumn<TRow>[],
	params: URLSearchParams,
	overrides: FilterCodecMap = {},
): GridFilterValues {
	const filters: GridFilterValues = {};
	for (const col of columns) {
		const filter = resolveColumnFilter(col);
		if (!filter) continue;

		const codec = overrides[col.key];
		const value = codec ? codec.read(params) : readGeneric(col.key, filter.type, params);
		if (value && !isFilterValueEmpty(value)) filters[col.key] = value;
	}
	return filters;
}

/** Deletes every generic `f.<key>.*` param the filterable columns own (used before re-writing the URL). */
export function clearGenericFilterParams<TRow>(columns: GridColumn<TRow>[], params: URLSearchParams): void {
	for (const col of columns) {
		if (!resolveColumnFilter(col)) continue;
		for (const s of SUFFIXES) params.delete(`f.${col.key}.${s}`);
	}
}
