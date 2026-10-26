/**
 * chart-overlay.ts — transparent overlay-canvas highlighting for line charts.
 *
 * Instead of chart.setActiveElements() + chart.draw() (which repaints the whole
 * Chart.js canvas), a crosshair line + highlight dot are drawn on a transparent
 * canvas layered over the chart. Also owns the split (segment) rectangle overlay.
 *
 * Colours flow through cssVar (theme-aware) with the previous Bootstrap values as
 * fallbacks, so a dark-mode theme can reach the canvas draws.
 */
import { must } from './invariant';
import { cssVar } from './css-vars';
import { reportChartError } from './chart-error-log';

type ChartInstance = InstanceType<typeof Chart>;

// Full-resolution source arrays per chart, for overlay pixel lookups (the drawn
// datasets are LTTB-decimated, so indices there don't map to real data points).
const _chartLabels = new WeakMap<object, number[]>();
const _chartFullXY = new WeakMap<object, { x: number; y: number }[]>();
const _chartSplitHighlights = new WeakMap<object, HTMLElement>();

/** Register a chart's full-resolution distances + xy arrays so the overlay can map index → pixel. */
export function registerChartSourceData(
	chart: ChartInstance,
	distances: number[],
	fullXY: { x: number; y: number }[],
): void {
	_chartLabels.set(chart, distances);
	_chartFullXY.set(chart, fullXY);
}

/**
 * The y-value the chart is DRAWING at a full-resolution index, or null if it has none registered.
 *
 * A page readout must report this rather than the source channel: the drawn series is smoothed (and
 * changes with the chart's view mode), so reading the channel makes the box disagree with the line
 * it is pointing at.
 */
export function drawnValueAt(chart: ChartInstance | null, index: number): number | null {
	if (!chart) return null;
	const y = _chartFullXY.get(chart)?.[index]?.y;
	return typeof y === "number" && Number.isFinite(y) ? y : null;
}

/** Optional per-call crosshair styling. Defaults = the themed solid red line, width 2. */
export interface CrosshairStyle {
	color?: string;
	width?: number;
	/** Dash pattern, e.g. [3,3]. Omitted/[] = solid. */
	dash?: number[];
}

// CC-08 / chart-kernel item 2: THE single vertical-crosshair stroke. Shared by the overlay-canvas
// highlight/bare-crosshair draws AND the Chart.js-plugin crosshairs (Statistics dashed guide, Segment
// progress line) that used to each re-implement it. Self-contained: saves/restores its own line state
// and resets the dash so the pattern never leaks into a following draw (the dots after the guide).
export function strokeCrosshairLine(
	ctx2d: CanvasRenderingContext2D,
	x: number,
	top: number,
	bottom: number,
	style?: CrosshairStyle,
): void {
	ctx2d.save();
	ctx2d.beginPath();
	ctx2d.setLineDash(style?.dash ?? []);
	ctx2d.lineWidth = style?.width ?? 2;
	ctx2d.strokeStyle = style?.color ?? cssVar("--chart-crosshair", "#dc3545");
	ctx2d.moveTo(x, top);
	ctx2d.lineTo(x, bottom);
	ctx2d.stroke();
	ctx2d.setLineDash([]);
	ctx2d.restore();
}

// Touch devices get a grab-handle on the crosshair: no hover means the bar is placed by
// tapping, and once the chart is zoomed a plain drag pans — the handle is the affordance
// (and hit target, see getCrosshairPixelX) for dragging the bar itself.
const _COARSE_POINTER =
	typeof window !== "undefined" &&
	typeof window.matchMedia === "function" &&
	window.matchMedia("(pointer: coarse)").matches;

function drawTouchHandle(ctx2d: CanvasRenderingContext2D, x: number, areaBottom: number): void {
	const cy = areaBottom - 12;
	ctx2d.beginPath();
	ctx2d.arc(x, cy, 9, 0, Math.PI * 2);
	ctx2d.fillStyle = cssVar("--chart-crosshair", "#dc3545");
	ctx2d.fill();
	ctx2d.lineWidth = 2;
	ctx2d.strokeStyle = cssVar("--chart-dot-ring", "#ffffff");
	ctx2d.stroke();
	// grip lines
	ctx2d.beginPath();
	ctx2d.moveTo(x - 2.5, cy - 3.5); ctx2d.lineTo(x - 2.5, cy + 3.5);
	ctx2d.moveTo(x + 2.5, cy - 3.5); ctx2d.lineTo(x + 2.5, cy + 3.5);
	ctx2d.lineWidth = 1.5;
	ctx2d.stroke();
}

