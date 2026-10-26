/**
 * chart-factory.ts — Chart.js line-chart factory for RouteBase.
 *
 * Builds a standard RouteBase line chart (LTTB decimation, X-only zoom/pan/pinch,
 * overlay-canvas highlight, optional greyscale background series, direct DOM
 * hover/click listeners that bypass Chart.js' render cycle). Composed from the
 * chart-* modules (decimation, zoom-pan, line-select, overlay, pan-state).
 *
 * Zoom/pan gestures: pages with x-synced chart groups pass a shared GesturePreviewGroup
 * (`previewGroup` opt) — the DEFAULT for new multi-chart pages, not an optional extra.
 * Gestures then draw a bitmap preview per frame and render for real once at gesture end
 * (see chart-gesture-preview.ts); without it every pan frame / wheel tick is a full
 * Chart.js re-render per chart (the measured FrontendBaseline F2 lag class).
 *
 * Chart.js + chartjs-plugin-zoom stay CDN globals — no runtime import.
 *
 * SCOPE: this is the GPS-distance LINE chart (Details' speed/elevation/HR) and the ONLY
 * createGpsLineChart(). It sources points from getGpsData() and builds series from a dataField key, so
 * it can't render the Statistics/Segment bar/scatter/time-multiline/density charts — those keep
 * their own builders. What is genuinely shared is single-sourced in sibling core/ modules that
 * every chart imports directly: finalizeChart/xZoomConfig (chart-zoom-pan), strokeCrosshairLine
 * (chart-overlay), setChartEmptyState (chart-empty-state), afterLayout (chart-panel-manager),
 * reportChartError (chart-error-log), robust bounds (chart-robust-scale). Fix a cross-cutting
 * concern once, there — not per chart. (See core/core.ts for the kernel index.)
 */
import { must } from './invariant';
import { lttbDecimate, nearestIndexForX } from './chart-decimation';
import { getSpeedSmoothingSeconds } from './smoothing';
import { buildSeriesViews, lineForMode, type SeriesMode, type SeriesViews } from './chart-series-modes';
import { getGpsData, trackSeconds } from './gps-data';
import { robustYBounds } from './chart-robust-scale';
import { CHART_HOVER_THROTTLE_MS } from './constants';
import { chartAreaGradient, enableLineSelect } from './chart-line-select';
import { xZoomConfig, finalizeChart } from './chart-zoom-pan';
import { registerChartSourceData, highlightChartIndex, destroyChartOverlay } from './chart-overlay';
import { isChartPanning } from './chart-pan-state';
import { attachWheelZoom, type GesturePreviewGroup, type XRange } from './chart-gesture-preview';
import { setChartEmptyState } from './chart-empty-state';
import { getAccountSettings } from './account-settings';

// ChartInstance: local alias for the Chart.js instance type (CDN global, no runtime import).
// zoom methods (zoomScale, resetZoom) are available via module augmentation in globals.d.ts.
type ChartInstance = InstanceType<typeof Chart>;

/** Options accepted by createGpsLineChart(). */
export interface ChartCreateOpts {
	canvasId: string;
	label: string;
	dataField: string;
	borderColor: string;
	backgroundColor: string;
	yUnit?: string;
	xUnit?: string;
	yFormat: string;
	tooltipPrefix: string;
	tooltipUnit: string;
	/** Sample-indexed smoothing override. Setting it opts the chart out of the seconds domain. */
	smoothingWindow?: number;
	/** Which view to open on. Omitted = the smoothed line. */
	seriesMode?: SeriesMode;
	chartName: string;
	borderWidth?: number;
	fill?: boolean;
	fillColor?: string | ((ctx: { chart: ChartInstance }) => string | CanvasGradient);
	backgroundField?: string;
	backgroundFill?: string;
	hideGrid?: boolean;
	onHighlight?: (index: number) => void;
	onMouseLeave?: () => void;
	onZoom?: (chart: ChartInstance) => void;
	/** Page-owned chart-click hook (distance km, index). Details passes a closure reading its
	 *  transient picker slot; other pages omit it. Replaces the former window.onChartClick read. */
	onChartClick?: (distance: number, index: number) => void;
	/**
	 * Shared gesture preview group for the page's x-synced charts (bitmap preview per frame,
	 * one real render at gesture end). When set, the zoom plugin's per-tick wheel handler is
	 * replaced by attachWheelZoom and pan/pinch route through the group. The group's apply
	 * must run redecimateChartForRange (its beforeApply hook) — zoomScale fires no onZoom.
	 */
	previewGroup?: GesturePreviewGroup | null;
}

