/**
 * chart-line-select.ts — click-to-select a line on multi-series charts.
 *
 * chartAreaGradient builds a scriptable vertical fill (colour → transparent);
 * setLineSelection toggles a line's emphasis (fill + thicker stroke); enableLineSelect
 * wires a DOM click listener with a drag-guard so panning doesn't select.
 */
import { must } from './invariant';
import { datasetStyle, selectedLineIndex, setSelectedLineIndexInternal } from './chart-internals';

type ChartInstance = InstanceType<typeof Chart>;

/**
 * The same colour at a different alpha. Accepts `#rrggbb` or `rgb()/rgba()`; anything else is passed
 * through unchanged, so a CSS variable or a named colour degrades to itself rather than to garbage.
 */
export function withAlpha(color: string, alpha: number): string {
	if (color.startsWith("#")) {
		const n = parseInt(color.slice(1), 16);
		return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
	}
	const m = color.match(/rgba?\(([^)]+)\)/);
	if (m) {
		const p = must(m[1], 'capture group 1').split(",").map((s) => parseFloat(s));
		return `rgba(${p[0]},${p[1]},${p[2]},${alpha})`;
	}
	return color;
}

/**
 * Scriptable vertical gradient (colour → transparent) for a filled line's area — keeps the line colour.
 */
export function chartAreaGradient(color: string, topAlpha = 0.16) {
	return (ctx: { chart: ChartInstance }): string | CanvasGradient => {
		const area = ctx.chart.chartArea;
		if (!area) return "transparent";
		const g = ctx.chart.ctx.createLinearGradient(0, area.top, 0, area.bottom);
		g.addColorStop(0, withAlpha(color, topAlpha));
		g.addColorStop(1, withAlpha(color, 0));
		return g;
	};
}

/**
 * Highlight one line by index: fill its area + thicken its stroke; revert all others to their base.
 * `index === null` clears. Uses chart.update("none") so zoom/pan are preserved.
 */
export function setLineSelection(chart: ChartInstance | null, index: number | null): void {
	if (!chart) return;
	setSelectedLineIndexInternal(chart, index);
	chart.data.datasets.forEach((ds, i) => {
		const d = datasetStyle(ds);
		if (d._baseWidth == null) d._baseWidth = d.borderWidth;
		if (!("_baseBg" in d)) d._baseBg = d.backgroundColor;
		const sel = i === index;
		d.fill = sel ? "origin" : false;
		d.backgroundColor = sel ? chartAreaGradient(d.borderColor) : d._baseBg;
		d.borderWidth = sel ? (d._baseWidth ?? 2) + 1.5 : (d._baseWidth ?? 2);
	});
	chart.update("none");
}

/**
 * Click a line to toggle its selection (see setLineSelection). Attaches a DOM click listener (works
 * even when the chart restricts Chart.js `events`), with a drag-guard so panning doesn't select.
 * Resolves the target via Chart.js' own nearest-element hit-test.
 * `signal` — pass when the chart can be rebuilt on the same canvas, so re-wiring doesn't stack.
 */
export function enableLineSelect(chart: ChartInstance | null, signal?: AbortSignal): void {
	if (!chart) return;
	const canvas = chart.canvas;
	if (!canvas) return;
	let downX = 0, downY = 0;
	canvas.addEventListener("mousedown", (e: MouseEvent) => { downX = e.clientX; downY = e.clientY; }, signal ? { signal } : undefined);
	canvas.addEventListener("click", (e: MouseEvent) => {
		if (Math.hypot(e.clientX - downX, e.clientY - downY) > 6) return; // was a drag (pan), not a click
		const hit = chart.getElementsAtEventForMode(e, "nearest", { intersect: false }, true);
		const target = hit.length ? hit[0].datasetIndex : null;
		if (target == null) return;
		const cur = selectedLineIndex(chart);
		const next = cur === target ? null : target;
		setLineSelection(chart, next);
	}, signal ? { signal } : undefined); // must share the mousedown's signal, else a rebuild on the same canvas stacks click handlers
}
