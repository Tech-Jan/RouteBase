/**
 * details-shared.ts — Details-bundle shared slots (leaf; imports no activity modules).
 *
 * Breaks the splits↔details / segments↔details import cycles that previously forced these
 * onto window. activity-splits registers the split-highlight API; activity-segments sets the
 * transient chart-click hook; details/segments/splits call the call-throughs. Call-throughs
 * no-op / return null until a provider registers — identical to the old window-guarded reads.
 */

export interface SplitHighlightApi {
	highlightSplitRange: (startDist: number, endDist: number) => void;
	clearSplitHighlight: () => void;
	refreshSplitHighlights: () => void;
	highlightSplitForDistance: (distance: number | null) => void;
}

let _splitHighlightApi: SplitHighlightApi | null = null;

/** Registered by activity-splits.ts once its highlight handlers are wired. */
export function registerSplitHighlightApi(api: SplitHighlightApi | null): void { _splitHighlightApi = api; }

export function highlightSplitRange(startDist: number, endDist: number): void { _splitHighlightApi?.highlightSplitRange(startDist, endDist); }
export function clearSplitHighlight(): void { _splitHighlightApi?.clearSplitHighlight(); }
export function refreshSplitHighlights(): void { _splitHighlightApi?.refreshSplitHighlights(); }
export function highlightSplitForDistance(distance: number | null): void { _splitHighlightApi?.highlightSplitForDistance(distance); }

// Transient chart-click hook — set while the segment-create picker is armed (segments.ts),
// read on chart click (chart-factory via the Details chart config) and on map click (splits.ts).
export type ChartClickHandler = (distance: number, index: number) => void;

let _onChartClick: ChartClickHandler | null = null;
export function setOnChartClick(fn: ChartClickHandler | null): void { _onChartClick = fn; }
export function getOnChartClick(): ChartClickHandler | null { return _onChartClick; }

// Page-provided MapLibre load hooks (passed to initMapLibreTerrain by details-init). These are
// closures over per-setup DOM state, registered by activity-splits / activity-replay-ui at boot,
// read at map-load time — so init-order vs the map "load" event is what it was under window.
type VoidFn = () => void;

let _hoverTracking: VoidFn | null = null;
export function registerHoverTracking(fn: VoidFn | null): void { _hoverTracking = fn; }
export function getHoverTracking(): VoidFn | null { return _hoverTracking; }

let _replayButtonVisibility: VoidFn | null = null;
export function registerReplayButtonVisibility(fn: VoidFn | null): void { _replayButtonVisibility = fn; }
export function getReplayButtonVisibility(): VoidFn | null { return _replayButtonVisibility; }

// Details "action bar" (Photos / Create Segment) — exclusive accordion. Each feature module registers a
// close callback under its tab id; opening one panel closes the other, so the two modules stay decoupled
// (neither imports the other). Kept here (leaf module) alongside the other shared slots.
export type ActionPanelId = "photos" | "segment";

const _actionPanelClosers = new Map<ActionPanelId, VoidFn>();
export function registerActionPanel(id: ActionPanelId, close: VoidFn): void { _actionPanelClosers.set(id, close); }

/** Close every registered action panel except the one being opened. */
export function closeOtherActionPanels(except: ActionPanelId): void {
	for (const [id, close] of _actionPanelClosers) if (id !== except) close();
}

/** Close every registered action panel (RP-06: called when replay starts so an armed
 *  photo-placement session can't commit a pin at the flythrough location). */
export function closeAllActionPanels(): void {
	for (const close of _actionPanelClosers.values()) close();
}