const _chartInstances = new Map<string, ChartInstance>();

// Re-creating a chart on the same canvas aborts the factory's previous hover/click/touch
// listeners — their closures hold the full-resolution data arrays (mirrors chart-zoom-pan's
// _panControllers; Chart.destroy() only removes Chart.js' own listeners, not these).
const _factoryListeners = new WeakMap<HTMLElement, AbortController>();

// Per-chart redecimation hook for gesture-apply / reset paths outside this factory
// (page code can't reach the closure-scoped _redecimateForZoom).
const _redecimators = new WeakMap<ChartInstance, (range?: XRange) => void>();

// Per-chart view swap (see core/chart-series-modes), for the page's chart control.
const _modeSetters = new WeakMap<ChartInstance, (mode: SeriesMode) => void>();

/**
 * Switch a factory chart between its series views, preserving its zoom.
 * No-op for a chart that was not built here.
 */
export function setChartSeriesMode(chart: ChartInstance | null, mode: SeriesMode): void {
	if (chart) _modeSetters.get(chart)?.(mode);
}

/**
 * Re-run the factory's LTTB window decimation for `chart` (no-op for non-factory charts).
 * `range` = the x-window to decimate for; omitted = the chart's live scale range (e.g. right
 * after resetZoom). Mutates dataset data only — caller decides when the render happens
 * (GesturePreviewGroup.beforeApply → the zoomScale render; reset paths → update("none")).
 */
export function redecimateChartForRange(chart: ChartInstance | null, range?: XRange): void {
	if (chart) _redecimators.get(chart)?.(range);
}

/**
 * Create a Chart.js line chart with all the standard RouteBase options
 * (zoom, pan, sync, highlight, smoothing, decimation).
 *
 * @returns The Chart.js instance, or null on error
 */