interface OverlayState {
	canvas: HTMLCanvasElement;
	ctx: CanvasRenderingContext2D;
	currentIndex: number;
	resizeObserver: ResizeObserver | null;
	/** Crosshair x of the last draw, in CSS pixels; null while no crosshair is shown. */
	lastPixelX: number | null;
	/** The crosshair's x VALUE (axis units), kept so a playhead-only repaint does not erase it. */
	crosshairValue: number | null;
	/** A replay position, in axis units. Drawn in the same pass as the crosshair — see setOverlayPlayhead. */
	playhead: number | null;
	/** The leader's rule while someone else is followed. Same space as `playhead`. */
	leadLine: number | null;
}

const _overlays = new WeakMap<object, OverlayState>();

function _getOverlay(chart: ChartInstance): OverlayState | null {
	const existing = _overlays.get(chart);
	if (existing) return existing;

	const parent = chart.canvas.parentElement;
	if (!parent) return null;

	const parentPos = getComputedStyle(parent).position;
	if (parentPos === "static") parent.style.position = "relative";

	const overlay = document.createElement("canvas");
	overlay.style.position = "absolute";
	overlay.style.top = "0";
	overlay.style.left = "0";
	overlay.style.width = "100%";
	overlay.style.height = "100%";
	overlay.style.pointerEvents = "none";
	overlay.style.zIndex = "5";
	overlay.style.willChange = "transform";
	overlay.width = chart.canvas.width;
	overlay.height = chart.canvas.height;
	parent.appendChild(overlay);

	const ctx2d = overlay.getContext("2d");
	if (!ctx2d) return null;

	// The overlay canvas has device-pixel dimensions (chart.canvas.width = CSS width × DPR).
	// Chart.js's getPixelForValue() and chartArea coordinates are in logical (CSS) pixels —
	// the same space Chart.js uses after applying ctx.scale(DPR, DPR) on its own canvas.
	// We apply the same scale here so drawing coordinates land at the correct position on
	// high-DPR screens (mobile). Without this, bars appear at 1/DPR of the correct x position.
	const dpr = window.devicePixelRatio || 1;
	ctx2d.scale(dpr, dpr);

	// Declared before the ResizeObserver so its (async) callback can invalidate the drawn state.
	let state: OverlayState | null = null;

	let ro: ResizeObserver | null = null;
	if (typeof ResizeObserver !== "undefined") {
		ro = new ResizeObserver(() => {
			overlay.width = chart.canvas.width;
			overlay.height = chart.canvas.height;
			// Setting canvas dimensions resets the context transform — re-apply the DPR scale.
			const curDpr = window.devicePixelRatio || 1;
			ctx2d.scale(curDpr, curDpr);
			// The resize wiped every drawn pixel — invalidate the cached index/crosshair-x so a
			// re-highlight at the same index doesn't early-return and a stale grab-handle x isn't
			// used by touch-pan yield (CG-04).
			if (state) { state.currentIndex = -1; state.lastPixelX = null; }
			// The wipe took the playhead and its lead line with it, and unlike the crosshair nothing
			// re-asserts those on the next pointer move — a paused replay would simply lose them.
			if (state && (state.playhead != null || state.leadLine != null)) _scheduleOverlayRules(chart);
		});
		ro.observe(chart.canvas);
	}

	state = {
		canvas: overlay, ctx: ctx2d, currentIndex: -1, resizeObserver: ro, lastPixelX: null,
		crosshairValue: null, playhead: null, leadLine: null,
	};
	_overlays.set(chart, state);
	return state;
}

// Clear the ENTIRE overlay canvas regardless of the transform on its ctx. The ctx is
// permanently DPR-scaled (see _getOverlay: ctx.scale(dpr,dpr)), so clearing with the
// device-pixel width/height in that scaled space clears a dpr× region — harmless overdraw,
// but it mixed logical and device coordinate spaces. Reset the transform, clear the physical
// canvas, restore the DPR scale so subsequent draws stay in logical (CSS-px) space.
function clearOverlayCanvas(overlay: OverlayState): void {
	const ctx2d = overlay.ctx;
	ctx2d.save();
	ctx2d.setTransform(1, 0, 0, 1, 0, 0);
	ctx2d.clearRect(0, 0, overlay.canvas.width, overlay.canvas.height);
	ctx2d.restore();
}

