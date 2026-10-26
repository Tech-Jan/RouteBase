/**
 * Pseudo-`GridColumn[]` schema for the Statistics filter header (owner / type / date). There is no grid
 * here — the columns exist only so the generic grid-url serialize/parse and the shared checkbox popup can
 * be reused: each column's `filter.type` maps to the same `f.<key>.*` wire format the server's
 * `GridFilterModelBinder` reads (`f.owner.in`, `f.type.in`, `f.date.from`/`.to`).
 */

import type { FilterOption, GridColumn, GridFilterValues } from '../core/grid/grid-types';
import { OWNER_KEYWORD_OPTIONS, OWNER_ME_TOKEN } from '../core/grid/owner-tokens';
import { VISIBILITY_FILTER_OPTIONS } from '../core/grid/visibility-options';

/** Column keys — kept as literals so searches find every usage. */
export const OWNER_KEY = 'owner';
export const TYPE_KEY = 'type';
export const VISIBILITY_KEY = 'visibility';
export const DATE_KEY = 'date';

/** One `{ value, label }` owner option (keyword token or a concrete visible user) — generated (G1). */
export type OwnerOption = import('../generated/contracts').OwnerOption;

/**
 * Builds the three filter columns. Owner options are the Me/Others/Everyone keyword tokens plus one entry
 * per visible user (empty in single-user mode); type options are the raw activity types (empty = all).
 */
export function buildStatsColumns(typeOptions: string[], ownerOptions: OwnerOption[]): GridColumn<unknown>[] {
	return [
		{
			key: OWNER_KEY,
			header: 'Owner',
			filter: { type: 'multiselect', options: [...OWNER_KEYWORD_OPTIONS, ...ownerOptions] as FilterOption[] },
		},
		{
			key: TYPE_KEY,
			header: 'Type',
			filter: { type: 'multiselect', options: typeOptions },
		},
		{
			key: VISIBILITY_KEY,
			header: 'Visibility',
			filter: { type: 'multiselect', options: VISIBILITY_FILTER_OPTIONS },
		},
		{
			key: DATE_KEY,
			header: 'Date',
			filter: { type: 'daterange' },
		},
	];
}

/**
 * The filter bar's "home" state: owner defaults to Me in multi-user mode (matches the Activities grid and the
 * server's default-to-Me), empty otherwise. Restored by the Reset button.
 */
export function defaultStatsFilters(multiUser: boolean): GridFilterValues {
	return multiUser ? { [OWNER_KEY]: { selected: [OWNER_ME_TOKEN] } } : {};
}