export function createGpsLineChart(opts: ChartCreateOpts): ChartInstance | null {
	const dataSource = getGpsData();
	if (dataSource.length === 0) {
		// Same empty-data contract as every other chart (item 4): a visible message, not a blank canvas.
		const emptyCanvas = document.getElementById(opts.canvasId);
		setChartEmptyState(emptyCanvas?.parentElement ?? null, "No data to plot.", true);
		console.warn(`No GPS data for ${opts.chartName} chart`);
		return null;
	}
	if (typeof Chart === "undefined") {
		console.error("Chart.js library not loaded!");
		return null;
	}
	const canvas = document.getElementById(opts.canvasId) as HTMLCanvasElement | null;
	if (!canvas) {
		console.error(`${opts.chartName} chart canvas #${opts.canvasId} not found!`);
		return null;
	}
	// Data present — clear any empty state left from a prior data-less render.
	setChartEmptyState(canvas.parentElement, "", false);

	// Destroy existing chart on this canvas
	const existingKey = "_routebaseChart_" + opts.canvasId;
	const existingChart = _chartInstances.get(existingKey);
	if (existingChart && typeof existingChart.destroy === "function") {
		destroyChartOverlay(existingChart); // overlay canvas + ResizeObserver must not outlive the chart
		existingChart.destroy();
		_chartInstances.delete(existingKey);
	}

	_factoryListeners.get(canvas)?.abort();
	const _listenerAc = new AbortController();
	_factoryListeners.set(canvas, _listenerAc);
	const _sig = { signal: _listenerAc.signal };

	// getContext returns null when the canvas already holds a different context kind — fail like
	// the missing-canvas path instead of handing Chart.js a null.
	const ctx = canvas.getContext("2d");
	if (!ctx) {
		console.error(`${opts.chartName} chart canvas #${opts.canvasId} has no 2d context`);
		return null;
	}

	// Data
	const distances = dataSource.map((p) => p.distance);

	// Styling — tension forced to 0: Bezier curves create visual artifacts
	// (loops/overshoots) with LTTB-decimated data at certain zoom levels.
	const tension = 0;
	const borderWidth =
		opts.borderWidth ?? getAccountSettings().charts.borderWidth;

	// Tick format helper
	const yTickFn = (v: string | number): string =>
		typeof v === "number"
			? opts.yFormat === "int"
				? v.toFixed(0)
				: v.toFixed(1)
			: v;
	const tooltipValFn =
		opts.yFormat === "int" ? (v: number) => v.toFixed(0) : (v: number) => v.toFixed(1);

	// Corner unit labels: the bottom/left-most tick (index 0) shows the bare
	// unit instead of a number while the chart is at rest, freeing the gutter
	// the axis title used to occupy. Once zoomed/panned the corner reverts to
	// the real value so the visible range stays readable.
	const xUnit = opts.xUnit ?? "km";
	const yUnit = opts.yUnit ?? opts.tooltipUnit;

	// LTTB (Largest Triangle Three Buckets) decimation — reduces data to
	// ~targetSamples while preserving visual shape. Applied up-front so
	// Chart.js only draws ~1000 points instead of 15k+.
	const DECIMATE_TARGET = 1000;

	// Used only where there is no time axis to measure a window against.
	const FALLBACK_SAMPLE_WINDOW = 20;

	// Elapsed seconds per point, or null when the track has no timestamps. This is what lets the
	// speed window be a physical duration instead of a point count — see core/smoothing.
	const seconds = trackSeconds(dataSource);

	// Elevation stays on the sample-indexed kernel: it arrives already conditioned by the server over
	// a fixed ARC LENGTH, so this pass is cosmetic and its window is not claiming to be a duration.
	const _sampleWindow = (field: string): number =>
		field === "altitude"
			// ?? not ||: a configured window of 0 means "no smoothing" and must not silently become the default.
			? getAccountSettings().charts.elevationSmoothingWindow
			: FALLBACK_SAMPLE_WINDOW;

	// Build both views of a field (recorded / smoothed) plus their decimated twins. All series
	// share the `distances` x-array, so one index window re-decimates them together on zoom.
	//
	// No `|| 0` on the way in: a missing sample is a gap, and reading it as a standstill is what made
	// a dropped HR strap draw a dip to zero.
	function _buildViews(field: string, overrideWindow?: number): SeriesViews {
		const raw = dataSource.map((p) => {
			const v = p[field] as number | undefined;
			return typeof v === "number" ? v : Number.NaN;
		});
		return buildSeriesViews({
			values: raw,
			xs: distances,
			// A per-chart sample override (heart rate) opts that chart out of the time domain entirely.
			seconds: overrideWindow != null || field === "altitude" ? null : seconds,
			windowSeconds: getSpeedSmoothingSeconds(),
			sampleWindow: overrideWindow ?? _sampleWindow(field),
			bins: getAccountSettings().charts.speedBinCount,
			target: DECIMATE_TARGET,
		});
	}

	const mainViews = _buildViews(opts.dataField, opts.smoothingWindow);
	let seriesMode: SeriesMode = opts.seriesMode ?? "smoothBin";
	let xyData = lineForMode(mainViews, seriesMode).full;
	const chartData = lineForMode(mainViews, seriesMode).dec;

	// Optional greyscale background series (e.g. elevation drawn behind speed).
	// The elevation silhouette is a backdrop, so it takes the processed view whatever that is.
	const bgSeries = opts.backgroundField ? _buildViews(opts.backgroundField).views.smoothBin : null;


	// Re-decimate visible range on zoom so the chart maintains high point
	// density when zoomed in instead of showing sparse interpolated curves.
	// `range` — decimate for this x-window instead of the live scale (which is stale
	// during a preview gesture: the apply hook runs before zoomScale moves the scale).
	function _redecimateForZoom(c: ChartInstance, range?: XRange): void {
		const xScale = c.scales.x;
		if (!xScale || xyData.length <= DECIMATE_TARGET) return;

		const xMin = range ? range.min : xScale.min;
		const xMax = range ? range.max : xScale.max;

		// Find the slice of full-res data within the visible x range.
		// xyData is sorted by x, so binary search for start/end.
		let lo = 0, hi = xyData.length - 1;
		while (lo < hi) { const m = (lo + hi) >>> 1; if (must(xyData[m], 'search in-bounds').x < xMin) lo = m + 1; else hi = m; }
		const startIdx = Math.max(0, lo - 1);
		lo = startIdx; hi = xyData.length - 1;
		while (lo < hi) { const m = (lo + hi + 1) >>> 1; if (must(xyData[m], 'search in-bounds').x > xMax) hi = m - 1; else lo = m; }
		const endIdx = Math.min(xyData.length - 1, lo + 1);

		// The binned modes are re-derived for the visible range, so ~BIN_COUNT bins stay on screen at
		// every zoom. The result is written back into xyData because the crosshair and the page readout
		// read THAT array — leaving it at whole-ride bins would make the number disagree with the line.
		const rebuilt = mainViews.rebuildRange(seriesMode, startIdx, endIdx);
		if (rebuilt) for (let i = 0; i < rebuilt.length; i++) xyData[startIdx + i] = must(rebuilt[i], 'rebuilt in-bounds');

		const visibleSlice = rebuilt ?? xyData.slice(startIdx, endIdx + 1);
		const newData = visibleSlice.length > DECIMATE_TARGET
			? lttbDecimate(visibleSlice, DECIMATE_TARGET)
			: visibleSlice;

		// Replace dataset data without triggering a second update
		must(c.data.datasets[0], 'main dataset exists').data = newData as import("chart.js").Point[];

		// Re-decimate the background series over the same x-window (shared distances).
		if (bgSeries && c.data.datasets[1]) {
			const bgSlice = bgSeries.full.slice(startIdx, endIdx + 1);
			c.data.datasets[1].data = (bgSlice.length > DECIMATE_TARGET
				? lttbDecimate(bgSlice, DECIMATE_TARGET)
				: bgSlice) as import("chart.js").Point[];
		}
	}

	/**
	 * Fit y to the P1/P99 of what is actually drawn, rather than to Chart.js' autoscale.
	 *
	 * Details charts used to rely on the smoothing to keep a 500 km/h GPS spike from squashing the
	 * axis — an accident, not a decision, and one that breaks the moment a mode draws less-smoothed
	 * data.
	 */
	function _refitY(c: ChartInstance): void {
		const yScale = c.options.scales?.y;
		if (!yScale) return;
		const measured: Array<Array<{ y?: number | null }>> = [
			(c.data.datasets[0]?.data ?? []) as Array<{ y?: number | null }>,
		];
		const bounds = robustYBounds(measured, { beginAtZero: opts.dataField !== "altitude" });
		if (!bounds) return; // nothing measurable — leave autoscale alone
		yScale.min = bounds.min;
		yScale.max = bounds.max;
	}

	// Throttle state for onHover — gates the entire hover path including
	// the index lookup so the expensive work runs at most ~30fps.
	let _lastHoverCallTime = 0;
	let _lastHoverIndex = -1;
	const _HOVER_THROTTLE_MS = CHART_HOVER_THROTTLE_MS;

	// Cast to ChartInstance to include chartjs-plugin-zoom methods (zoomScale, resetZoom)
	const chart = new Chart(ctx, {
		type: "line",
		data: {
			datasets: [
				{
					label: opts.label,
					data: chartData,
					borderColor: opts.borderColor,
					backgroundColor: opts.fill
						? (opts.fillColor ?? chartAreaGradient(opts.borderColor, 0.45))
						: opts.backgroundColor,
					fill: opts.fill ? "start" : false,
					tension: tension,
					pointRadius: 0,
					pointHoverRadius: 0,
					borderWidth: borderWidth,
					// Round joins, not the canvas default of miter. A miter extends a sharp vertex out to
					// miterLimit × lineWidth/2 — up to ~10 px — so a hard corner grows a small spike past
					// where the line actually turns. It shows on the stepped-looking modes, only at sharp
					// edges, and eases off as you zoom in and the angles soften. core/profile-chart sets
					// the same thing on the canvas strip, which is why the race strip never shows it.
					borderJoinStyle: "round" as CanvasLineJoin,
					borderCapStyle: "round" as CanvasLineCap,
					order: 0,
					indexAxis: "x",
					parsing: false,
					normalized: true,
				},
				// Greyscale filled background series (drawn first via higher order).
				...(bgSeries
					? [{
						label: opts.backgroundField as string,
						data: bgSeries.dec,
						borderColor: "transparent",
						backgroundColor: opts.backgroundFill ?? "rgba(140, 145, 160, 0.30)",
						fill: "start",
						tension: 0,
						pointRadius: 0,
						pointHoverRadius: 0,
						borderWidth: 0,
						order: 1,
						yAxisID: "yBg",
						parsing: false,
						normalized: true,
					}]
					: []),
			] as import("chart.js").ChartDataset<"line">[],
		},
		options: {
			responsive: true,
			maintainAspectRatio: false,
			animation: false,
			interaction: { mode: "nearest", axis: "x", intersect: false },
			hover: { intersect: false },
			events: ["mouseout"] as (keyof HTMLElementEventMap)[],
			plugins: {
				legend: { display: false },
				tooltip: {
					enabled: !opts.onHighlight,
					animation: false,
					callbacks: {
						label: (context: import("chart.js").TooltipItem<"line">) =>
							`${opts.tooltipPrefix}: ${tooltipValFn(context.parsed.y ?? 0)} ${opts.tooltipUnit}`,
						title: (context: import("chart.js").TooltipItem<"line">[]) =>
							`Distance: ${parseFloat(String(context[0]?.parsed.x)).toFixed(2)} ${xUnit}`,
					},
				},
				// `as unknown`: chartjs-plugin-zoom config is not part of chart.js's core PluginOptionsByType.
				// With a preview group the plugin's wheel handler (real update per tick) is replaced
				// by attachWheelZoom below; the plugin stays configured for zoomScale/resetZoom state.
				zoom: xZoomConfig((c) => {
					_redecimateForZoom(c);
					if (opts.onZoom) opts.onZoom(c);
				}, { wheel: !opts.previewGroup }) as unknown,
			} as NonNullable<import("chart.js").ChartOptions<"line">["plugins"]>,
			scales: {
				x: {
					type: "linear",
					// Pin the axis to the data extent so the line fills the full
					// width. Without this Chart.js rounds the max up to a "nice"
					// number, leaving empty space on the right until the user zooms.
					...(distances.length ? { min: distances[0], max: distances[distances.length - 1] } : {}),
					title: { display: false },
					grid: { display: !opts.hideGrid },
					ticks: {
						// "inner" right-aligns the last label (e.g. 34.0) inside the plot
						// so it doesn't overflow the canvas edge, and lets the plot use
						// more width on the right.
						align: "inner" as const,
						callback: function (this: import("chart.js").Scale, v: string | number, index: number): string {
							if (typeof v !== "number") return v;
							if (index === 0 && !this.chart.isZoomedOrPanned()) return xUnit;
							return v.toFixed(1);
						},
					},
				},
				y: {
					title: { display: false },
					grid: { display: !opts.hideGrid },
					ticks: {
						padding: 2,
						callback: function (this: import("chart.js").Scale, v: string | number, index: number): string {
							if (index === 0 && typeof v === "number" && !this.chart.isZoomedOrPanned()) return yUnit;
							return yTickFn(v);
						},
					},
				},
				// Hidden, independently-scaled axis for the greyscale background series.
				...(bgSeries
					? { yBg: { type: "linear", display: false, position: "right", grace: "8%" } }
					: {}),
			},
		},
		plugins: [],
	});

	// Fit y before the first paint, not only after a mode change: the modes have visibly different
	// ranges, and Chart.js autoscale would let one GPS spike squash the whole plot.
	_refitY(chart);
	chart.update("none");

	// Store full (un-decimated) arrays for overlay pixel lookups
	registerChartSourceData(chart, distances, xyData);

	// Expose the x-window redecimation for gesture-apply / reset paths (redecimateChartForRange).
	_redecimators.set(chart, (range?: XRange) => _redecimateForZoom(chart, range));

	/**
	 * Swap which view the chart draws, keeping the x-zoom.
	 *
	 * A dataset swap rather than a rebuild, because a rebuild would reset the zoom — changing how a
	 * series is drawn must not throw away where the user was looking. Only y changes, so this is the
	 * same shape as segment-stats' delta swap.
	 */
	_modeSetters.set(chart, (next: SeriesMode) => {
		if (next === seriesMode) return;
		seriesMode = next;

		const line = lineForMode(mainViews, seriesMode);
		// A fresh copy: _redecimateForZoom writes rebuilt bins back into xyData, and mutating the view's
		// own array would corrupt the mode for the next switch.
		xyData = line.full.slice();
		must(chart.data.datasets[0], 'main dataset exists').data = line.dec as import("chart.js").Point[];
		// The crosshair and the page's readout read THIS array — re-registering is what keeps the
		// number under the cursor equal to the line it is pointing at.
		registerChartSourceData(chart, distances, xyData);

		// Restore the zoom window's decimation, then re-fit y: the recorded series reaches further than
		// the smoothed line, and without this its peaks clip against bounds fitted to a smoother
		// series. Robust bounds (P1/P99) rather than autoscale, because a single GPS spike would
		// otherwise squash the whole chart — smoothing used to hide that by accident.
		_redecimateForZoom(chart);
		_refitY(chart);
		chart.update("none");
	});

	// Store reference for later cleanup
	_chartInstances.set(existingKey, chart);

	// Unified zoom callback — used by both the wheel-zoom plugin and our
	// custom touch pinch handler so redecimation + sync fire for all zoom triggers.
	// With a preview group these non-preview paths never run; the group's apply
	// hooks (redecimateChartForRange + page afterApply) take over.
	const _fullZoomCallback = (c: ChartInstance): void => {
		_redecimateForZoom(c);
		if (opts.onZoom) opts.onZoom(c);
	};

	// Shared chart defaults (panning, Y-axis freeze on X-zoom, inner X tick, tight Y padding).
	// Click-drag panning + touch pinch zoom replace chartjs-plugin-zoom pinch. Working reliably
	// across browsers takes touch-action on the exact canvas AND the iOS gesture* refusal —
	// both are applied by chart-zoom-pan.ts via core/claim-pinch-gesture.ts.
	finalizeChart(canvas, chart, opts.chartName, _fullZoomCallback, opts.previewGroup);

	// Wheel x-zoom through the preview group (plugin wheel is off above) — bitmap preview
	// per tick, one debounced real render after the burst. getBounds MUST be the data extent:
	// the default (axis options) breaks zoom-out, because the apply render's zoomScale mutates
	// options.scales.x.min/max to the zoomed window (same reason seg-stats passes wheelBounds).
	if (opts.previewGroup) {
		const dataBounds = {
			min: must(distances[0], 'non-empty (guarded above)'),
			max: must(distances.at(-1), 'non-empty (guarded above)'),
		};
		attachWheelZoom(canvas, () => chart, opts.previewGroup, () => dataBounds);
	}

	// Click the line to toggle an area fill + thicker stroke (single dataset → just this line).
	// Skipped when the line is permanently filled — toggling would clear that fill.
	if (!opts.fill) enableLineSelect(chart, _listenerAc.signal);

	// Direct mousemove listener — bypasses Chart.js entirely so its
	// _eventHandler -> render() -> draw() cycle never fires on hover.
	// Only our binary search + overlay draw runs.
	if (opts.onHighlight) {
		const onHighlight = opts.onHighlight; // const rebind — the narrowing survives into the handlers
		canvas.addEventListener("mousemove", (e: MouseEvent) => {
			if (isChartPanning()) return;

			const now = performance.now();
			if (now - _lastHoverCallTime < _HOVER_THROTTLE_MS) return;
			_lastHoverCallTime = now;

			const xScale = chart.scales.x;
			if (!xScale) return;
			const rect = canvas.getBoundingClientRect();
			const pixelX = e.clientX - rect.left;
			const xVal = xScale.getValueForPixel(pixelX);
			if (xVal == null) return;

			const idx = nearestIndexForX(distances, xVal);
			if (idx === _lastHoverIndex) return;
			_lastHoverIndex = idx;

			onHighlight(idx);
			highlightChartIndex(chart, idx);
		}, _sig);

		// Touch highlight — mirrors mousemove for mobile finger drag.
		// touchstart shows the bar immediately on finger-down; touchmove
		// tracks it as the finger slides. touch-action: pan-y (set on the canvas
		// in chart-zoom-pan.ts) keeps the browser from consuming these first,
		// while still allowing a one-finger vertical page scroll.
		const _handleTouchHighlight = (e: TouchEvent) => {
			if (isChartPanning() || e.touches.length !== 1) return;
			const now = performance.now();
			if (now - _lastHoverCallTime < _HOVER_THROTTLE_MS) return;
			_lastHoverCallTime = now;
			const xScale = chart.scales.x;
			if (!xScale) return;
			const rect = canvas.getBoundingClientRect();
			const pixelX = must(e.touches[0], 'length === 1 (guarded above)').clientX - rect.left;
			const xVal = xScale.getValueForPixel(pixelX);
			if (xVal == null) return;
			const idx = nearestIndexForX(distances, xVal);
			if (idx === _lastHoverIndex) return;
			_lastHoverIndex = idx;
			onHighlight(idx);
			highlightChartIndex(chart, idx);
		};
		canvas.addEventListener("touchstart", _handleTouchHighlight, { passive: true, signal: _listenerAc.signal });
		canvas.addEventListener("touchmove",  _handleTouchHighlight, { passive: true, signal: _listenerAc.signal });
	}

	// Direct click listener — bypasses Chart.js event system entirely,
	// same pattern as mousemove above.
	// Also handles mobile tap (synthesized as a click), where mousemove never
	// fires, so highlightChartIndex must be called here for the clicked chart.
	// Drag guard: mousedown.preventDefault() does NOT reliably suppress the synthesized click
	// (it prevents focus/selection, not click synthesis — Firefox always fires it), so a pan
	// release could land a phantom onChartClick and corrupt an armed segment-picker range.
	// Suppress by distance moved since mousedown; a genuine tap/click moves ~0 px.
	let _clickDownX = 0, _clickDownY = 0;
	canvas.addEventListener("mousedown", (e: MouseEvent) => {
		_clickDownX = e.clientX; _clickDownY = e.clientY;
	}, _sig);
	canvas.addEventListener("click", (e: MouseEvent) => {
		if (Math.abs(e.clientX - _clickDownX) + Math.abs(e.clientY - _clickDownY) > 5) return;
		const xScale = chart.scales.x;
		if (!xScale) return;
		const rect = canvas.getBoundingClientRect();
		const pixelX = e.clientX - rect.left;
		const xVal = xScale.getValueForPixel(pixelX);
		if (xVal == null) return;
		const idx = nearestIndexForX(distances, xVal);
		highlightChartIndex(chart, idx);
		if (opts.onHighlight) opts.onHighlight(idx);
		// Page-owned click hook (Details segment-picker). Only pages that pass onChartClick fire it.
		// Read from the build-time closure (dataSource) — index-aligned with idx by construction —
		// not a live getGpsData() (would desync idx from data if GPS were ever swapped; CF-02).
		if (opts.onChartClick && dataSource[idx] != null) {
			opts.onChartClick(dataSource[idx].distance, idx);
		}
	}, _sig);

	// Mouse-leave handler
	if (opts.onMouseLeave) {
		canvas.addEventListener("mouseleave", opts.onMouseLeave, _sig);
	}

	return chart;
}
