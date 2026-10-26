/**
 * Overview grid-filter bridge (leaf slot module).
 *
 * The Activities grid (activities-grid.ts) boots as its own standalone entry, so the
 * shape-filter paint UI can't import it directly. The grid registers its owner-filter +
 * column-filter accessors here at boot; the shape-filter modules call the call-throughs,
 * which return null until the grid registers. isGridFilterApiRegistered() reproduces the
 * old `typeof window.getActivitiesGridFilters === 'function'` presence check that hides the
 * "adopt filters" row on grid-less pages.
 */

// Type-only import (erased at runtime — no cycle with shape-filter, which imports this bridge).
import type { RuleDocumentDto } from './shape-filter/rule-document';
import type { GridColumnFilterWire } from './activities-filter-wire';

/** Owner filter wire shape (mirrors the server ColumnFilter { in, text }). */
export interface OwnerFilter { in: string[]; text: string }

/** Full column-filter map (date/name/type/distance/HR/…) — the grid's ColumnFilter wire subset (D26). */
export type GridFilters = Record<string, GridColumnFilterWire>;

export interface GridFilterApi {
	getOwnerFilter: () => OwnerFilter | null;
	setOwnerFilter: (filter: OwnerFilter | null) => void;
	getGridFilters: () => GridFilters | null;
	/** Map-drawn shape ("Merge table ↔ shapes"): restrict the grid to the tracks the shape matches; null clears. */
	setSpatialFilter: (rules: RuleDocumentDto | null) => void;
}

let _api: GridFilterApi | null = null;
const _registrationListeners: Array<() => void> = [];

/** Registered by activities-grid.ts once the grid has booted. */
export function registerGridFilterApi(api: GridFilterApi): void {
	_api = api;
	for (const cb of _registrationListeners.splice(0)) cb();
}

/**
 * Run `cb` once the grid has registered — immediately if it already has. Module init order isn't
 * guaranteed (the grid boots as its own entry), so consumers that reveal UI on the grid's presence
 * must subscribe rather than probe once at construction.
 */
export function onGridFilterApiRegistered(cb: () => void): void {
	if (_api) cb();
	else _registrationListeners.push(cb);
}

/** True once the grid has registered — the shape-filter "adopt filters" row keys off this. */
export function isGridFilterApiRegistered(): boolean { return _api !== null; }

export function getActivitiesOwnerFilter(): OwnerFilter | null { return _api?.getOwnerFilter() ?? null; }
export function setActivitiesOwnerFilter(filter: OwnerFilter | null): void { _api?.setOwnerFilter(filter); }
export function getActivitiesGridFilters(): GridFilters | null { return _api?.getGridFilters() ?? null; }
export function setActivitiesSpatialFilter(rules: RuleDocumentDto | null): void { _api?.setSpatialFilter(rules); }
