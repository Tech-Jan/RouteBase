/**
 * map-dispatcher.ts — IMapAdapter dispatcher.
 *
 * Extracted from the old utils god-file (CB-01 dissolution). A single stateful unit:
 * one registered adapter, dispatch methods route to it (no-op if none).
 * Consumer code calls `mapDispatcher.showHighlightMarker(lat, lng)` and the
 * dispatcher routes to the registered MapLibre adapter.
 *
 * Duck-typed adapter interface:
 *   showHighlightMarker(lat, lng)
 *   hideHighlightMarker()
 *   destroyHighlightMarker()
 *   showSplitHighlight(startIndex, endIndex, gpsData)
 *   clearSplitHighlight()
 *   setupProximityHover(opts)
 *   updateTrackColors(colorMode, gpsData)
 *   resize()
 *   destroy()
 */
import type { ProximityHoverOptions, IMapAdapter, GpsPoint } from './core-types';
import { getGpsData } from './gps-data';

// Current track color mode ('none' | 'speed' | 'heartrate' | 'elevation' | 'cadence').
// Formerly window.currentColorMode — written on every updateTrackColors + by the Details
// color buttons / init; read by maplibre-terrain when re-adding the track after a style swap.
let _currentColorMode = 'none';
export function getCurrentColorMode(): string { return _currentColorMode; }
export function setCurrentColorMode(mode: string): void { _currentColorMode = mode; }

/** The map-adapter dispatcher: one registered IMapAdapter, methods route to it. */
export interface MapDispatcher {
	_adapter: IMapAdapter | null;
	register(adapter: IMapAdapter): void;
	unregister(): void;
	activeEngine(): string | null;
	showHighlightMarker(lat: number, lng: number): void;
	hideHighlightMarker(): void;
	destroyHighlightMarker(): void;
	showSplitHighlight(startIndex: number, endIndex: number): void;
	clearSplitHighlight(): void;
	setupProximityHover(opts: ProximityHoverOptions): void;
	updateTrackColors(colorMode: string, gpsArray?: GpsPoint[]): void;
}

export const mapDispatcher: MapDispatcher = {
	_adapter: null,

	/**
	 * Register a map adapter. Called by initMapLibreTerrain after map init.
	 */
	register(adapter: IMapAdapter): void {
		// Destroy previous adapter if replacing it (in-place style switch)
		if (mapDispatcher._adapter && mapDispatcher._adapter !== adapter) {
			try {
				mapDispatcher._adapter.destroy();
			} catch (_e) { /* teardown: previous adapter may already be destroyed */ }
		}
		mapDispatcher._adapter = adapter;
	},

	/**
	 * Unregister the current adapter. Called during map teardown.
	 */
	unregister(): void {
		mapDispatcher._adapter = null;
	},

	/** Which engine is currently active? */
	activeEngine(): string | null {
		return mapDispatcher._adapter?.engineName || null;
	},

	// Dispatcher methods — delegate to the active adapter, no-op if none registered

	showHighlightMarker(lat: number, lng: number): void {
		mapDispatcher._adapter?.showHighlightMarker(lat, lng);
	},

	hideHighlightMarker(): void {
		mapDispatcher._adapter?.hideHighlightMarker();
	},

	destroyHighlightMarker(): void {
		mapDispatcher._adapter?.destroyHighlightMarker();
	},

	showSplitHighlight(startIndex: number, endIndex: number): void {
		const data = getGpsData();
		if (startIndex < 0 || endIndex >= data.length || startIndex >= endIndex) return;
		mapDispatcher._adapter?.showSplitHighlight(startIndex, endIndex, data);
	},

	clearSplitHighlight(): void {
		mapDispatcher._adapter?.clearSplitHighlight();
	},

	setupProximityHover(opts: ProximityHoverOptions): void {
		mapDispatcher._adapter?.setupProximityHover(opts);
	},

	updateTrackColors(colorMode: string, gpsArray?: GpsPoint[]): void {
		const data = gpsArray || getGpsData();
		_currentColorMode = colorMode;
		mapDispatcher._adapter?.updateTrackColors(colorMode, data);
	},
};
