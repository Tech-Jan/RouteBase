/**
 * Dashboard feed grid contracts — the sort/filter controls grid DTO + bootstrap.
 *
 * Formerly ambient globals (GodFileSplit).
 */

import type { DashboardGridRow } from '../generated/contracts';

/** One row of the dashboard feed's sort/filter controls grid (Pages/Index).
 *  Generated from C# DashboardGridRow (island component schema, D27). */
export type { DashboardGridRow };

/** Bootstrap for the dashboard feed controls grid — all visible activity rows + filter options. */
export interface DashboardGridBootstrap {
	rows: DashboardGridRow[];
	multiUser: boolean;
	typeOptions: string[];
	ownerOptions: string[];
}
