/**
 * chart-pan-state.ts — shared drag-pan flag.
 *
 * True while a chart pan/pinch/preview gesture is active; hover paths read it to
 * suppress map-highlight / crosshair updates mid-gesture. Module-scoped `let` with
 * accessors because `export let` can't be reassigned cross-module.
 */
let _isPanning = false;

export function isChartPanning(): boolean {
	return _isPanning;
}

export function setChartPanning(v: boolean): void {
	_isPanning = v;
}
