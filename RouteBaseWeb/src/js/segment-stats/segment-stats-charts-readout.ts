/**
 * Segment Statistics — hover readout strip + mouse/touch hover wiring
 * (extracted from segment-stats-charts.ts, SC2-05).
 *
 * The floating readout box above the crosshair (one row per statsbox-enabled effort),
 * the per-canvas mouse/touch hover listeners that drive it, and the effort
 * hover/selection it pins. Factory takes the delta system (for the reference-effort
 * rebuild on selection) and the live ctx, RECEIVED instead of reached for via
 * the module singleton; the returned closures still read that ctx live per hover.
 */
import type { ChartJsInstance } from '../core/core-types';
import { elementXAt, isDatasetVisible, syntheticChartEvent } from '../core/chart-internals';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { CHART_HOVER_THROTTLE_MS } from '../core/constants';
import { ChartReadoutBox, type ReadoutRow } from '../core/chart-readout-box';
import { setLineSelection } from '../core/chart-line-select';
import { isChartPanning } from '../core/chart-pan-state';
import type { DeltaSystem, ChartKey } from './segment-stats-delta';
import { formatElapsedHms } from './seg-stats-shared';
import { formatDateDMY } from '../core/grid/grid-format';
import { must } from '../core/invariant';

// Nearest index in a chart dataset's {x,…}[] (sorted ascending by x) to a target x-value.
// CRITICAL: each effort/series is decimated independently and efforts have different point
// counts, so the same array index maps to a DIFFERENT x across efforts and across the
// speed/elevation/distance charts. In time mode (≈1 Hz GPS) index≈seconds≈x so reusing an
// index happens to line up; in % Complete mode x is segmentProgress, unrelated to the index,
// which made cross-effort hover values and crosshairs land on random points. Always resolve
// by x-value via this helper — never by reusing a point index.
export function nearestIndexByX(data: Array<{ x?: number } | null>, xVal: number): number {
	if (!data || data.length === 0) return 0;
	let lo = 0, hi = data.length - 1;
	while (lo < hi) {
		const mid = (lo + hi) >> 1;
		if ((data[mid]?.x ?? 0) < xVal) lo = mid + 1;
		else hi = mid;
	}
	if (lo > 0 && Math.abs((data[lo - 1]?.x ?? 0) - xVal) <= Math.abs((data[lo]?.x ?? 0) - xVal))
		return lo - 1;
	return lo;
}

// Fixed monospace width reserved for a readout row's label so the box doesn't reflow when the label
// swaps between a 10-char date ("yyyy-MM-dd") and an 8-char elapsed time ("HH:MM:SS").
const SEG_LABEL_WIDTH_CH = 10;

export interface ChartReadoutSystem {
	attachSegMouseHover: (canvas: HTMLElement, chartKey: string, getChart: () => ChartJsInstance | null, unit: string, formatVal: (v: number | null | undefined) => string) => void;
	attachSegTouchScrub: (canvas: HTMLElement, chartKey: string, getChart: () => ChartJsInstance | null, unit: string, formatVal: (v: number | null | undefined) => string) => void;
	setMouseLeave: (canvas: HTMLElement, handlerRef: { current: EventListener | null }, fn: EventListener) => void;
	perfLeave: { current: EventListener | null };
	elevLeave: { current: EventListener | null };
	effortLeave: { current: EventListener | null };
	scheduleHideSegBoxes: () => void;
	hideAllSegBoxes: () => void;
	onEffortSelect: (index: number | null) => void;
	/** Exposed for tests (SC2-01/SC2-04). */
	updateHoverStrip: (chart: ChartJsInstance | null, hoverDsIndex: number, pointIndex: number, unit: string, formatVal: (v: number | null | undefined) => string, xMode: string) => void;
}

