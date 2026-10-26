/**
 * Segment Statistics — Charts module (core)
 *
 * Handles performance/elevation/effort-cost charts, zoom sync, and control wiring.
 * Extracted helpers (SC2-05): scale/extent math → segment-stats-charts-scale.ts,
 * backdrop plugins → segment-stats-charts-plugins.ts, hover readout strip →
 * segment-stats-charts-readout.ts, progressive load → segment-stats-charts-load.ts.
 * Progress chart is in segment-stats-charts-progress.ts.
 *
 * Depends on: ctx (shared context from segment-statistics.ts)
 */
// Local Chart.js type aliases (CDN global, no runtime import)
type LinePlugins = NonNullable<import("chart.js").ChartOptions<"line">["plugins"]>;

import { readData } from '../core/data-attrs';
import { scaleInternals } from '../core/chart-internals';
import type { ChartJsInstance } from '../core/core-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { ChartPanelManager } from '../core/chart-panel-manager';
import { GesturePreviewGroup, attachWheelZoom } from '../core/chart-gesture-preview';
import { robustYBounds } from '../core/chart-robust-scale';
import { xZoomConfig, finalizeChart } from '../core/chart-zoom-pan';
import { destroyChartOverlay } from '../core/chart-overlay';
import { setLineSelection } from '../core/chart-line-select';
import { ctx, setStoredChartXAxis } from './segment-statistics';
import { createDeltaSystem, type ChartKey, type PointXFn } from './segment-stats-delta';
import { createAggregateSystem } from './segment-stats-aggregate';
import { shouldClientDecimate } from './chart-point-budget';
import { effortPointX, computeMaxX, visibleYData, rawXExtent, cornerTick } from './segment-stats-charts-scale';
import { makeDensityPlugin, makeElevBackgroundPlugin } from './segment-stats-charts-plugins';
import { createChartReadout } from './segment-stats-charts-readout';
import { createChartLoad } from './segment-stats-charts-load';
import { getAccountSettings } from '../core/account-settings';
import { must } from '../core/invariant';
import { createSeriesModeControl, type SeriesModeControl } from '../core/series-mode-control';

/** The speed chart's Recorded/Smoothed button. Rebuilt whenever the toggles are rewired. */
let _speedModeControl: SeriesModeControl | null = null;

/**
 * Build and wire the perf/elevation/effort-cost comparison charts for this page. Hoisted out of
 * the module IIFE (was a top-level `if (ctx…)` body) so the render path is reachable from a test
 * with a synthetic ctx. Behaviour and import-time semantics are unchanged: the top-level call at
 * the bottom passes the shared ctx exactly as before, and every collaborator (delta/aggregate/
 * readout/load) still reads that same singleton.
 */