function _drawOverlay(chart: ChartInstance, overlay: OverlayState, index: number): void {
	const area = chart.chartArea;
	if (!area) return;

	const ctx2d = overlay.ctx;

	clearOverlayCanvas(overlay);
	overlay.lastPixelX = null;

	// Use full (un-decimated) arrays for correct index mapping
	const labels = _chartLabels.get(chart);
	const fullXY = _chartFullXY.get(chart);
	if (!labels || !fullXY || index >= labels.length) return;

	const xScale = chart.scales.x;
	const yScale = chart.scales.y;
	if (!xScale || !yScale) return;

	const xVal = labels[index];
	if (xVal === undefined) return; // dead: index is produced in-bounds of the lockstep labels
	const x = xScale.getPixelForValue(xVal);
	if (x < area.left || x > area.right) return;

	overlay.lastPixelX = x;
	ctx2d.save();
	strokeCrosshairLine(ctx2d, x, area.top, area.bottom);
	if (_COARSE_POINTER) drawTouchHandle(ctx2d, x, area.bottom);

	// Draw dot at the data value from the full-resolution array
	const yVal = fullXY[index]?.y;
	if (yVal != null) {
		const y = yScale.getPixelForValue(yVal);
		if (y >= area.top && y <= area.bottom) {
			const ds = chart.data.datasets[0] as { borderColor?: string };
			const dotColor = ds.borderColor ? String(ds.borderColor) : cssVar("--chart-dot", "#007bff");
			ctx2d.beginPath();
			ctx2d.arc(x, y, 5, 0, Math.PI * 2);
			ctx2d.fillStyle = dotColor;
			ctx2d.fill();
			ctx2d.lineWidth = 2;
			ctx2d.strokeStyle = cssVar("--chart-dot-ring", "#ffffff");
			ctx2d.stroke();
		}
	}
	ctx2d.restore();
}

const _pendingOverlayDraws = new Map<ChartInstance, number>();
let _overlayRafId = 0;

/** Apply an overlay draw immediately (synchronous). */
function _applyOverlayDraw(c: ChartInstance, idx: number): void {
	const ov = _getOverlay(c);
	if (!ov) return;
	ov.currentIndex = idx;
	if (idx < 0) {
		clearOverlayCanvas(ov);
		ov.lastPixelX = null;
	} else {
		_drawOverlay(c, ov, idx);
	}
}

function _scheduleOverlayDraw(chart: ChartInstance, index: number): void {
	_pendingOverlayDraws.set(chart, index);
	if (!_overlayRafId) {
		_overlayRafId = requestAnimationFrame(() => {
			_overlayRafId = 0;
			_pendingOverlayDraws.forEach((idx, c) => {
				try {
					_applyOverlayDraw(c, idx);
				} catch (e) { reportChartError("chart-overlay:overlayDraw", e); }
			});
			_pendingOverlayDraws.clear();
		});
	}
}

/**
 * Highlight a data-point on a chart via the overlay canvas.
 * When `immediate` is true the draw happens synchronously (use this
 * when the caller is already inside a rAF to avoid a 1-frame delay).
 * Otherwise the draw is batched into the next animation frame.
 */
export function highlightChartIndex(chart: ChartInstance | null, index: number, immediate?: boolean): void {
	if (!chart) return;
	try {
		const overlay = _overlays.get(chart);
		if (overlay && overlay.currentIndex === index) return;
		if (immediate) {
			_pendingOverlayDraws.delete(chart);
			_applyOverlayDraw(chart, index);
		} else {
			_scheduleOverlayDraw(chart, index);
		}
	} catch (e) { reportChartError("chart-overlay:highlight", e); }
}

/**
 * Clear highlight from a chart.
 */
export function clearChartHighlight(chart: ChartInstance | null, immediate?: boolean): void {
	if (!chart) return;
	try {
		const overlay = _overlays.get(chart);
		if (overlay && overlay.currentIndex === -1) return;
		if (immediate) {
			_pendingOverlayDraws.delete(chart);
			_applyOverlayDraw(chart, -1);
		} else {
			_scheduleOverlayDraw(chart, -1);
		}
	} catch (e) { reportChartError("chart-overlay:clearHighlight", e); }
}

