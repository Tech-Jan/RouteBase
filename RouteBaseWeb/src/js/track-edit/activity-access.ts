/**
 * activity-access.ts — the seam the track editor uses to reach the activity-details page.
 *
 * Dependency inversion: the abstraction lives with the CONSUMER (track-edit), not the provider. The
 * activity feature implements `ActivityDetailsAccess` and registers it at page init
 * (activity-details-init.ts); the editor's preview/toolbar/controller consume the call-throughs below.
 * This breaks the old track-edit -> activity import cycle — track-edit no longer imports activity
 * internals (chart lifecycle in activity-details.ts, action-panel slots in details-shared.ts).
 *
 * Call-throughs no-op / return null until a provider registers — identical to the pattern in
 * details-shared.ts, and safe because the editor is only reachable from a details page that has already
 * run initializeActivityDetails() (which registers).
 */

import type { ChartJsInstance } from '../core/core-types';

/** Chart-click hook shape — mirrors details-shared.ts's ChartClickHandler (kept local so the seam
 *  carries no type import back into activity/). */
export type ChartClickHandler = (distance: number, index: number) => void;

/** Everything the editor needs from the activity-details page. Implemented + registered by the activity
 *  feature (activity-details-init.ts), consumed here. */
export interface ActivityDetailsAccess {
	initSpeedChart(): void;
	initElevationChart(): void;
	initHeartRateChart(): void;
	getSpeedChart(): ChartJsInstance | null;
	getElevationChart(): ChartJsInstance | null;
	closeAllActionPanels(): void;
	setOnChartClick(fn: ChartClickHandler | null): void;
}

let _access: ActivityDetailsAccess | null = null;

/** Registered by activity-details-init.ts once the page's chart + action-panel slots are wired. */
export function registerActivityDetailsAccess(access: ActivityDetailsAccess | null): void { _access = access; }

export function initSpeedChart(): void { _access?.initSpeedChart(); }
export function initElevationChart(): void { _access?.initElevationChart(); }
export function initHeartRateChart(): void { _access?.initHeartRateChart(); }
export function getSpeedChart(): ChartJsInstance | null { return _access?.getSpeedChart() ?? null; }
export function getElevationChart(): ChartJsInstance | null { return _access?.getElevationChart() ?? null; }
export function closeAllActionPanels(): void { _access?.closeAllActionPanels(); }
export function setOnChartClick(fn: ChartClickHandler | null): void { _access?.setOnChartClick(fn); }