export function initSegmentStatsCharts(ctx: SegmentStatisticsContext): void {

	const config = ctx.config;

	// bind this init's ctx into the scale helper once so every collaborator (delta/aggregate/
	// plugins) receives a plain PointXFn while the impl still reads ctx.currentXAxis live per call
	// (the elevation backdrop redraws on every gesture and must track x-mode without a rebuild).
	const pointX: PointXFn = (p, totalDistance) => effortPointX(ctx, p, totalDistance);

	// Statsbox dimming: efforts whose statsbox checkbox is off keep their line but at low opacity
	// (appended as a hex alpha) and are excluded from the hover readout box. ~15% opacity.
	const STATS_DIM_ALPHA = "26";
	function statsBorderColor(color: string, index: number): string {
		return ctx.effortStats.has(index) ? color : color + STATS_DIM_ALPHA;
	}

	const canvasPerf = document.getElementById("performanceChart") as HTMLCanvasElement | null;
	const canvasElev = document.getElementById("elevationChart") as HTMLCanvasElement | null;
	const canvasEffort = document.getElementById("effortChart") as HTMLCanvasElement | null;
	if (canvasPerf && canvasElev) {

	const perfCtx = canvasPerf.getContext("2d");
	const elevCtx = canvasElev.getContext("2d");
	const effortCtx = canvasEffort?.getContext("2d") ?? null;

	// ====================================================================
	// CHART ZOOM — gesture preview group
	// ====================================================================

	// One preview group over the three x-synced charts: gestures (wheel/drag/pinch) draw a
	// bitmap preview on ALL three canvases per frame, and the single real render at gesture
	// end applies the same range to all of them — this replaces the old per-tick
	// syncChartZoom, whose 3 full re-renders per wheel tick were the zoom/pan lag.
	//
	// ID-15 hybrid (mode read per gesture from ctx.aggregateMode):
	//  - aggregate: few datasets, apply ~ 0 ms => per-frame real renders (no bitmap, so no
	//    black area past a snapshot).
	//  - individual: 744 datasets / ~270 ms apply => overscan bitmap snapshot: per-frame real
	//    renders are infeasible, so the snapshot is widened OVERSCAN_FACTOR x the viewport and
	//    re-snapshotted only when a pan runs past that buffer. M4 calibrates OVERSCAN_FACTOR
	//    (3 = pan a full viewport each way before a re-snapshot); the end apply spike is unchanged.
	const OVERSCAN_FACTOR = 3;
	const gesturePreview = new GesturePreviewGroup(
		() => [ctx.performanceChart, ctx.elevationChart, ctx.effortChart],
		undefined,
		{
			getMode: () => ctx.aggregateMode
				? { perFrame: true }
				: { perFrame: false, overscan: OVERSCAN_FACTOR },
		},
	);

	// ====================================================================
	// PANEL RESIZE — delegated to ChartPanelManager
	// ====================================================================

	const _panelMgr = new ChartPanelManager({
		wrapperId: "chartPanelWrap",
		xAxisTabs: ["speed", "elevation", "effort"],
		defaultTabs: ["speed", "elevation"],
		chartMap: {
			speed: () => ctx.performanceChart,
			elevation: () => ctx.elevationChart,
			effort: () => ctx.effortChart,
			progress: () => ctx.progressChart,
			distribution: () => ctx.distributionChart,
		},
	});

	function _resizeChartsAfterLayout(tabKeys: string[]): void {
		_panelMgr._resizeAfterLayout(tabKeys);
	}

	function initChartTabs(): void {
		_panelMgr.initButtons();
	}

	// ====================================================================
	// DELTA / AGGREGATE / READOUT SYSTEMS
	// ====================================================================

	// Per-chart "delta vs reference effort" system (speed / elevation / effort-cost). All the
	// dataset/title/format/refresh logic lives in segment-stats-delta.ts; we inject the two local
	// helpers it needs (statsBorderColor from here, effortPointX from the scale module).
	const delta = createDeltaSystem({ ctx, statsBorderColor, effortPointX: pointX });

	// Aggregate mode (density backdrop + P25–P75 band + median for the bulk, highlighted efforts drawn
	// individually) reuses the delta system's per-effort series so band values match the plotted lines exactly.
	const aggregate = createAggregateSystem({ ctx, seriesProvider: delta.seriesProvider });

	// Hover readout strip + mouse/touch hover wiring; onEffortSelect rebuilds delta charts against the pin.
	const readout = createChartReadout({ ctx, delta });

	// Density backdrop plugin (per chart) blits the aggregate density bitmap; elevation backdrop draws
	// the highlighted effort's profile behind the speed lines.
	const elevBackgroundPlugin = makeElevBackgroundPlugin({ ctx, effortPointX: pointX });

	// The three comparison charts, tagged with the base beginAtZero (frees the robust y-floor to 0 in
	// total mode) — used by _refitYAxis / aggregate rebuild to recompute robust bounds with the right floor.
	const METRIC_CHARTS: Array<{ key: ChartKey; baseZero: boolean; get: () => ChartJsInstance | null }> = [
		{ key: "speed", baseZero: true, get: () => ctx.performanceChart },
		{ key: "elevation", baseZero: false, get: () => ctx.elevationChart },
		{ key: "effort", baseZero: false, get: () => ctx.effortChart },
	];

	const decimationConfig = {
		enabled: true,
		algorithm: "lttb" as const,
		samples: 500,
		threshold: 500,
	};

	// ====================================================================
	// PERFORMANCE & ELEVATION CHARTS
	// ====================================================================

	const renderChart = (): void => {
		if (!ctx.performanceData) return;
		gesturePreview.cancel(); // snapshots reference the charts being destroyed
		// SC2-01: drop the stale rerender closure + hide the readout boxes BEFORE destroying the charts
		// it closes over — otherwise a later box-row hover/click fires it against a destroyed chart
		// (chart.canvas null → TypeError) and it pins the whole destroyed dataset array in memory.
		ctx._rerenderStrip = null;
		readout.hideAllSegBoxes();
		// Overlay teardown BEFORE destroy: the hover crosshair's overlay canvas + ResizeObserver
		// otherwise outlive the chart and pin it (and its datasets) across every rebuild.
		for (const chart of [ctx.performanceChart, ctx.elevationChart, ctx.effortChart]) {
			if (chart) { destroyChartOverlay(chart); chart.destroy(); }
		}
		// SC2-07: leaving aggregate mode → drop the retained density bitmaps (~11 MB at 3 charts). They
		// rebuild from scratch on the next aggregate toggle. Kept while still in aggregate (rebuild reuses).
		if (!ctx.aggregateMode) aggregate.clearDensityImages();

		const chartTension = getAccountSettings().charts.tension;
		const chartBorderWidth = getAccountSettings().charts.borderWidth;

		// Per-point x mapping lives in the scale module's `effortPointX` (used by the builders) — it reads
		// ctx.currentXAxis live. `usePercent` here only drives the axis label + extent below.
		// "percent" uses the backend-computed segmentProgress (true position ALONG the segment) so the
		// same physical location lines up across efforts; falls back to per-effort distance.
		const usePercent = ctx.currentXAxis === "percent";

		// Speed / Elevation / Effort-cost datasets are built by the shared per-chart helper so each
		// chart can independently rebuild (delta toggle / selection) without recreating the others.
		// Aggregate mode swaps per-effort lines for the band+median (+ highlighted efforts) build.
		const buildDatasetsFor = (key: ChartKey): unknown[] =>
			ctx.aggregateMode
				? aggregate.buildAggregateDatasets(key, chartBorderWidth, chartTension)
				: delta.buildMetricDatasets(key, chartBorderWidth, chartTension);
		const speedDatasets = buildDatasetsFor("speed");
		const elevationDatasets = buildDatasetsFor("elevation");
		const costDatasets = buildDatasetsFor("effort");

		// Pin the axis to the longest *visible* effort's extent so the lines fill the full width without
		// Chart.js rounding the max up to a "nice" number (empty space on the right). Recomputed live by
		// ctx._refitXAxis when efforts are toggled. See computeMaxX.
		const maxX = computeMaxX(ctx, usePercent);
		const sharedXAxisConfig = {
			type: "linear" as const,
			display: true,
			min: 0,
			max: maxX,
			title: { display: false },
			ticks: {
				// "inner" right-aligns the last label (e.g. 100) inside the plot so it
				// doesn't overflow the canvas edge, and lets the plot use more width.
				align: "inner" as const,
				callback: function (this: import("chart.js").Scale, v: string | number, index: number): string {
					if (typeof v !== "number") return v;
					return usePercent
						? cornerTick(this.chart, v, index, "%", (p) => p.toFixed(0))
						: cornerTick(this.chart, v, index, "time", (t) =>
							Math.floor(t / 60) + ":" + Math.floor(t % 60).toString().padStart(2, "0"));
				},
			},
		};
		// Zoom plugin stays configured (zoomScale/resetZoom state) but its wheel handler is off —
		// wheel zoom goes through attachWheelZoom + the gesture preview group instead.
		const zoomConfig: unknown = xZoomConfig(null, { wheel: false });

		// The three comparison charts share one option skeleton (responsive/animation/interaction,
		// hover→readout, legend-off live title, shared x-axis + zoom + decimation, corner-tick y-axis).
		// They differ only in: canvas, datasets, hover unit + formatter, y-tick formatter, base
		// beginAtZero (Δ mode always frees the axis), the delta key driving the live title/format, and
		// (perf only) the elevation backdrop plugin. One factory keeps them in lockstep.
		const makeMetricChart = (
			canvasCtx: CanvasRenderingContext2D,
			datasets: unknown[],
			o: {
				deltaKey: ChartKey;          // "speed" | "elevation" | "effort"
				hoverKey: string;            // throttle/sync key: "perf" | "elev" | "cost"
				unit: string;                // hover-readout + y-tick unit
				yTickFmt: (v: number) => string;
				baseBeginAtZero: boolean;    // y beginAtZero when NOT in delta mode
				getChart: () => ChartJsInstance | null;
				extraPlugins?: object[];
			},
		): ChartJsInstance => {
			// Hover formatting is supplied per-canvas to attachSegMouseHover/attachSegTouchScrub below
			// (the old onHover callback that used a local formatter here was removed).
			// Pin the y-axis to robust (P1/P99) bounds over the visible efforts up front so an outlier
			// spike can't squash every real line — outliers clip off the plot. Null (all placeholders
			// mid-load) → leave min/max undefined and let Chart.js autoscale; freeze pins it afterwards.
			const beginAtZero = !delta.chartIsDelta(o.deltaKey) && o.baseBeginAtZero;
			const yBounds = robustYBounds(visibleYData(ctx, datasets as Array<{ data?: Array<{ y?: number | null }> }>), { beginAtZero });
			return new Chart(canvasCtx, {
				type: "line",
				data: { datasets: datasets as never },
				options: {
					responsive: true,
					maintainAspectRatio: false,
					animation: false,
					interaction: { mode: "nearest", axis: "x", intersect: false },
					// Restrict Chart.js to mouseout only so native mousemove no longer triggers its
					// update → render → draw cycle (which repaints every effort line). Hover is driven by
					// attachSegMouseHover (direct DOM listener) + an overlay-canvas crosshair instead.
					events: ["mouseout"],
					plugins: ({
						legend: { display: false },
						title: { display: true, text: delta.chartTitle(o.deltaKey) },
						tooltip: { enabled: false },
						zoom: zoomConfig,
						// Client LTTB decimation only when the data is FULL-RES (budget 0 = knob "Full")
						// and not aggregated (PS-11). Server-downsampled data is already at a fixed,
						// bounded resolution, so decimation OFF makes zoom rescale the loaded points
						// ("picture-like") instead of re-slicing per zoom — the individual-tracks lag
						// the user reported. Aggregate mode is always off (LTTB re-slicing mangled the
						// 3000-bin band/median on zoom-in).
						decimation: shouldClientDecimate(ctx.perfPointBudget, ctx.aggregateMode)
							? decimationConfig
							: { ...decimationConfig, enabled: false },
					} as LinePlugins),
					scales: {
						x: sharedXAxisConfig,
						y: {
							type: "linear",
							display: true,
							title: { display: false },
							min: yBounds?.min,
							max: yBounds?.max,
							// Δ mode produces negative values (cheaper than the reference), so the
							// axis must not be pinned to zero regardless of the chart's base setting.
							beginAtZero,
							ticks: {
								padding: 2,
								callback: function (this: import("chart.js").Scale, v: string | number, index: number): string {
									return typeof v === "number" ? cornerTick(this.chart, v, index, o.unit, o.yTickFmt) : String(v);
								},
							},
						},
					},
				},
				plugins: [...(o.extraPlugins ?? [])] as never,
			});
		};

		ctx.performanceChart = makeMetricChart(must(perfCtx, 'canvas yields a 2d context'), speedDatasets, {
			deltaKey: "speed", hoverKey: "perf", unit: "km/h",
			yTickFmt: String, baseBeginAtZero: true,
			getChart: () => ctx.performanceChart,
			extraPlugins: [elevBackgroundPlugin, makeDensityPlugin("speed", { aggregate, ctx })],
		});

		ctx.elevationChart = makeMetricChart(must(elevCtx, 'canvas yields a 2d context'), elevationDatasets, {
			deltaKey: "elevation", hoverKey: "elev", unit: "m",
			yTickFmt: String, baseBeginAtZero: false,
			getChart: () => ctx.elevationChart,
			extraPlugins: [makeDensityPlugin("elevation", { aggregate, ctx })],
		});

		if (effortCtx) {
			ctx.effortChart = makeMetricChart(effortCtx, costDatasets, {
				deltaKey: "effort", hoverKey: "cost", unit: "kJ",
				yTickFmt: (n) => n.toFixed(0), baseBeginAtZero: false,
				getChart: () => ctx.effortChart,
				extraPlugins: [makeDensityPlugin("effort", { aggregate, ctx })],
			});
		}

		// Don't tear down immediately: schedule the box to hide, and let the box's onHide run
		// ctx.onChartLeave() when it actually closes. Moving the cursor onto the (overlapping) box
		// cancels that pending hide, so the markers/crosshair survive while a row is picked.
		readout.setMouseLeave(canvasPerf, readout.perfLeave, readout.scheduleHideSegBoxes);
		readout.setMouseLeave(canvasElev, readout.elevLeave, readout.scheduleHideSegBoxes);
		if (canvasEffort) {
			readout.setMouseLeave(canvasEffort, readout.effortLeave, readout.scheduleHideSegBoxes);
		}

		// Touch scrub: Chart.js fires onHover on mousemove only, so dragging the crosshair by touch
		// moved the vertical line but never refreshed the readout box. Drive the same update path
		// (cross-chart sync + box values) from touchstart/touchmove. Skipped while a 1-finger pan is
		// active (only when zoomed — see setupPanning) so panning and scrubbing don't fight.
		readout.attachSegTouchScrub(canvasPerf, "perf", () => ctx.performanceChart, "km/h", delta.makeFormatter("speed"));
		readout.attachSegTouchScrub(canvasElev, "elev", () => ctx.elevationChart, "m", delta.makeFormatter("elevation"));
		if (canvasEffort) {
			readout.attachSegTouchScrub(canvasEffort, "cost", () => ctx.effortChart, "kJ", delta.makeFormatter("effort"));
		}

		// Mouse hover via direct DOM listener (replaces the Chart.js onHover callback removed above).
		readout.attachSegMouseHover(canvasPerf, "perf", () => ctx.performanceChart, "km/h", delta.makeFormatter("speed"));
		readout.attachSegMouseHover(canvasElev, "elev", () => ctx.elevationChart, "m", delta.makeFormatter("elevation"));
		if (canvasEffort) {
			readout.attachSegMouseHover(canvasEffort, "cost", () => ctx.effortChart, "kJ", delta.makeFormatter("effort"));
		}

		// Shared chart defaults: X-only drag/pinch panning + freeze each Y axis on X-zoom (so the
		// height doesn't rescale/flicker). The gesture preview group keeps the perf/elev/effort
		// trio in sync: every gesture previews on all three canvases and applies once, at the end.
		finalizeChart(canvasPerf, ctx.performanceChart, "performance", null, gesturePreview);
		finalizeChart(canvasElev, ctx.elevationChart, "elevation", null, gesturePreview);
		if (canvasEffort && ctx.effortChart) {
			finalizeChart(canvasEffort, ctx.effortChart, "effort", null, gesturePreview);
		}

		// Wheel zoom via the preview group (plugin wheel is disabled above). Re-attaching on
		// rebuild is safe — attachWheelZoom aborts the canvas' previous listener. getBounds → the
		// raw extent (not the capped axis) so zoom-out reaches efforts past the initial percentile cap.
		const wheelBounds = () => rawXExtent(ctx);
		attachWheelZoom(canvasPerf, () => ctx.performanceChart, gesturePreview, wheelBounds);
		attachWheelZoom(canvasElev, () => ctx.elevationChart, gesturePreview, wheelBounds);
		if (canvasEffort) {
			attachWheelZoom(canvasEffort, () => ctx.effortChart, gesturePreview, wheelBounds);
		}

		// Effort highlight is driven by the readout box (hover a row to emphasise, click to pin),
		// not by clicking the thin chart line — see selectEffortFromBox / hoverEffortFromBox.
		// Re-apply a selection that survived this rebuild (visibility toggle / x-axis switch). Skipped
		// in aggregate mode — the pinned effort's emphasis (fill + thicker line) is baked into its
		// aggregate dataset instead (dataset index ≠ effortIndex there, so setLineSelection can't map).
		if (!ctx.aggregateMode && ctx.selectedEffortIndex != null) {
			for (const chart of [ctx.performanceChart, ctx.elevationChart, ctx.effortChart]) {
				if (chart) setLineSelection(chart, ctx.selectedEffortIndex);
			}
		}
	};

	// Re-fit the comparison charts' x-axis to the currently-visible efforts, in place (no rebuild).
	// Called by updateVisibleEfforts (segment-statistics.ts) right before each chart.update(), so hiding
	// the longest effort tightens the axis — not just removes the line — in a single repaint.
	ctx._refitXAxis = (): void => {
		const max = computeMaxX(ctx, ctx.currentXAxis === "percent");
		for (const chart of [ctx.performanceChart, ctx.elevationChart, ctx.effortChart]) {
			// SC2-03: don't half-reset a zoomed window. When the user is zoomed to [100,200], overwriting
			// only x.max would yield [100,newFullMax] — neither the zoom nor the fit. Re-fit applies on the
			// next reset/zoom-out, when the axis options are authoritative again (same API as :465).
			if ((chart as { isZoomedOrPanned?: () => boolean }).isZoomedOrPanned?.()) continue;
			const xScale = chart?.options.scales?.x as { max?: number | undefined } | undefined;
			if (xScale) xScale.max = max;
		}
	};

	// Re-fit the comparison charts' y-axis to the currently-visible efforts. The axis is pinned to
	// robust (P1/P99) bounds so hiding the tallest effort re-fits the plot without the outlier squash.
	// Recompute those bounds for the now-visible set and set the LIVE scale's options directly (not
	// chart.options, a different object — mirroring refreshDeltaChart), then one repaint. Called by
	// updateVisibleEfforts after toggling meta.hidden. Null bounds (nothing visible) → leave as-is.
	ctx._refitYAxis = (): void => {
		for (const mc of METRIC_CHARTS) {
			const chart = mc.get();
			if (!chart) continue;
			const beginAtZero = !delta.chartIsDelta(mc.key) && mc.baseZero;
			const bounds = robustYBounds(visibleYData(ctx, chart.data.datasets as Array<{ data?: Array<{ y?: number | null }> }>), { beginAtZero });
			const yScale = chart.scales.y ? scaleInternals(chart.scales.y) : undefined;
			if (yScale && bounds) { yScale.options.min = bounds.min; yScale.options.max = bounds.max; }
			chart.update("none");
		}
	};

	// Rebuild one chart's aggregate datasets in place (band/median recomputed for the new visible or
	// highlighted set) + re-fit robust y-bounds + title, then a single repaint. In aggregate mode this
	// replaces the per-effort paths (refitX/Y, refreshDeltaChart, setLineSelection) — the band spans the
	// visible efforts and highlight/selection/delta all change which datasets exist, so it's a full rebuild.
	function rebuildAggregateChart(mc: { key: ChartKey; baseZero: boolean; get: () => ChartJsInstance | null }): void {
		const chart = mc.get();
		if (!chart) return;
		const borderWidth = getAccountSettings().charts.borderWidth;
		const tension = getAccountSettings().charts.tension;
		chart.data.datasets = aggregate.buildAggregateDatasets(mc.key, borderWidth, tension) as typeof chart.data.datasets;

		const titleOpts = chart.options.plugins?.title as { text?: string } | undefined;
		if (titleOpts) titleOpts.text = delta.chartTitle(mc.key);

		const beginAtZero = !delta.chartIsDelta(mc.key) && mc.baseZero;
		const bounds = robustYBounds(visibleYData(ctx, chart.data.datasets as Array<{ data?: Array<{ y?: number | null }> }>), { beginAtZero });
		const yScale = chart.scales.y ? scaleInternals(chart.scales.y) : undefined;
		if (yScale) { yScale.options.min = bounds ? bounds.min : undefined; yScale.options.max = bounds ? bounds.max : undefined; }
		chart.update("none");
	}

	ctx._rebuildAggregate = (): void => {
		for (const mc of METRIC_CHARTS) rebuildAggregateChart(mc);
	};

	// Re-apply statsbox dimming to the live charts without a full rebuild (toggling a row's checkbox).
	function applyStatsDimming(): void {
		// A pinned effort must stay in the statsbox set; if it was just un-checked, drop the pin.
		const dropSel = ctx.selectedEffortIndex != null && !ctx.effortStats.has(ctx.selectedEffortIndex);

		// Aggregate mode: the statsbox set IS the highlighted set (drawn as individual lines) and also
		// determines band membership — rebuild the band + highlighted lines in one pass. No per-line dimming.
		if (ctx.aggregateMode) {
			if (dropSel) ctx.selectedEffortIndex = null;
			ctx._rebuildAggregate?.();
			return;
		}

		if (dropSel) {
			if (ctx.performanceChart) setLineSelection(ctx.performanceChart, null);
			if (ctx.elevationChart) setLineSelection(ctx.elevationChart, null);
			if (ctx.effortChart) setLineSelection(ctx.effortChart, null);
			readout.onEffortSelect(null);
		}
		[ctx.performanceChart, ctx.elevationChart, ctx.effortChart].forEach((chart) => {
			if (!chart) return;
			const datasets = chart.data.datasets as { borderColor?: string }[];
			for (let i = 0; i < datasets.length; i++) {
				const baseColor = must(ctx.effortColors[i % ctx.effortColors.length], 'in-bounds modulo of non-empty palette');
				must(datasets[i], 'i < length (loop-bounded)').borderColor = statsBorderColor(baseColor, i);
			}
			chart.update("none");
		});
	}

	ctx._applyStatsDimming = applyStatsDimming;

	// ====================================================================
	// CONTROL WIRING & LOAD
	// ====================================================================

	// One-time wiring (tabs + x-axis / delta / progress toggles). Attached once after the whole load.
	function initChartListeners(): void {
		initChartTabs();

		// The markup hard-codes `checked` on the Time radio, so a restored 'percent' would render percent
		// charts under a control reading Time. Sync the radio to ctx, which was seeded from the stored
		// preference at import — the control must show the view, not the markup's default.
		document.querySelectorAll<HTMLInputElement>('input[name="chartXAxis"]').forEach((radio) => {
			radio.checked = radio.value === ctx.currentXAxis;
		});

		document.querySelectorAll('input[name="chartXAxis"]').forEach((radio) => {
			radio.addEventListener("change", (e: Event) => {
				ctx.currentXAxis = (e.target as HTMLInputElement).value;
				setStoredChartXAxis(ctx.currentXAxis);
				renderChart();
				// SC2-06: read the active tabs fresh (activePanels snapshot) instead of a stale live capture.
				_resizeChartsAfterLayout(_panelMgr.activePanels());
			});
		});

		// Per-chart delta toggles (corner Δ button on each chart) — flip that one chart between
		// Total and delta-vs-reference and rebuild only its data in place.
		document.querySelectorAll<HTMLButtonElement>(".chart-delta-toggle").forEach((btn) => {
			btn.addEventListener("click", () => {
				const key = readData(btn, 'deltaChart');
				if (key !== "speed" && key !== "elevation" && key !== "effort") return;
				const on = !ctx.deltaCharts.has(key);
				if (on) ctx.deltaCharts.add(key); else ctx.deltaCharts.delete(key);
				btn.setAttribute("aria-pressed", String(on));
				btn.classList.toggle("is-active", on);
				// Aggregate mode recomputes the band+median (and highlighted lines) against the new delta
				// baseline; normal mode rebuilds just this chart's per-effort data.
				if (ctx.aggregateMode) {
					const mc = METRIC_CHARTS.find((m) => m.key === key);
					if (mc) rebuildAggregateChart(mc);
				} else {
					delta.refreshDeltaChart(key);
				}
			});
		});

		// Created after the charts exist so its restore callback redraws rather than pre-empting them.
		_speedModeControl?.destroy();
		_speedModeControl = createSeriesModeControl({
			button: document.getElementById("segSpeedModeToggle"),
			prefKey: "segmentSpeed",
			onChange: (mode) => {
				delta.setSpeedMode(mode);
				if (ctx.aggregateMode) {
					const mc = METRIC_CHARTS.find((m) => m.key === "speed");
					if (mc) rebuildAggregateChart(mc);
				} else {
					delta.refreshDeltaChart("speed");
				}
			},
		});

		// Aggregate toggle (chip) — swap between per-effort lines and the density + band + median view. A manual
		// press wins over the auto-on threshold for the rest of the page-load. Full renderChart because
		// the whole dataset structure changes (per-effort ↔ band/median/highlighted).
		const aggBtn = document.getElementById("chartAggregateToggle") as HTMLButtonElement | null;
		aggBtn?.addEventListener("click", () => {
			chartLoad.setAggregateManual(true);
			ctx.aggregateMode = !ctx.aggregateMode;
			chartLoad.syncAggregateButton();
			renderChart();
			_resizeChartsAfterLayout(_panelMgr.activePanels());
		});

		document.querySelectorAll('input[name="progressXAxis"]').forEach((radio) => {
			radio.addEventListener("change", (e: Event) => {
				ctx.progressXAxis = (e.target as HTMLInputElement).value;
				if (typeof ctx._renderProgressChart === "function") ctx._renderProgressChart();
			});
		});

		document.querySelectorAll('input[name="progressYMetric"]').forEach((radio) => {
			radio.addEventListener("change", (e: Event) => {
				ctx.progressYMetric = (e.target as HTMLInputElement).value;
				if (typeof ctx._renderProgressChart === "function") ctx._renderProgressChart();
			});
		});
	}

	// Success tail shared by the normal-completion and the chunk-failure paths (SC2-02): even a fatal
	// chunk failure renders the efforts that did load (placeholders yield empty datasets) and wires the
	// chart controls, instead of leaving a silent, chartless, listener-dead card. Decide aggregate mode
	// first (auto-on past the threshold) so the initial paint is already the band view.
	function finishChartInit(): void {
		chartLoad.maybeAutoAggregate();
		renderChart();
		initChartListeners();
		ctx._renderProgressChart?.();
	}

	const chartLoad = createChartLoad({ ctx, config, onLoaded: finishChartInit });

	void chartLoad.load();

	} // end if (canvasPerf && canvasElev)
} // end initSegmentStatsCharts

if (ctx && ctx.config.hasEfforts) {
	initSegmentStatsCharts(ctx);
}
