// Activity Details - Map and Charts Visualization
// ESM module: functions exported for direct import by activity-details-init.ts and others.
import type { ChartJsInstance, GpsPoint } from '../core/core-types';
import { must } from '../core/invariant';
import { ChartPanelManager } from '../core/chart-panel-manager';
import { createGpsLineChart, redecimateChartForRange, setChartSeriesMode } from '../core/chart-factory';
import { createSeriesModeControl, type SeriesModeControl } from '../core/series-mode-control';
import { readoutDecimalsForMode, SERIES_MODES, type SeriesMode } from '../core/chart-series-modes';
import { devicePrefKeys, getEnum } from '../core/device-prefs';
import { getAccountSettings } from '../core/account-settings';
import { GesturePreviewGroup } from '../core/chart-gesture-preview';
import { highlightChartIndex, clearChartHighlight, drawnValueAt } from '../core/chart-overlay';
import { getGpsData } from '../core/gps-data';
import { mapDispatcher } from '../core/map-dispatcher';
import { ChartReadoutBox, type ReadoutRow } from '../core/chart-readout-box';
import { getLockedSplit } from '../core/split-state';
import { isReplayActive } from '../core/map-state';
import {
	clearSplitHighlight,
	refreshSplitHighlights,
	highlightSplitForDistance,
	getOnChartClick,
} from './details-shared';

let elevationChart: ChartJsInstance | null = null;
let speedChart: ChartJsInstance | null = null;
/** The speed chart's series-mode button (see core/chart-series-modes). Rebuilt with the chart. */
let _speedModeControl: SeriesModeControl | null = null;
let heartRateChart: ChartJsInstance | null = null;

// Shared chart panel manager — wired once all charts exist
let _actPanelMgr: ChartPanelManager | null = null;

// requestAnimationFrame-based highlight batching — coalesces multiple
// mousemove events per frame into a single DOM/canvas update.
let _pendingHighlightIndex: number = -1;
let _pendingHighlightSource: string | null = null;
let _highlightRafId: number = 0;
let _lastHighlightedIndex: number = -1;

// Chart-instance accessors (ID-08): speed/elevation charts are read by activity-splits.ts
// for split-range overlays. Module-scoped state exported via accessor instead of window.*.
export function getSpeedChart(): ChartJsInstance | null { return speedChart; }
export function getElevationChart(): ChartJsInstance | null { return elevationChart; }

// One gesture preview group over the x-synced charts (speed/elevation/HR): pan/wheel/pinch
// draw a bitmap preview per frame on ALL of them, and the ONE real render per chart at
// gesture end applies the same range everywhere — this replaces the old per-frame
// syncChartZoom (a full re-render of every chart per pan frame / wheel tick, baseline F2).
// beforeApply: zoomScale fires no onZoom, so the LTTB window decimation must be re-run
// for the final range here, before the apply render. afterApply: the splits overlay
// stayed stale during the bitmap preview (like the axes) — refresh it once.
const _gesturePreview = new GesturePreviewGroup(
	() => [speedChart, elevationChart, heartRateChart],
	{
		beforeApply: (chart, range) => redecimateChartForRange(chart, range),
		afterApply: () => {
			refreshSplitHighlights();
		},
	},
);

/**
 * Reset all detail charts to full extent. Cancels any in-flight gesture first — a pending
 * wheel apply-debounce firing after the reset would re-zoom (reset wins). resetZoom restores
 * the axis but leaves the dataset holding the old window's decimation, so re-decimate to the
 * full range and render once.
 */
export function resetChartZoomAll(): void {
	_gesturePreview.cancel();
	for (const chart of [speedChart, elevationChart, heartRateChart]) {
		if (!chart) continue;
		chart.resetZoom();
		redecimateChartForRange(chart);
		chart.update("none");
	}
}

function chartMouseLeave(): void {
	if (!getLockedSplit()) {
		setTimeout(() => {
			if (!getLockedSplit()) {
				hideHighlight();
				clearSplitHighlight();
			}
		}, 100);
	}
}