export function createChartReadout(deps: { delta: DeltaSystem; ctx: SegmentStatisticsContext }): ChartReadoutSystem {
	const { delta, ctx } = deps;

	const _hoverThrottles: Record<string, number> = {};
	const _HOVER_THROTTLE_MS = CHART_HOVER_THROTTLE_MS;

	// ====================================================================
	// MOUSELEAVE LISTENER MANAGEMENT
	// ====================================================================

	function setMouseLeave(canvas: HTMLElement, handlerRef: { current: EventListener | null }, fn: EventListener): void {
		if (handlerRef.current) {
			canvas.removeEventListener("mouseleave", handlerRef.current);
		}
		handlerRef.current = fn;
		canvas.addEventListener("mouseleave", fn);
	}

	const _perfLeave: { current: EventListener | null } = { current: null };
	const _elevLeave: { current: EventListener | null } = { current: null };
	const _effortLeave: { current: EventListener | null } = { current: null };

	// Single mouse+touch hover path: pan guard → throttle → cross-chart sync + crosshair → readout box.
	// One time-throttle (keyed by chartKey) now gates the WHOLE path — the cross-chart sync/crosshair
	// (ctx.onChartHover) AND the readout-box rebuild (updateHoverStrip, which does an innerHTML rebuild
	// + layout reflow). Works for a synthetic mouse ChartEvent and a synthetic touch ChartEvent alike
	// (both carry x/y + native). The active element is resolved via getElementsAtEventForMode (a pure
	// hit-test query — it does NOT repaint the chart) for the readout box's x/caret.
	function runSegHover(
		chartKey: string,
		chart: ChartJsInstance | null,
		event: import("chart.js").ChartEvent,
		unit: string,
		formatVal: (v: number | null | undefined) => string,
	): void {
		if (!chart) return;
		if (isChartPanning()) return;
		const now = performance.now();
		if (_hoverThrottles[chartKey] && now - _hoverThrottles[chartKey] < _HOVER_THROTTLE_MS) return;
		_hoverThrottles[chartKey] = now;
		ctx.onChartHover?.(chart, event);
		const els = chart.getElementsAtEventForMode(event, "nearest", { axis: "x", intersect: false }, false);
		if (els.length > 0) {
			updateHoverStrip(chart, els[0].datasetIndex, els[0].index, unit, formatVal, "auto");
		}
	}

	// renderChart re-runs the hover/scrub wiring on every rebuild — abort the canvas' previous
	// listeners instead of stacking them (same pattern as attachWheelZoom / setupPanning).
	const _segHoverControllers = new WeakMap<HTMLElement, AbortController>();
	const _segScrubControllers = new WeakMap<HTMLElement, AbortController>();

	// Mouse hover via a direct DOM mousemove listener (mirrors chart-factory's createGpsLineChart) instead of a
	// Chart.js onHover callback. Chart.js' onHover fires inside its native event → update → render → draw
	// cycle, which repaints every effort line on every mousemove; events:["mouseout"] on the chart stops
	// that, and this listener drives the hover path itself. A per-pixel guard skips redundant work when
	// the cursor hasn't moved horizontally (the crosshair is x-only), on top of the time-throttle above.
	function attachSegMouseHover(
		canvas: HTMLElement,
		chartKey: string,
		getChart: () => ChartJsInstance | null,
		unit: string,
		formatVal: (v: number | null | undefined) => string,
	): void {
		_segHoverControllers.get(canvas)?.abort();
		const ac = new AbortController();
		_segHoverControllers.set(canvas, ac);
		let lastPx = -1;
		canvas.addEventListener("mousemove", (e: MouseEvent) => {
			if (isChartPanning()) return;
			const chart = getChart();
			if (!chart || !chart.chartArea) return;
			const rect = chart.canvas.getBoundingClientRect();
			const px = e.clientX - rect.left;
			const rounded = Math.round(px);
			if (rounded === lastPx) return; // same x-pixel → nothing new to draw
			lastPx = rounded;
			const chartEvent = syntheticChartEvent(px, e.clientY - rect.top, e);
			runSegHover(chartKey, chart, chartEvent, unit, formatVal);
		}, { signal: ac.signal });
	}

	// Touch scrub for a chart canvas: synthesises a Chart.js-style event from the touch point and runs
	// the same hover path as the mouse (Chart.js' onHover never fires on touchmove, which left the box
	// frozen while the crosshair followed the finger on mobile).
	function attachSegTouchScrub(
		canvas: HTMLElement,
		chartKey: string,
		getChart: () => ChartJsInstance | null,
		unit: string,
		formatVal: (v: number | null | undefined) => string,
	): void {
		_segScrubControllers.get(canvas)?.abort();
		const ac = new AbortController();
		_segScrubControllers.set(canvas, ac);
		const onTouch = (e: TouchEvent): void => {
			if (e.touches.length !== 1) return;
			const chart = getChart();
			if (!chart || !chart.chartArea) return;
			const rect = chart.canvas.getBoundingClientRect();
			const t = e.touches[0];
			if (!t) return; // dead: touchstart/touchmove always carry a touch
			// A normalized ChartEvent (the `native` key must be present for Chart.js' getRelativePosition
			// to read x/y directly); same shape onChartHover already feeds getElementsAtEventForMode.
			const chartEvent = syntheticChartEvent(t.clientX - rect.left, t.clientY - rect.top, null);
			runSegHover(chartKey, chart, chartEvent, unit, formatVal);
		};
		canvas.addEventListener("touchstart", onTouch, { passive: true, signal: ac.signal });
		canvas.addEventListener("touchmove", onTouch, { passive: true, signal: ac.signal });
	}

	// ====================================================================
	// HOVER INFO STRIP
	// ====================================================================

	// The hover values live in a floating readout box above the crosshair (one row per statsbox-enabled
	// effort). Which efforts appear there — and which lines are dimmed — is driven by ctx.effortStats.
	const _segBoxes = new WeakMap<HTMLElement, ChartReadoutBox>();

	function _segBox(chart: ChartJsInstance): ChartReadoutBox | null {
		const host = chart.canvas.parentElement as HTMLElement | null; // .chart-container (position:relative)
		if (!host) return null;
		let box = _segBoxes.get(host);
		if (!box) {
			box = new ChartReadoutBox(host);
			_segBoxes.set(host, box);
			// Box rows drive the highlight: hover a row to emphasise that effort, click to pin it.
			// onHide defers the map/crosshair teardown until the (sticky) box actually closes, so
			// moving the cursor from the chart onto the box keeps the markers alive.
			box.setInteractive({
				onRowHover: hoverEffortFromBox,
				onRowClick: (key) => selectEffortFromBox(Number(key)),
				onHide: () => ctx.onChartLeave?.(),
			});
		}
		return box;
	}

	// Hide all readout boxes after a short grace period (entering a box cancels its own pending hide),
	// so the cursor can travel from the chart onto the box without it vanishing.
	function _scheduleHideSegBoxes(): void {
		for (const c of [ctx.performanceChart, ctx.elevationChart, ctx.effortChart]) {
			const host = c?.canvas.parentElement as HTMLElement | null;
			const box = host ? _segBoxes.get(host) : null;
			if (box) box.scheduleHide();
		}
	}

	// Hide all readout boxes immediately (no grace period, no onHide teardown) — used before a chart
	// rebuild (SC2-01) so a box (with its ✕) can't survive a rebuild on coarse pointers that never
	// fire mouseleave. Reads each live canvas's host directly (charts not yet destroyed at call time).
	function hideAllSegBoxes(): void {
		for (const c of [ctx.performanceChart, ctx.elevationChart, ctx.effortChart]) {
			const host = c?.canvas?.parentElement as HTMLElement | null;
			const box = host ? _segBoxes.get(host) : null;
			box?.hide();
		}
	}

	// Hovering a readout row temporarily emphasises that effort (bold row + enlarged map marker);
	// leaving the rows (key null) clears it. Repaints the box and refreshes the map from ctx state.
	function hoverEffortFromBox(key: string | null): void {
		// The "median" aggregate row has a non-numeric key — treat it as no effort (no emphasis).
		const n = key != null ? Number(key) : NaN;
		const next = Number.isNaN(n) ? null : n;
		if (next === ctx.hoveredEffortIndex) return;
		ctx.hoveredEffortIndex = next;
		ctx._rerenderStrip?.();
		ctx.refreshHighlight?.();
	}

	// Clicking a readout row pins that effort (fill + thicker line on every synced chart); clicking the
	// pinned effort again clears it. Only statsbox-enabled efforts (those listed in the box) are eligible.
	function selectEffortFromBox(effortIndex: number): void {
		if (Number.isNaN(effortIndex) || !ctx.effortStats.has(effortIndex)) return;
		const next = ctx.selectedEffortIndex === effortIndex ? null : effortIndex;
		// Aggregate mode: setLineSelection can't map (dataset index ≠ effortIndex) — onEffortSelect
		// rebuilds the aggregate datasets, where the pinned effort's emphasis is baked into its line.
		if (!ctx.aggregateMode) {
			for (const chart of [ctx.performanceChart, ctx.elevationChart, ctx.effortChart]) {
				if (chart) setLineSelection(chart, next);
			}
		}
		onEffortSelect(next);
		ctx.hoveredEffortIndex = null; // the pin now governs emphasis
		ctx._rerenderStrip?.();
		ctx.refreshHighlight?.();
	}

	function _segEffortTime(i: number): string {
		const eff = ctx.performanceData?.[i];
		if (eff) return formatElapsedHms(eff.elapsedTime);
		return "Effort " + (i + 1);
	}

	// Row label: the activity date by default, switching to the effort's elapsed time once an effort is
	// selected (so a selection turns the box into a time comparison). Not switched on hover — that would
	// flicker as the cursor moves between lines.
	function _segEffortLabel(i: number): string {
		if (ctx.selectedEffortIndex != null) return _segEffortTime(i);
		const eff = ctx.performanceData?.[i];
		// formatDateDMY, not the wire string verbatim: activityDate is wall-clock ISO now (B3);
		// echoing it would show the full stamp instead of the day.
		return formatDateDMY(eff?.activityDate) || _segEffortTime(i);
	}

	function updateHoverStrip(
		chart: ChartJsInstance | null,
		hoverDsIndex: number,
		pointIndex: number,
		_unit: string,
		formatVal: (v: number | null | undefined) => string,
		xMode: string,
	): void {
		// SC2-01: a stale _rerenderStrip closure can fire after renderChart() destroyed its chart
		// (Chart.js destroy() nulls chart.canvas) — guard every field _segBox/this fn dereferences.
		if (!chart || !chart.chartArea || !chart.canvas) return;

		// X-position → box title. Read x from the actually-hovered dataset so the box
		// caret lines up with the vertical crosshair (which tracks the hovered element).
		let title = "";
		const ds0 = chart.data.datasets[hoverDsIndex] as {data: {x?: number}[]} | undefined;
		const x0 = ds0?.data[pointIndex]?.x;
		if (x0 !== undefined) {
			const mode = xMode === "auto" ? ctx.currentXAxis : xMode;
			title = mode === "percent"
				? x0.toFixed(0) + "%"
				: Math.floor(x0 / 60) + ":" + Math.floor(x0 % 60).toString().padStart(2, "0");
		}

		// Emphasis is driven by the readout box itself: the pinned (clicked) effort, else the
		// effort whose row is currently hovered. No cursor-line proximity detection on the chart.
		const emph = ctx.selectedEffortIndex ?? ctx.hoveredEffortIndex ?? -1;
		const rows: ReadoutRow[] = [];
		// Rows are keyed by _effortIndex (set on every dataset), so the loop works in both modes: normal =
		// one row per statsbox-checked effort; aggregate = the Median row + one per highlighted effort
		// (band datasets are skipped). Falls back to positional index for any legacy dataset without a tag.
		const datasets = chart.data.datasets as Array<{
			data: ({ x?: number; y?: number | null } | null)[];
			borderColor?: string; _effortIndex?: number | null; _aggRole?: "band" | "median" | "effort";
			_binCountsAt?: (x: number) => number;
		}>;
		for (let i = 0; i < datasets.length; i++) {
			const ds = must(datasets[i], 'i < length (loop-bounded)');
			const role = ds._aggRole;
			const ei = ds._effortIndex === undefined ? i : ds._effortIndex; // legacy fallback
			if (ctx.aggregateMode) {
				if (role === "band") continue; // band edges carry no readable value
			} else {
				// Skip efforts whose statsbox checkbox is off — their line stays (dimmed) but the value is
				// hidden. Cheap O(1) Set check first: it rejects most of the ~hundreds of datasets before
				// the costlier Chart.js meta lookup below.
				if (ei == null || !ctx.effortStats.has(ei)) continue;
				// SC2-04: on a freshly-built chart meta.hidden is null and visibility lives on the
				// dataset (.hidden). isDatasetVisible resolves meta.hidden ?? dataset.hidden exactly as
				// the renderer does, so a hidden effort's row no longer reappears after a rebuild.
				if (!isDatasetVisible(chart, i)) continue;
			}
			// Resolve each series' point by the hovered x-value, not by reusing pointIndex —
			// series have different sampling/decimation so equal indices mean different x.
			const pi = x0 !== undefined
				? nearestIndexByX(ds.data, x0)
				: Math.min(pointIndex, ds.data.length - 1);
			const p = ds.data[pi];
			if (!p) continue;
			const isMedian = role === "median";
			const color = (ds.borderColor as string) || (ei != null ? ctx.effortColors[ei % ctx.effortColors.length] ?? "" : "") || "#888";
			// Median row also shows how many efforts contribute at this x (short efforts drop out).
			const nAtX = isMedian && p.x != null ? ds._binCountsAt?.(p.x) : undefined;
			rows.push({
				color,
				label: isMedian ? "Median" : _segEffortLabel(ei as number),
				cells: nAtX != null
					? [{ num: formatVal(p.y), unit: "" }, { num: String(nAtX), unit: "eff" }]
					: [{ num: formatVal(p.y), unit: "" }],
				emphasis: !isMedian && ei != null && ei === emph,
				labelWidthCh: SEG_LABEL_WIDTH_CH,
				rowKey: isMedian ? "median" : String(ei),
			});
		}

		const caretX = elementXAt(chart, hoverDsIndex, pointIndex) ?? chart.chartArea.left;
		const box = _segBox(chart);
		if (!box) return;
		// Remember how to repaint this same box (box-row hover/click changes emphasis or the pinned
		// selection while the cursor sits on the box, with no fresh chart hover event to redraw it).
		ctx._rerenderStrip = () => updateHoverStrip(chart, hoverDsIndex, pointIndex, _unit, formatVal, xMode);
		box.show(title, rows, caretX, chart.chartArea.top);

		// Hide every other (synced) chart's box so only the hovered chart shows one.
		for (const other of [ctx.performanceChart, ctx.elevationChart, ctx.effortChart]) {
			if (!other || other === chart) continue;
			const otherHost = other.canvas.parentElement as HTMLElement | null;
			const otherBox = otherHost ? _segBoxes.get(otherHost) : null;
			if (otherBox) otherBox.hide();
		}
	}

	// Apply an effort selection (fill + thicker line); driven by selectEffortFromBox (readout-box click).
	const onEffortSelect = (index: number | null): void => {
		ctx.selectedEffortIndex = index;
		// Aggregate mode: selection changes both the highlighted set (individual lines) and the delta
		// reference — rebuild all three aggregate charts, which also refreshes the elevation backdrop.
		if (ctx.aggregateMode) { ctx._rebuildAggregate?.(); return; }
		// Repaint the performance chart so the elevation backdrop (drawn by
		// elevBackgroundPlugin from ctx.selectedEffortIndex) updates immediately.
		ctx.performanceChart?.update("none");
		// In delta mode the selected effort IS the baseline, so every chart currently in delta must
		// rebuild against the new reference. Deferred so it runs after the synced setLineSelection
		// redraws for the selection currently in flight.
		if (ctx.deltaCharts.size > 0) {
			const keys = Array.from(ctx.deltaCharts) as ChartKey[];
			setTimeout(() => { for (const key of keys) delta.refreshDeltaChart(key); }, 0);
		}
	};

	return {
		attachSegMouseHover,
		attachSegTouchScrub,
		setMouseLeave,
		perfLeave: _perfLeave,
		elevLeave: _elevLeave,
		effortLeave: _effortLeave,
		scheduleHideSegBoxes: _scheduleHideSegBoxes,
		hideAllSegBoxes,
		onEffortSelect,
		updateHoverStrip,
	};
}