// ── Vertical rules on the overlay canvas (multi-series charts) ──
// The segment-comparison charts draw many effort lines at once, so a single
// highlighted data point (the dot drawn by highlightChartIndex) is meaningless —
// they only ever need a bare vertical line at the hovered x. Drawing it on the
// transparent overlay means the main Chart.js canvas is never repainted on hover.
// rAF-batched so the hovered chart and its synced siblings coalesce into a single frame.
//
// The overlay carries MORE than the crosshair now: a replay playhead and its lead line (the race
// speed chart). They share one pass because the pass clears the whole canvas — two independent
// writers would erase each other, which is why the crosshair's own value is persisted on the state
// rather than living only in the pending map.
const _pendingCrosshairs = new Set<ChartInstance>();
let _crosshairRafId = 0;

// The SAME two custom properties (and fallbacks) the canvas strips resolve — colour follows the entity,
// not the chart it happens to be drawn on, and a race shows its playhead on both surfaces at once.
const _playheadColor = (): string => cssVar("--profile-playhead", "rgba(249, 115, 22, 0.75)");
const _leadLineColor = (): string => cssVar("--profile-lead-line", "rgba(250, 204, 21, 0.6)");

/** Pixel x for an axis value, or null when it is off the visible plot. */
function _pixelInArea(chart: ChartInstance, xValue: number | null): number | null {
	if (xValue == null) return null;
	const area = chart.chartArea;
	const xScale = chart.scales.x;
	if (!area || !xScale) return null;
	const x = xScale.getPixelForValue(xValue);
	if (!Number.isFinite(x) || x < area.left || x > area.right) return null;
	return x;
}

function _paintOverlayRules(chart: ChartInstance): void {
	const ov = _getOverlay(chart);
	if (!ov) return;
	const ctx2d = ov.ctx;
	clearOverlayCanvas(ov);
	ov.lastPixelX = null;
	const area = chart.chartArea;
	if (!area) return;

	// Under the crosshair: the crosshair is what the pointer is asking about, so it stays topmost.
	const leadX = _pixelInArea(chart, ov.leadLine);
	if (leadX != null) {
		strokeCrosshairLine(ctx2d, leadX, area.top, area.bottom, { color: _leadLineColor(), width: 1.5, dash: [4, 4] });
	}
	const playX = _pixelInArea(chart, ov.playhead);
	if (playX != null) {
		strokeCrosshairLine(ctx2d, playX, area.top, area.bottom, { color: _playheadColor(), width: 2 });
	}

	const x = _pixelInArea(chart, ov.crosshairValue);
	if (x == null) return;
	// lastPixelX is the CROSSHAIR's x and nothing else: chart-zoom-pan's touch yield hit-tests the
	// grab-handle with it, and a playhead x there would make the handle claim drags it does not own.
	ov.lastPixelX = x;
	ctx2d.save();
	strokeCrosshairLine(ctx2d, x, area.top, area.bottom);
	if (_COARSE_POINTER) drawTouchHandle(ctx2d, x, area.bottom);
	ctx2d.restore();
}

function _scheduleOverlayRules(chart: ChartInstance): void {
	_pendingCrosshairs.add(chart);
	if (!_crosshairRafId) {
		_crosshairRafId = requestAnimationFrame(() => {
			_crosshairRafId = 0;
			_pendingCrosshairs.forEach((c) => {
				try { _paintOverlayRules(c); } catch (e) { reportChartError("chart-overlay:crosshair", e); }
			});
			_pendingCrosshairs.clear();
		});
	}
}

function _scheduleCrosshair(chart: ChartInstance, xValue: number | null): void {
	const ov = _getOverlay(chart);
	if (!ov) return;
	ov.crosshairValue = xValue;
	_scheduleOverlayRules(chart);
}

export function drawVerticalCrosshair(chart: ChartInstance | null, xValue: number): void {
	if (!chart) return;
	try { _scheduleCrosshair(chart, xValue); } catch (e) { reportChartError("chart-overlay:drawCrosshair", e); }
}

export function clearVerticalCrosshair(chart: ChartInstance | null): void {
	if (!chart) return;
	try { _scheduleCrosshair(chart, null); } catch (e) { reportChartError("chart-overlay:clearCrosshair", e); }
}

