/**
 * Segment Statistics — Cross-hover coordination
 *
 * Bridges map ↔ chart hover interactions. Owns the highlight marker logic
 * and the findClosestPointByX utility.
 *
 * Depends on: ctx (shared context from segment-statistics.ts)
 */
import type { ChartJsInstance, ChartEventLike } from '../core/core-types';
import type { EffortPerformanceData, EffortDataPoint } from './segment-stats-effort-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { ctx } from './segment-statistics';
import { getMaplibreMap } from '../core/map-state';
import { drawVerticalCrosshair, clearVerticalCrosshair } from '../core/chart-overlay';
import { reportChartError } from '../core/chart-error-log';
import { must } from '../core/invariant';

const HOVER_MARKER_SOURCE = "seg-hover-markers";

// Highlight color echoes the replay marker emphasis (golden ring + glow).
const HIGHLIGHT_RING = "#ffe066";

interface HoverMarkerFeature {
	type: "Feature";
	properties: { color: string; highlighted: boolean };
	geometry: { type: "Point"; coordinates: [number, number] };
}

// ====================================================================
// UTILITY — binary search for closest x-value in sorted points
//
// Pure (no ctx / no closure state), so it is a module-level export that the
// bridge assigns onto ctx.findClosestPointByX inside initSegmentStatsHover.
// ====================================================================

export function findClosestPointByX(points: EffortDataPoint[], targetX: number, xField: string): number {
	// In percent mode, prefer segmentProgress (true position along the segment, 0–1
	// and monotonic non-decreasing) so the marker lands at the matching physical
	// location across efforts. targetX is 0–100, so compare against segmentProgress*100.
	const usePercentProgress =
		xField === "percent" && points.length > 0 && points[0]?.segmentProgress != null;

	// ?? NaN via the rebind: the search keeps i in-bounds — dead, and NaN comparisons behave as
	// the undefined ones did.
	const valueAt = (i: number): number => {
		const p = points[i];
		if (p === undefined) return NaN;
		if (usePercentProgress) return (p.segmentProgress ?? 0) * 100;
		if (xField === "time") return p.time || 0;
		return p.distance || 0;
	};

	// Legacy fallback: no segmentProgress → convert percent to the effort's own
	// distance so the distance-based search lands on the matching location.
	if (xField === "percent" && !usePercentProgress) {
		const total = points.length ? must(points.at(-1), 'non-empty (guarded)').distance : 0;
		targetX = (targetX / 100) * total;
	}

	let lo = 0;
	let hi = points.length - 1;

	// Binary search for the insertion point
	while (lo < hi) {
		const mid = (lo + hi) >> 1;
		if (valueAt(mid) < targetX) {
			lo = mid + 1;
		} else {
			hi = mid;
		}
	}

	// lo is now the first index where valueAt(lo) >= targetX
	// Check lo and lo-1 to find which is actually closer
	if (lo === 0) return 0;
	if (lo >= points.length) return points.length - 1;

	const diffLo = Math.abs(valueAt(lo) - targetX);
	const diffPrev = Math.abs(valueAt(lo - 1) - targetX);
	return diffPrev <= diffLo ? lo - 1 : lo;
}

/**
 * Wire the map↔chart cross-hover bridge onto the shared ctx. Hoisted out of the module IIFE (was a
 * top-level body that ran on import) so the bridge is reachable from a test with a synthetic ctx.
 * Behaviour and import-time semantics are unchanged: the top-level call at the bottom passes the shared
 * ctx exactly as before, and every marker/crosshair seam it assigns targets that same singleton.
 */
