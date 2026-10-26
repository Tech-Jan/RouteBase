/**
 * Overview editor-pick bridge (leaf slot module).
 *
 * The Activities grid (activities-grid.ts) boots as its own standalone entry and owns the checkbox
 * selection used to pick tracks for the track editor. The track-edit front-door bar (entry-picker.ts)
 * boots as a different entry and can't import the grid — so the grid registers a publisher here at boot
 * and publishes the picked-id array on every selection change; the bar reads/observes/clears through the
 * call-throughs (null until the grid registers). One-directional: overview never imports track-edit.
 *
 * Deliberately DISTINCT from the highlight bridge (getOverviewSelectionBridge): the bar must count the
 * EXPLICIT pick set, not the effective `pick ?? paint ?? grid` highlight — which also moves on filter/paint.
 */

/** The imperative bit the bridge can't derive from published ids — a global clear routed back to the grid. */
export interface PickPublisher {
	clearPicks(): void;
}

export interface OverviewPickBridge {
	/** Current picked activity ids (last published; [] before the grid registers or after a clear). */
	getPickedIds(): number[];
	/** Fires immediately with the current picks, then after every publishPickedIds. */
	onPickedChanged(cb: (ids: number[]) => void): void;
	/** Clear the whole pick set (routed to the grid). No-op until the grid registers. */
	clearPicks(): void;
}

let _publisher: PickPublisher | null = null;
let _lastIds: number[] = [];
const _subs: Array<(ids: number[]) => void> = [];

/** Registered by activities-grid.ts once the grid has booted in editor-pick mode. */
export function registerPickPublisher(p: PickPublisher): void { _publisher = p; }

/** Published by activities-grid.ts on every selection change — the id-array snapshot the bar reads. */
export function publishPickedIds(ids: number[]): void {
	_lastIds = ids;
	for (const cb of _subs) {
		try { cb(ids); } catch (e: unknown) { console.warn('[overview] pick subscriber failed:', e); }
	}
}

export function getOverviewPickBridge(): OverviewPickBridge {
	return {
		getPickedIds: () => _lastIds,
		onPickedChanged: (cb) => { _subs.push(cb); cb(_lastIds); }, // immediate fire with the current value
		clearPicks: () => _publisher?.clearPicks(),
	};
}