// Initialize speed chart
export function initSpeedChart(): void {
	speedChart = createGpsLineChart({
		canvasId: "speedChart",
		chartName: "speed",
		dataField: "speed",
		label: "Speed (km/h)",
		borderColor: "#007bff",
		backgroundColor: "rgba(0, 123, 255, 0.1)",
		yFormat: "decimal",
		tooltipPrefix: "Speed",
		tooltipUnit: "km/h",
		fill: true,                         // filled blue area under the speed line
		backgroundField: "altitude",        // dark-grey elevation silhouette behind it
		backgroundFill: "rgba(120, 124, 140, 0.32)",
		onHighlight: (index: number) => {
			updateHighlight(index, "speed");
		},
		onMouseLeave: chartMouseLeave,
		onChartClick: (distance, index) => getOnChartClick()?.(distance, index),
		previewGroup: _gesturePreview,
		seriesMode: storedSpeedMode(),
	});

	// Built after the chart so the first paint already matches the restored preference. The control
	// calls back immediately with that mode, which is a no-op against the chart it was built with.
	_speedModeControl?.destroy();
	_speedModeControl = createSeriesModeControl({
		button: document.getElementById("speedChartModeToggle"),
		prefKey: "activitySpeed",
		onChange: (mode) => setChartSeriesMode(speedChart, mode),
	});
}

/** The stored view for the speed chart, read before the chart is built so it opens on it. */
function storedSpeedMode(): SeriesMode {
	const fallback = getAccountSettings().charts.defaultSpeedView as SeriesMode;
	return getEnum<SeriesMode>(devicePrefKeys.chartSeriesMode("activitySpeed"), SERIES_MODES,
		SERIES_MODES.includes(fallback) ? fallback : "smoothBin");
}

// Initialize elevation chart
export function initElevationChart(): void {
	elevationChart = createGpsLineChart({
		canvasId: "elevationChart",
		chartName: "elevation",
		dataField: "altitude",
		label: "Altitude (m)",
		borderColor: "#aab0c0",                       // thin light-grey line
		backgroundColor: "rgba(40, 167, 69, 0.1)",
		yFormat: "int",
		tooltipPrefix: "Altitude",
		tooltipUnit: "m",
		fill: true,                                    // same filled silhouette as the speed-chart background…
		fillColor: "rgba(165, 170, 185, 0.42)",        // …but a slightly brighter grey
		borderWidth: 1,
		onHighlight: (index: number) => {
			updateHighlight(index, "elevation");
		},
		onMouseLeave: chartMouseLeave,
		onChartClick: (distance, index) => getOnChartClick()?.(distance, index),
		previewGroup: _gesturePreview,
	});
}

// Initialize heart rate chart
export function initHeartRateChart(): void {
	const canvas = document.getElementById("heartRateChart");
	if (!canvas) return;
	heartRateChart = createGpsLineChart({
		canvasId: "heartRateChart",
		chartName: "heartrate",
		dataField: "heartRate",
		label: "Heart Rate (bpm)",
		borderColor: "#e83e8c",
		backgroundColor: "rgba(232, 62, 140, 0.1)",
		yFormat: "int",
		tooltipPrefix: "Heart Rate",
		tooltipUnit: "bpm",
		smoothingWindow: 5,
		onHighlight: (index: number) => {
			updateHighlight(index, "heartrate");
		},
		onMouseLeave: chartMouseLeave,
		onChartClick: (distance, index) => getOnChartClick()?.(distance, index),
		previewGroup: _gesturePreview,
	});
}

// ── Floating readout box ──────────────────────────────────────────────────
// One tight box per chart (speed / elevation / heart rate), floating above that
// chart's vertical crosshair line. Built with the shared ChartReadoutBox
// (core/chart-readout-box.ts); the metrics show one row per field.
type ActChartKey = "speed" | "elevation" | "heartrate";

const _actBoxes = new Map<ActChartKey, ChartReadoutBox>();

function _actChartForKey(key: ActChartKey): ChartJsInstance | null {
	return key === "speed" ? speedChart : key === "elevation" ? elevationChart : heartRateChart;
}

// Pick the chart the readout should sit above: the hovering chart if the hover came from one,
// else the top-most currently-visible panel (so map/replay hovers land on a visible chart).
function _actTargetKey(source: string | null): ActChartKey | null {
	if (source === "speed" || source === "elevation" || source === "heartrate") return source;
	const active = _actPanelMgr ? _actPanelMgr.activePanels() : null;
	for (const key of ["speed", "elevation", "heartrate"] as ActChartKey[]) {
		if (_actChartForKey(key) && (!active || active.indexOf(key) >= 0)) return key;
	}
	return null;
}