/**
 * The replay playhead and (optionally) the leader's rule, in the x-axis's own units. Null clears one.
 *
 * On the OVERLAY, not in a plugin, and that is the whole point: a race chart carries a dozen rider
 * lines, so re-rendering it once per replay frame is the flicker/CPU class the crosshair was moved off
 * the Chart.js event cycle to avoid. This repaints one transparent canvas instead, rAF-batched.
 */
export function setOverlayPlayhead(
	chart: ChartInstance | null,
	playhead: number | null,
	leadLine: number | null = null,
): void {
	if (!chart) return;
	try {
		const ov = _getOverlay(chart);
		if (!ov) return;
		if (ov.playhead === playhead && ov.leadLine === leadLine) return;
		ov.playhead = playhead;
		ov.leadLine = leadLine;
		_scheduleOverlayRules(chart);
	} catch (e) { reportChartError("chart-overlay:setPlayhead", e); }
}

/**
 * Crosshair x of the chart's last overlay draw, in CSS pixels relative to the canvas —
 * null while no crosshair (or highlight dot) is shown. Touch pan (chart-zoom-pan.ts)
 * uses it to yield 1-finger drags that start on the crosshair grab-handle, so the
 * scrub handlers can move the bar while the chart is zoomed.
 */
export function getCrosshairPixelX(chart: ChartInstance | null): number | null {
	if (!chart) return null;
	return _overlays.get(chart)?.lastPixelX ?? null;
}

/**
 * Tear down a chart's overlay state: the appended overlay canvas, its ResizeObserver,
 * the split-highlight div, and any pending rAF draw entries. MUST be called right before
 * chart.destroy() whenever a chart is rebuilt on the same canvas — the ResizeObserver
 * observes the (persistent) chart canvas, so its closure otherwise keeps the destroyed
 * chart instance and every appended overlay canvas alive across rebuilds.
 */
export function destroyChartOverlay(chart: ChartInstance | null): void {
	if (!chart) return;
	_pendingOverlayDraws.delete(chart);
	_pendingCrosshairs.delete(chart);
	const ov = _overlays.get(chart);
	if (ov) {
		ov.resizeObserver?.disconnect();
		ov.canvas.remove();
		_overlays.delete(chart);
	}
	const split = _chartSplitHighlights.get(chart);
	if (split) {
		split.remove();
		_chartSplitHighlights.delete(chart);
	}
	_chartLabels.delete(chart);
	_chartFullXY.delete(chart);
}

/**
 * Show a gold overlay on a chart between two data indices (split/segment).
 */
export function showSplitOverlay(
	chart: ChartInstance | null,
	startIndex: number,
	endIndex: number,
): void {
	if (!chart) return;
	const labels = _chartLabels.get(chart) || (chart.data.labels as number[] | undefined);
	if (!labels || startIndex < 0 || endIndex >= labels.length) return;
	if (startIndex > endIndex) return; // CG-05: a reversed range would render a zero-width overlay

	let overlay = _chartSplitHighlights.get(chart);
	if (!overlay) {
		const parent = chart.canvas.parentElement;
		if (!parent) return; // CG-05: canvas detached (chart torn down mid-call) — no-op, don't throw
		overlay = document.createElement("div");
		Object.assign(overlay.style, {
			position: "absolute",
			backgroundColor: "rgba(255, 215, 0, 0.2)",
			pointerEvents: "none",
			zIndex: "10",
			display: "none",
			top: "0",
			bottom: "0",
		});
		parent.appendChild(overlay);
		_chartSplitHighlights.set(chart, overlay);
	}
	const chartArea = chart.chartArea;
	if (!chartArea) {
		overlay.style.display = "none";
		return;
	}

	const startDist = labels[startIndex] as number;
	const endDist = labels[endIndex] as number;
	const xScale = must(chart.scales.x, 'x scale configured');
	const curMin = xScale.min;
	const curMax = xScale.max;

	if (endDist < curMin || startDist > curMax) {
		overlay.style.display = "none";
		return;
	}

	const x1 = xScale.getPixelForValue(Math.max(startDist, curMin));
	const x2 = xScale.getPixelForValue(Math.min(endDist, curMax));

	overlay.style.left = x1 + "px";
	overlay.style.width = Math.max(0, x2 - x1) + "px";
	overlay.style.display = "block";
}

/**
 * Hide the split overlay on a chart.
 */
export function clearSplitOverlay(chart: ChartInstance | null): void {
	if (!chart) return;
	const highlight = _chartSplitHighlights.get(chart);
	if (highlight) {
		highlight.style.display = "none";
	}
}
