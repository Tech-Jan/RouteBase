/**
 * Activity Details — Splits & Segments Highlight Module
 *
 * Single source of truth for all split / segment highlighting on maps and charts.
 *
 * Registers the split-highlight API in details-shared (highlightSplitRange /
 * clearSplitHighlight / refreshSplitHighlights / highlightSplitForDistance), consumed by
 * details.ts + segments.ts, plus the MapLibre hover hook (setupMapLibreHoverTracking),
 * which details-init passes to initMapLibreTerrain as the setupHoverTracking hook.
 */

import { readDataNumber } from '../core/data-attrs';
import { must } from '../core/invariant';
import type { GpsPoint } from '../core/core-types';
import { findNearestPointIndex } from '../core/nearest-point';
import { getGpsData } from '../core/gps-data';
import { mapDispatcher } from '../core/map-dispatcher';
import { showSplitOverlay, clearSplitOverlay } from '../core/chart-overlay';
import { getSpeedChart, getElevationChart, updateHighlight, hideHighlight } from './activity-details';
import { getLockedSplit, setLockedSplit } from '../core/split-state';
import { getMaplibreMap } from '../core/map-state';
import { registerSplitHighlightApi, getOnChartClick, registerHoverTracking } from './details-shared';

// Currently highlighted distance range (splits/segments). Module-local — no
// external reader; refreshSplitHighlights re-derives chart overlays from it.
let _activeSplitRange: { startDist: number; endDist: number } | null = null;