function _actBoxFor(key: ActChartKey, chart: ChartJsInstance): ChartReadoutBox | null {
	let box = _actBoxes.get(key);
	if (!box) {
		const host = chart.canvas.parentElement as HTMLElement | null; // .chart-container (position:relative)
		if (!host) return null;
		box = new ChartReadoutBox(host);
		// Touch ✕ dismiss: tear down the crosshair + map marker with the box.
		box.setOnClose(hideHighlight);
		_actBoxes.set(key, box);
	}
	return box;
}

function _hideOtherActBoxes(except: ActChartKey | null): void {
	_actBoxes.forEach((box, key) => { if (key !== except) box.hide(); });
}

// Build one ReadoutRow per metric for the GPS point (swatch colours match the line colours).
function _actReadoutRows(p: GpsPoint, shownSpeed: number | null): ReadoutRow[] {
	// `shownSpeed` is the value the speed LINE is drawing at this index, which is not p.speed unless
	// the chart is in Recorded mode. Reading the raw channel here put the box and the line several
	// km/h apart on a spiky descent — the number claimed to describe a line it did not come from.
	const speed = shownSpeed ?? p.speed ?? null;
	// A smoothed value is a mean over half a minute of riding — its tenths are arithmetic, not
	// measurement, so the readout drops them. Recorded keeps its decimal: that IS the stored sample.
	const dp = readoutDecimalsForMode(_speedModeControl?.mode ?? 'smoothBin');
	const rows: ReadoutRow[] = [
		{ color: "#6c757d", label: "Distance", cells: [{ num: p.distance != null ? p.distance.toFixed(2) : "--", unit: "km" }] },
		{ color: "#007bff", label: "Speed", cells: [{ num: speed != null && Number.isFinite(speed) ? speed.toFixed(dp) : "--", unit: "km/h" }] },
	];
	let paceNum = "--";
	if (speed != null && Number.isFinite(speed) && speed > 0.5) {
		const paceSec = Math.round((60 / speed) * 60);
		paceNum = Math.floor(paceSec / 60) + ":" + String(paceSec % 60).padStart(2, "0");
	}
	rows.push({ color: "#6f42c1", label: "Pace", cells: [{ num: paceNum, unit: "/km" }] });
	rows.push({ color: "#28a745", label: "Altitude", cells: [{ num: p.altitude != null ? p.altitude.toFixed(0) : "--", unit: "m" }] });
	if (p.heartRate != null && p.heartRate > 0)
		rows.push({ color: "#e83e8c", label: "Heart Rate", cells: [{ num: p.heartRate.toFixed(0), unit: "bpm" }] });
	if (p.cadence != null && p.cadence > 0)
		rows.push({ color: "#fd7e14", label: "Cadence", cells: [{ num: p.cadence.toFixed(0), unit: "rpm" }] });
	return rows;
}

// Crosshair pixel x for a GPS point — the details charts plot distance (km) on x (datasets are
// decimated {x,y} with no data.labels), so map the point's distance through the x scale, matching the
// overlay crosshair. Returns null when the point falls outside the visible plot (zoomed/panned away).
function _actCaretX(chart: ChartJsInstance, p: GpsPoint): number | null {
	const area = chart.chartArea;
	const xScale = chart.scales?.x;
	if (!xScale || area == null || p.distance == null) return null;
	const x = xScale.getPixelForValue(p.distance);
	if (x < area.left || x > area.right) return null;
	return x;
}

/**
 * Render the floating readout box for the GPS point at `index`, above the chart
 * that originated the hover (or the top visible chart for map/replay hovers).
 * Called synchronously from _executeHighlight (already inside a rAF).
 */
function _renderActReadout(index: number, source: string | null): void {
	// Use the same array the charts are built from (getGpsData) so the point's distance
	// lines up with the x-scale — `index` is an index into this array (matching the crosshair).
	const data = getGpsData();
	if (!data.length) return;
	const idx = Math.max(0, Math.min(index, data.length - 1));
	const p = must(data[idx], 'clamped index (non-empty guarded)');

	const key = _actTargetKey(source);
	const chart = key ? _actChartForKey(key) : null;
	if (!key || !chart || !chart.chartArea) { _hideOtherActBoxes(null); return; }

	const caretX = _actCaretX(chart, p);
	if (caretX == null) { _hideOtherActBoxes(null); return; }

	const box = _actBoxFor(key, chart);
	if (!box) return;
	box.show(p.time || "", _actReadoutRows(p, drawnValueAt(speedChart, idx)), caretX, chart.chartArea.top);
	_hideOtherActBoxes(key);
}