export function initSegmentStatsHover(ctx: SegmentStatisticsContext): void {

	// ====================================================================
	// MAP HIGHLIGHT MARKERS — one GeoJSON source + circle layers
	//
	// One feature per hovered effort, rebuilt each hover tick and pushed with a
	// single setData(). GPU-rendered circles replace the previous per-effort DOM
	// markers: with 3D terrain enabled, every DOM Marker update ran a synchronous
	// gl.readPixels occlusion test (terrain.depthAtPoint) — hundreds of GPU
	// pipeline stalls per hover tick with large effort sets. Same pattern as
	// segment-stats-replay-markers.ts.
	// ====================================================================

	let _hoverFeatures: HoverMarkerFeature[] = [];
	let _flushQueued = false;
	let _reflushPending = false;
	// SH-04: the map object is replaced on bfcache restore / basemap switch. Key the pending-reflush
	// guard to the instance it was registered against so a freshly-swapped, still-loading map always
	// gets its own once('idle') registration instead of being blocked by a stale flag from the dead map.
	let _reflushPendingMap: maplibregl.Map | null = null;

	// updateMapMarkersForX calls showMapHighlight once per visible effort after a
	// hideAllMapMarkers — a microtask coalesces the whole tick into one setData().
	function _queueMarkerFlush(): void {
		if (_flushQueued) return;
		_flushQueued = true;
		queueMicrotask(() => {
			_flushQueued = false;
			_flushMarkers();
		});
	}

	function _flushMarkers(): void {
		const ml = getMaplibreMap();
		if (!ml) return;
		try {
			// Style still loading (initial page load or a basemap switch): addSource/addLayer
			// would throw, the catch below would swallow it, and the markers stayed invisible
			// until some later interaction flushed again. Re-flush once the map settles.
			if (typeof ml.isStyleLoaded === "function" && !ml.isStyleLoaded()) {
				if (!_reflushPending || _reflushPendingMap !== ml) {
					_reflushPending = true;
					_reflushPendingMap = ml;
					ml.once("idle", () => {
						_reflushPending = false;
						_reflushPendingMap = null;
						_flushMarkers();
					});
				}
				return;
			}
			let src = ml.getSource(HOVER_MARKER_SOURCE) as maplibregl.GeoJSONSource | undefined;
			if (!src) {
				// Nothing to draw and no source yet → don't add layers just to show nothing.
				// Lazy re-adding also self-heals after basemap style switches (which wipe sources).
				if (_hoverFeatures.length === 0) return;
				_addMarkerLayers(ml);
				src = ml.getSource(HOVER_MARKER_SOURCE) as maplibregl.GeoJSONSource | undefined;
			}
			src?.setData({ type: "FeatureCollection", features: _hoverFeatures } as GeoJSON.GeoJSON);
		} catch (_e) { /* style may be mid-switch; the next hover tick re-adds */ }
	}

	function _addMarkerLayers(ml: maplibregl.Map): void {
		ml.addSource(HOVER_MARKER_SOURCE, {
			type: "geojson",
			data: { type: "FeatureCollection", features: [] },
		});
		// Golden glow behind the emphasised marker (replaces the old box-shadow).
		ml.addLayer({
			id: "seg-hover-markers-halo",
			type: "circle",
			source: HOVER_MARKER_SOURCE,
			paint: {
				"circle-radius": ["case", ["get", "highlighted"], 20, 0],
				"circle-color": HIGHLIGHT_RING,
				"circle-blur": 0.6,
				"circle-opacity": ["case", ["get", "highlighted"], 0.55, 0],
			},
		});
		// Marker body — mirrors the old DOM marker: colour fill + white ring; the
		// highlighted effort renders larger with a golden ring, sorted on top.
		ml.addLayer({
			id: "seg-hover-markers-fill",
			type: "circle",
			source: HOVER_MARKER_SOURCE,
			layout: { "circle-sort-key": ["case", ["get", "highlighted"], 1, 0] },
			paint: {
				"circle-radius": ["case", ["get", "highlighted"], 12, 7],
				"circle-color": ["get", "color"],
				"circle-stroke-width": 3,
				"circle-stroke-color": ["case", ["get", "highlighted"], HIGHLIGHT_RING, "#ffffff"],
			},
		});
	}

	ctx.showMapHighlight = (
		_effortIndex: number,
		lat: number,
		lng: number,
		color: string,
		highlighted = false,
	): void => {
		_hoverFeatures.push({
			type: "Feature",
			properties: { color, highlighted },
			geometry: { type: "Point", coordinates: [lng, lat] },
		});
		_queueMarkerFlush();
	};

	/**
	 * Clear all highlight markers. Called on every hover frame before showing the
	 * new set — the cleared list and the subsequent showMapHighlight pushes land in
	 * the same coalesced setData().
	 */
	function hideAllMapMarkers(): void {
		_hoverFeatures = [];
		_queueMarkerFlush();
	}

	/**
	 * Clear markers immediately (chart leave, map engine switches, clearEfforts).
	 * Synchronous flush: the map instance may be about to be torn down, so don't
	 * leave the clear sitting in a microtask.
	 */
	function destroyAllMapMarkers(): void {
		_hoverFeatures = [];
		_flushMarkers();
	}

	// Public API — same seams as the previous DOM-marker implementation.
	ctx.hideMapHighlight = destroyAllMapMarkers;

	// Lightweight per-frame hide — kept as a separate seam for map-hover callers.
	ctx._hideMapMarkers = hideAllMapMarkers;

	// Map→chart hover (segment-stats-map-hover.ts): one marker at the corresponding on-segment point.
	// MH-03: this seam was declared but never assigned, so the map-hover position marker was dead.
	// Removal stays symmetric: map-hover clears via ctx.onChartLeave (→ hideMapHighlight) on miss/leave.
	ctx.showSegmentPositionMarker = (lat: number, lng: number): void => {
		_hoverFeatures = [];
		_hoverFeatures.push({
			type: "Feature",
			properties: { color: "#0e84ff", highlighted: false },
			geometry: { type: "Point", coordinates: [lng, lat] },
		});
		_queueMarkerFlush();
	};

	// Pure point-resolution search (see findClosestPointByX above). Assigned onto ctx so existing
	// lazy readers (updateMapMarkersForX at the `?.()` call) keep working; the standalone export is
	// what the tests drive directly.
	ctx.findClosestPointByX = findClosestPointByX;

	// ====================================================================
	// SHARED: update map markers for all visible efforts at a given x
	// ====================================================================

	function updateMapMarkersForX(
		hoveredX: number | undefined,
		xField: string,
		sourceChart: ChartJsInstance | null,
		activeEffortIndex = -1,
	): void {
		if (!ctx.performanceData || hoveredX == null) return;
		hideAllMapMarkers();
		ctx.performanceData.forEach((effort: EffortPerformanceData, i: number) => {
			if (!ctx.effortVisibility.has(i) || !effort.points || effort.points.length === 0)
				return;
			// Normal mode: datasetIndex==effortIndex, so a hidden dataset means a hidden effort. In
			// aggregate mode datasets are band/median/highlighted (NOT effort-indexed) — visibility is
			// governed by ctx.effortVisibility (checked above), so skip this per-dataset hidden test.
			if (!ctx.aggregateMode && sourceChart && sourceChart.data.datasets[i] && sourceChart.data.datasets[i].hidden)
				return;
			const bestIdx = ctx.findClosestPointByX?.(effort.points, hoveredX, xField) ?? 0;
			const pt = effort.points[bestIdx];
			if (pt && pt.lat != null && pt.lng != null) {
				ctx.showMapHighlight?.(
					i,
					pt.lat,
					pt.lng,
					must(ctx.effortColors[i % ctx.effortColors.length], 'in-bounds modulo of non-empty palette'),
					i === activeEffortIndex,
				);
			}
		});
	}

	// ====================================================================
	// SHARED: set active element on a chart with same-element skip guard
	// ====================================================================

	// setActiveElements deactivates the PREVIOUSLY-active elements (Chart.js removeHoverStyle). If a
	// dataset rebuild (effort highlight / aggregate↔individual toggle / delta swap) happened since the
	// active element was set, its meta.data[index] is now undefined and Chart.js throws while styling
	// it. Guard both entry points (ID-16): swallow that stale-element throw — the next full render
	// reconciles the active state — so a hover-leave can't spam the console with an Uncaught TypeError.
	// Guarded set — swallows the ID-16 stale-element throw, does NOT draw(). Returns true iff it changed
	// anything (skip guard + a real update), so callers can decide whether to draw() (SH-01). The
	// rAF-driven map→chart hover deliberately skips draw() (hover.mode null); the chart→map path draws.
	function trySetActiveElements(chart: ChartJsInstance | null, dsIndex: number, pointIndex: number): boolean {
		if (!chart) return false;
		// Skip if already highlighting the same element
		const current = chart.getActiveElements();
		const only = current.length === 1 ? current[0] : undefined;
		if (only && only.datasetIndex === dsIndex && only.index === pointIndex)
			return false;
		try {
			chart.setActiveElements([{ datasetIndex: dsIndex, index: pointIndex }]);
			chart.tooltip?.setActiveElements([{ datasetIndex: dsIndex, index: pointIndex }], { x: 0, y: 0 });
			return true;
		} catch (e) { // ID-16 known race: element went stale between set and a rebuild — reconciled next render
			reportChartError("segment-stats-hover:staleActiveElement(ID-16)", e); return false; }
	}

	function setChartActive(chart: ChartJsInstance | null, dsIndex: number, pointIndex: number): void {
		// draw() triggers verticalLinePlugin.afterDatasetsDraw on the cross-synced chart
		// (the chart that didn't originate the hover event and so wasn't redrawn by Chart.js).
		if (chart && trySetActiveElements(chart, dsIndex, pointIndex)) chart.draw();
	}

	function clearChartActive(chart: ChartJsInstance | null): void {
		if (!chart) return;
		if (chart.getActiveElements().length === 0) return;
		try {
			chart.setActiveElements([]);
			chart.tooltip?.setActiveElements([], { x: 0, y: 0 });
			chart.draw();
		} catch (e) { // ID-16 known race (see trySetActiveElements)
			reportChartError("segment-stats-hover:staleActiveElement(ID-16)", e); }
	}

	// SH-01: expose the guarded helpers so the map→chart hover (segment-stats-map-hover.ts) and the
	// progress chart's onHover (segment-stats-charts-progress.ts) consume one ID-16-guarded shape
	// instead of each re-implementing the unguarded setActiveElements pattern.
	ctx._trySetChartActive = trySetActiveElements;
	ctx._setChartActive = setChartActive;
	ctx._clearChartActive = clearChartActive;

	// ====================================================================
	// CHART HOVER → MAP + OTHER CHARTS
	// ====================================================================

	ctx.onChartHover = (chart: ChartJsInstance, event: ChartEventLike): void => {
		// Normalize: if a native MouseEvent arrives, convert to a ChartEvent-shaped object
		// so Chart.js can compute relative canvas coordinates.
		let chartEvent: import("chart.js").ChartEvent;
		if (event instanceof MouseEvent) {
			const rect = chart.canvas.getBoundingClientRect();
			chartEvent = {
				native: event,
				x: event.clientX - rect.left,
				y: event.clientY - rect.top,
				type: event.type as import("chart.js").ChartEvent["type"],
			};
		} else {
			chartEvent = event;
		}

		// All the synced x-axis line charts except the one the cursor is over.
		const otherCharts = [ctx.performanceChart, ctx.elevationChart, ctx.effortChart].filter(
			(c): c is ChartJsInstance => c != null && c !== chart,
		);
		const elements = chart.getElementsAtEventForMode(
			chartEvent,
			"nearest",
			{ axis: "x", intersect: false },
			false,
		);
		if (elements.length === 0) {
			hideAllMapMarkers();
			clearVerticalCrosshair(chart);
			otherCharts.forEach((oc) => clearVerticalCrosshair(oc));
			return;
		}

		const el = elements[0];
		const dsIndex = el.datasetIndex;
		const pointIndex = el.index;

		// Use the cursor's exact x-value (shared across all efforts) as the reference x, not the
		// nearest point's snapped x. The snapped value depends on which effort dsIndex landed on
		// (arbitrary in % Complete mode), which made marker positions wobble. Falls back to the
		// snapped point x if the scale can't resolve the pixel.
		const dsPoints = (chart.data.datasets[dsIndex]?.data ?? []) as Array<{ x?: number } | null>;
		const hoveredX = chart.scales.x?.getValueForPixel(chartEvent.x ?? 0) ?? dsPoints[pointIndex]?.x;

		// Crosshair: draw a vertical line at the hovered x on the overlay canvas of the hovered chart
		// AND every synced sibling. This replaces setChartActive()+chart.draw(), which repainted the
		// whole Chart.js canvas (every effort line) on each synced chart per hover — the dominant cost
		// with many efforts. drawing by x-value keeps the line at the same physical position across
		// charts even though each effort is decimated independently.
		if (hoveredX != null) {
			drawVerticalCrosshair(chart, hoveredX);
			for (const oc of otherCharts) {
				drawVerticalCrosshair(oc, hoveredX);
			}
		}

		if (ctx.progressChart && dsIndex < ctx.progressChart.data.datasets.length) {
			setChartActive(ctx.progressChart, dsIndex, 0);
		}

		// Remember the scrubbed x so a later box-row hover can refresh markers without a chart event.
		ctx.lastHoverX = hoveredX;
		// Emphasis comes from the readout box only: the pinned effort, else the hovered row; -1 = none.
		const activeEffort = ctx.selectedEffortIndex ?? ctx.hoveredEffortIndex ?? -1;
		updateMapMarkersForX(hoveredX, ctx.currentXAxis, chart, activeEffort);
	};

	// Re-emphasise the map markers from ctx state at the last scrubbed x — called when a readout-box
	// row is hovered/clicked while the cursor sits on the (sticky) box, with no fresh chart event.
	ctx.refreshHighlight = (): void => {
		const activeEffort = ctx.selectedEffortIndex ?? ctx.hoveredEffortIndex ?? -1;
		// null source chart: visibility is governed by ctx.effortVisibility (no per-chart hidden check).
		updateMapMarkersForX(ctx.lastHoverX, ctx.currentXAxis, null, activeEffort);
	};

	ctx.onChartLeave = (): void => {
		destroyAllMapMarkers();
		// Main comparison charts use the overlay crosshair; the progress chart still uses the
		// Chart.js active-element crosshair (few points, cheap to redraw).
		[ctx.performanceChart, ctx.elevationChart, ctx.effortChart].forEach((c) =>
			clearVerticalCrosshair(c),
		);
		clearChartActive(ctx.progressChart);
	};
}

if (!ctx) {
	throw new Error("segment-stats-hover: no ctx");
}
initSegmentStatsHover(ctx);