export function setupSplitsInteraction(_gpsData: GpsPoint[]): void {
	// ----------------------------------------------------------------
	// 1. Split row hover / click / lock
	// ----------------------------------------------------------------

	const splitRows = document.querySelectorAll(".split-row");

	splitRows.forEach((row) => {
		row.addEventListener("mouseenter", function (this: HTMLElement) {
			if (getLockedSplit()) return;

			const startDist = readDataNumber(this, 'splitStart');
			const endDist = readDataNumber(this, 'splitEnd');

			highlightSplitRange(startDist, endDist);
			this.classList.add("table-primary");
		});

		row.addEventListener("mouseleave", function (this: HTMLElement) {
			if (getLockedSplit()) return;

			clearSplitHighlight();
			this.classList.remove("table-primary");
		});

		row.addEventListener("click", function (this: HTMLElement) {
			const startDist = readDataNumber(this, 'splitStart');
			const endDist = readDataNumber(this, 'splitEnd');

			const locked = getLockedSplit();
			if (locked && locked.start === startDist && locked.end === endDist) {
				// Unlock
				setLockedSplit(null);
				clearSplitHighlight();
				this.classList.remove("table-warning");
			} else {
				// Lock this split
				setLockedSplit({ start: startDist, end: endDist });

				document.querySelectorAll(".split-row").forEach((r) => {
					r.classList.remove("table-warning", "table-primary");
				});

				this.classList.add("table-warning");
				this.classList.remove("table-primary");

				highlightSplitRange(startDist, endDist);
			}
		});
	});

	// ----------------------------------------------------------------
	// 2. highlightSplitRange — distance-based (no API call)
	//
	//    Works for both splits and segments: given a start/end distance
	//    it finds GPS indices locally and highlights on map + charts.
	// ----------------------------------------------------------------

	// Cached last-highlighted range — skip redundant work if the same range is requested again.
	let _lastStartIndex = -1;
	let _lastEndIndex = -1;

	function highlightSplitRange(startDist: number, endDist: number): void {
		const data = getGpsData();
		if (data.length === 0) return;

		_activeSplitRange = { startDist, endDist };

		// Binary search for start/end indices instead of O(n) linear scan.
		// DC-01: clamp the end lookup to the last point — F2-rounded data-split-end
		// can exceed the final GPS distance, which would otherwise return -1 and kill
		// the last split's highlight entirely.
		const startIndex = _lowerBound(data, startDist);
		const endIndex = _lowerBound(data, endDist, true);

		if (startIndex === -1 || endIndex === -1 || startIndex >= endIndex) return;

		// Skip all expensive work if the same range is already highlighted
		if (startIndex === _lastStartIndex && endIndex === _lastEndIndex) return;
		_lastStartIndex = startIndex;
		_lastEndIndex = endIndex;

		// Map highlight (routed through the MapLibre adapter)
		mapDispatcher.showSplitHighlight(startIndex, endIndex);

		// Chart overlays
		showSplitOverlay(getElevationChart(), startIndex, endIndex);
		showSplitOverlay(getSpeedChart(), startIndex, endIndex);
	}

	// ----------------------------------------------------------------
	// 4. clearSplitHighlight
	// ----------------------------------------------------------------

	function clearSplitHighlight(): void {
		if (getLockedSplit()) return;

		_activeSplitRange = null;
		_lastHighlightedSplitIndex = -1;
		_lastStartIndex = -1;
		_lastEndIndex = -1;

		mapDispatcher.clearSplitHighlight();
		clearSplitOverlay(getElevationChart());
		clearSplitOverlay(getSpeedChart());

		document.querySelectorAll(".split-row").forEach((row) => {
			row.classList.remove("table-primary", "table-warning");
		});
	}

	// ----------------------------------------------------------------
	// 5. refreshSplitHighlights — called after chart zoom/pan
	// ----------------------------------------------------------------

	function refreshSplitHighlights(): void {
		if (!_activeSplitRange) return;

		const { startDist, endDist } = _activeSplitRange;
		const data = getGpsData();

		const startIndex = _lowerBound(data, startDist);
		const endIndex = _lowerBound(data, endDist, true); // DC-01: clamp end past track end

		if (startIndex !== -1 && endIndex !== -1) {
			showSplitOverlay(getElevationChart(), startIndex, endIndex);
			showSplitOverlay(getSpeedChart(), startIndex, endIndex);
		}
	}

	// ----------------------------------------------------------------
	// 6. highlightSplitForDistance — reverse lookup from map/chart hover
	//
	//    When the user hovers a point on the map or chart, this finds
	//    which split row (if any) contains that distance and adds a
	//    subtle highlight to the row.  Does NOT trigger the full split
	//    highlight (no map polyline / chart overlay) — just the table.
	//
	//    Optimised: uses pre-built sorted ranges + binary search to
	//    avoid iterating all rows on every mousemove. Caches the last
	//    highlighted row to skip unnecessary DOM updates.
	// ----------------------------------------------------------------

	// Pre-build sorted split ranges once for fast binary search
	const _splitRanges: { start: number; end: number; index: number }[] = [];
	splitRows.forEach((row, i) => {
		_splitRanges.push({
			start: readDataNumber(row as HTMLElement, 'splitStart'),
			end: readDataNumber(row as HTMLElement, 'splitEnd'),
			index: i,
		});
	});
	// Ranges are already sorted by distance from the DOM order, but sort
	// explicitly to be safe.
	_splitRanges.sort((a, b) => a.start - b.start);

	let _lastHighlightedSplitIndex = -1;

	function highlightSplitForDistance(distance: number | null): void {
		if (distance == null || getLockedSplit()) return;

		// Binary search for the split containing this distance
		let lo = 0,
			hi = _splitRanges.length - 1,
			found = -1;
		while (lo <= hi) {
			const mid = (lo + hi) >> 1;
			const r = must(_splitRanges[mid], 'mid within [lo,hi] (binary search)');
			if (distance < r.start) {
				hi = mid - 1;
			} else if (distance >= r.end) {
				lo = mid + 1;
			} else {
				found = mid;
				break;
			}
		}

		// Skip DOM update if the same row is already highlighted
		if (found === _lastHighlightedSplitIndex) return;

		// Un-highlight previous row
		if (_lastHighlightedSplitIndex >= 0 && _lastHighlightedSplitIndex < _splitRanges.length) {
			const prev = must(_splitRanges[_lastHighlightedSplitIndex], 'index bounds-checked above');
			splitRows[prev.index]?.classList.remove("table-primary");
		}

		_lastHighlightedSplitIndex = found;

		// Highlight new row
		if (found >= 0) {
			const cur = must(_splitRanges[found], 'found is a hit index');
			splitRows[cur.index]?.classList.add("table-primary");
		}
	}

	// ----------------------------------------------------------------
	// 7. 3D map hover tracking — delegates to proximity hover
	// ----------------------------------------------------------------

	function setupMapLibreHoverTracking(): void {
		const map = getMaplibreMap();
		if (!map || getGpsData().length === 0) return;

		mapDispatcher.setupProximityHover({
			onHighlight: (index: number, source: string) => {
				updateHighlight(index, source);
			},
			onHideHighlight: () => {
				hideHighlight();
			},
		});

		map.on("click", (e: maplibregl.MapMouseEvent) => {
			const onChartClick = getOnChartClick();
			if (!onChartClick) return;
			const dataSource = getGpsData();
			const index = findNearestPointIndex(e.lngLat.lat, e.lngLat.lng, dataSource);
			const hit = index >= 0 ? dataSource[index] : undefined;
			if (hit) {
				onChartClick(hit.distance, index);
			}
		});
	}

	// ----------------------------------------------------------------
	// 8. Binary search helper — find first index where data[i].distance >= target
	// ----------------------------------------------------------------

	// clampEnd: when target rounds past the last point's distance, return the last
	// index instead of the -1 sentinel (DC-01) — used only for end-of-range lookups.
	function _lowerBound(data: GpsPoint[], target: number, clampEnd = false): number {
		let lo = 0, hi = data.length - 1;
		while (lo < hi) {
			const mid = (lo + hi) >> 1;
			if (must(data[mid], 'mid within [lo,hi) (binary search)').distance < target) lo = mid + 1;
			else hi = mid;
		}
		const at = lo < data.length ? data[lo] : undefined;
		if (at === undefined || at.distance < target) {
			return clampEnd && data.length > 0 ? data.length - 1 : -1;
		}
		return lo;
	}

	// ----------------------------------------------------------------
	// 9. Register the highlight API + the MapLibre hover hook (both in
	//    details-shared; the hook is passed to initMapLibreTerrain by init).
	// ----------------------------------------------------------------

	registerSplitHighlightApi({
		highlightSplitRange,
		clearSplitHighlight,
		refreshSplitHighlights,
		highlightSplitForDistance,
	});
	registerHoverTracking(setupMapLibreHoverTracking);

}