/** Hide every readout box (on mouse leave). */
function _hideActReadout(): void {
	_actBoxes.forEach((box) => box.hide());
}

/** Wire the ChartPanelManager for the details page. Called once all charts exist. */
export function initActPanelMgr(): void {
	const hasHR = !!heartRateChart;
	const defaults = ["speed", "elevation"];
	if (hasHR) defaults.push("heartrate");

	_actPanelMgr = new ChartPanelManager({
		wrapperId: "actChartPanelWrap",
		xAxisTabs: [],
		defaultTabs: defaults,
		chartMap: {
			speed: () => speedChart,
			elevation: () => elevationChart,
			heartrate: () => heartRateChart,
		},
	});
	_actPanelMgr.initButtons();

	// The floating readout box appears only on hover (like Statistics/Segments) — nothing to seed.
}

// ── Core highlight function (rAF-throttled) ──────────────────────────
// All sources (map hover, chart hover, split click) funnel through
// here.  At most one actual DOM/canvas update runs per frame.

export function updateHighlight(index: number, source: string): void {
	// Skip all highlight/marker logic during replay
	if (isReplayActive()) return;
	const dataSource = getGpsData();

	if (index < 0 || index >= dataSource.length) return;

	// Skip if same index as already highlighted
	if (index === _lastHighlightedIndex) return;

	// Schedule the actual work for the next animation frame
	_pendingHighlightIndex = index;
	_pendingHighlightSource = source;

	if (!_highlightRafId) {
		_highlightRafId = requestAnimationFrame(_executeHighlight);
	}
}

function _executeHighlight(): void {
	_highlightRafId = 0;

	const index = _pendingHighlightIndex;
	const source = _pendingHighlightSource;
	if (index < 0) return;

	const dataSource = getGpsData();
	if (index >= dataSource.length) return;

	_lastHighlightedIndex = index;
	const point = dataSource[index];
	if (point === undefined) return; // negative/hole index — nothing to highlight

	// Update map marker on whichever engine is active (single call)
	mapDispatcher.showHighlightMarker(point.lat, point.lng);

	// Render the floating readout box synchronously — we are already inside a rAF
	// so the DOM write lands in the same frame as the crosshair (no 16ms lag).
	_renderActReadout(index, source);

	// Update charts via shared helpers — skip the chart that originated
	// the event since Chart.js already updated its own active elements.
	// Also skip charts whose panel is currently hidden (avoids expensive
	// canvas redraws for off-screen charts).
	const activePanels = _actPanelMgr ? _actPanelMgr.activePanels() : null;

	if (source !== "elevation" && (!activePanels || activePanels.indexOf("elevation") >= 0)) {
		highlightChartIndex(elevationChart, index, true);
	}
	if (source !== "speed" && (!activePanels || activePanels.indexOf("speed") >= 0)) {
		highlightChartIndex(speedChart, index, true);
	}
	if (source !== "heartrate" && (!activePanels || activePanels.indexOf("heartrate") >= 0)) {
		highlightChartIndex(heartRateChart, index, true);
	}

	highlightSplitForDistance(point.distance);
}

// Hide highlight
export function hideHighlight(): void {
	// Skip all highlight/marker logic during replay
	if (isReplayActive()) return;

	// Cancel any pending highlight update
	if (_highlightRafId) {
		cancelAnimationFrame(_highlightRafId);
		_highlightRafId = 0;
	}
	_pendingHighlightIndex = -1;
	_lastHighlightedIndex = -1;

	// Hide the floating readout box
	_hideActReadout();

	// Hide map marker on whichever engine is active (single call)
	mapDispatcher.hideHighlightMarker();

	// Clear charts via shared helpers
	clearChartHighlight(elevationChart);
	clearChartHighlight(speedChart);
	clearChartHighlight(heartRateChart);
}
