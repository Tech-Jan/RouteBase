// statistics-hover.ts — hover highlight for the Statistics cumulative chart.
//
// Two cooperating pieces, kept out of statistics.ts to keep that file focused:
//   • crosshairPlugin — draws a dashed vertical guide at the hovered x plus a ring dot on every
//     visible series (canvas, so hex/rgba colours rather than the oklch CSS tokens).
//   • makeReadoutExternal — a Chart.js external-tooltip handler that renders the floating HTML
//     value box (one row per series). The on-canvas tooltip stays disabled.
//
// Depends on: Chart (CDN global).

import { ChartReadoutBox, type ReadoutRow, type ReadoutInteractive } from "../core/chart-readout-box";
import { must } from '../core/invariant';
import { datasetStyle, tooltipActiveElements } from '../core/chart-internals';
import { strokeCrosshairLine } from "../core/chart-overlay";
import { cssVar } from "../core/css-vars";

type ChartInstance = InstanceType<typeof Chart>;

// Fallbacks approximate --line-2 (guide) and --bg-2 (dot centre) from cockpit-tokens.css; read
// through cssVar so a theme actually reaches the canvas draw (item 3 — no hard-coded literals).

// Apply an alpha to a "#rrggbb" or "rgb()/rgba()" colour (line colours can be either).
function withAlpha(c: string, a: number): string {
	if (c.startsWith("#")) {
		const n = parseInt(c.slice(1), 16);
		return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${a})`;
	}
	const m = c.match(/rgba?\(([^)]+)\)/);
	if (m && m[1] !== undefined) {
		const [r, g, b] = m[1].split(",").map((s) => parseFloat(s));
		// Malformed color → keep the original string (was a literal "rgba(undefined,…)").
		if (r !== undefined && g !== undefined && b !== undefined) return `rgba(${r},${g},${b},${a})`;
	}
	return c;
}

// Scriptable vertical gradient (colour → transparent) for a filled line's area — keeps the line colour.
export function areaGradient(color: string) {
	return (ctx: import("chart.js").ScriptableContext<"line">): string | CanvasGradient => {
		const area = ctx.chart.chartArea;
		if (!area) return "transparent";
		const g = ctx.chart.ctx.createLinearGradient(0, area.top, 0, area.bottom);
		g.addColorStop(0, withAlpha(color, 0.16));
		g.addColorStop(1, withAlpha(color, 0));
		return g;
	};
}

// Apply the current selection to a live chart (no rebuild, so zoom is preserved): the selected series
// (matched by the dataset's `_seriesLabel`) gets an area fill; others fall back to their `_defaultFill`.
export function applySelectionFill(chart: ChartInstance, selectedLabel: string | null): void {
	chart.data.datasets.forEach((ds) => {
		const d = datasetStyle(ds);
		const selected = selectedLabel != null && d._seriesLabel === selectedLabel;
		const fillIt = selectedLabel != null ? selected : !!d._defaultFill;
		d.fill = fillIt ? "origin" : false;
		d.backgroundColor = fillIt ? areaGradient(d.borderColor) : d.borderColor;
		if (d._baseWidth != null) d.borderWidth = selected ? d._baseWidth + 1.5 : d._baseWidth;
	});
	chart.update("none");
}

export const crosshairPlugin: import("chart.js").Plugin<"line"> = {
	id: "cumCrosshair",
	afterDatasetsDraw(chart: ChartInstance) {
		const active = tooltipActiveElements(chart);
		if (!active.length) return;
		const ctx = chart.ctx;
		const area = chart.chartArea;
		const x = must(active[0], 'non-empty active elements (guarded)').element.x;
		ctx.save();
		// Statistics uses the same crosshair kernel as every other chart, styled as its dashed grey
		// guide (a documented option on the shared stroke, not a second implementation — item 2).
		strokeCrosshairLine(ctx, x, area.top, area.bottom, {
			color: cssVar("--line-2", "rgba(148,163,184,0.45)"),
			width: 1,
			dash: [3, 3],
		});
		const dotFill = cssVar("--bg-2", "#1b2027");
		for (const a of active) {
			if (!Number.isFinite(a.element.y)) continue; // null gap (series ended before this x)
			ctx.beginPath();
			ctx.arc(a.element.x, a.element.y, 3.5, 0, Math.PI * 2);
			ctx.fillStyle = dotFill;
			ctx.fill();
			ctx.lineWidth = 2;
			ctx.strokeStyle = must(chart.data.datasets[a.datasetIndex], 'active element datasetIndex in datasets').borderColor as string;
			ctx.stroke();
		}
		ctx.restore();
	},
};

// The floating value box lives inside the chart wrap (which is position:relative); created lazily via
// the shared ChartReadoutBox (core/chart-readout-box.ts).
let _readoutBox: ChartReadoutBox | null = null;
function getReadoutBox(): ChartReadoutBox | null {
	if (!_readoutBox) {
		const wrap = document.getElementById("cumulative-chart-wrap");
		if (!wrap) return null;
		_readoutBox = new ChartReadoutBox(wrap);
	}
	return _readoutBox;
}

export function hideReadout(): void {
	const box = getReadoutBox();
	if (box) box.hide();
}

// Enable hover/click on the readout rows (sets .is-interactive + wires delegated listeners).
export function setReadoutInteractive(opts: ReadoutInteractive): void {
	const box = getReadoutBox();
	if (box) box.setInteractive(opts);
}

// Series whose row is currently hovered in the box (transient emphasis), and the latest external-tooltip
// context + handler — so a box-row hover can repaint the readout without a fresh chart mouse event.
let _hoveredLabel: string | null = null;
let _activeReadout: ReadoutExternal | null = null;
let _lastReadoutCtx: { chart: ChartInstance; tooltip: import("chart.js").TooltipModel<"line"> } | null = null;

export function setStatHoveredLabel(label: string | null): void {
	_hoveredLabel = label;
}

/** Repaint the readout box from the last tooltip context (emphasis reflects current selection/hover). */
export function repaintReadout(): void {
	if (_activeReadout && _lastReadoutCtx) _activeReadout(_lastReadoutCtx);
}

// One readout cell. `key` groups a series' metrics into a single row; `label` is the row's unique
// identity (used for emphasis matching); `display` is the text actually shown (e.g. just the year —
// the swatch colour already conveys the type); `rank` orders the metric columns; `num`+`unit` are the
// value shown in that column; `numWidthCh` fixes the number column width (in ch) so it never reflows.
export interface ReadoutCell { key: string; label: string; display: string; rank: number; num: string; unit: string; numWidthCh?: number | undefined; }

// The handler can be called directly (e.g. right after a click) as well as assigned to tooltip.external.
export type ReadoutExternal = (ctx: { chart: ChartInstance; tooltip: import("chart.js").TooltipModel<"line"> }) => void;

// Builds the external-tooltip handler. `describe(datasetIndex, y)` returns the cell parts above;
// `getSelectedLabel()` is the pinned (clicked) series. Emphasis is the pinned series, else the series
// whose row is currently hovered in the box — no cursor-line proximity detection. Cells pivot into one
// row per series; each row carries its series label as the row key so box hover/click can target it.
export function makeReadoutExternal(
	isYoy: boolean,
	describe: (datasetIndex: number, y: number) => ReadoutCell,
	getSelectedLabel: () => string | null,
): ReadoutExternal {
	const handler: ReadoutExternal = (context): void => {
		const box = getReadoutBox();
		if (!box) return;
		_activeReadout = handler;
		_lastReadoutCtx = context;
		const tooltip = context.tooltip;
		// Skip series with no value at this x (line ended / not started).
		const points = (tooltip.dataPoints ?? []).filter((dp) => Number.isFinite(dp.parsed.y));
		// Sticky hide: the box may overlap the chart, so defer hiding so the cursor can reach a row.
		if (tooltip.opacity === 0 || !points.length) { box.scheduleHide(); return; }

		// Bold the pinned series, else the series whose row is hovered in the box.
		const emphLabel = getSelectedLabel() ?? _hoveredLabel;

		// Pivot: one row per series (first-seen order), the series' metrics as columns (rank order).
		type Row = ReadoutRow & { _ranks: number[] };
		const rowMap = new Map<string, Row>();
		const order: string[] = [];
		for (const dp of points) {
			const c = describe(dp.datasetIndex, dp.parsed.y as number);
			let row = rowMap.get(c.key);
			if (!row) {
				row = { label: c.display, rowKey: c.label, color: must(context.chart.data.datasets[dp.datasetIndex], 'tooltip point datasetIndex in datasets').borderColor as string, emphasis: emphLabel != null && c.label === emphLabel, cells: [], _ranks: [] };
				rowMap.set(c.key, row);
				order.push(c.key);
			}
			row._ranks.push(c.rank);
			row.cells.push({ num: c.num, unit: c.unit, numWidthCh: c.numWidthCh });
		}

		// Sort each row's metric columns by rank.
		const rows: ReadoutRow[] = order.map((key) => {
			const row = must(rowMap.get(key), 'key from order (inserted together)');
			const idx = row.cells.map((_, i) => i).sort((a, b) => must(row._ranks[a], '_ranks lockstep with cells') - must(row._ranks[b], '_ranks lockstep with cells'));
			return { label: row.label, rowKey: row.rowKey, color: row.color, emphasis: row.emphasis, cells: idx.map((i) => must(row.cells[i], 'idx permutes cells indices')) };
		});

		// parsed.x is a UTC-midnight epoch of the server's calendar date (Date.UTC in the chart store) — format
		// in UTC so a west-of-UTC viewer doesn't see the label shift back a day (statistics-chart-store L104-106).
		const d = new Date(must(points[0], 'non-empty points (guarded at entry)').parsed.x as number);
		const dateStr = d.toLocaleDateString(undefined, isYoy
			? { month: "short", day: "numeric", timeZone: "UTC" }
			: { year: "numeric", month: "short", day: "numeric", timeZone: "UTC" });

		// The box floats centered on the crosshair, just above the plot — see ChartReadoutBox.
		box.show(dateStr, rows, tooltip.caretX, context.chart.chartArea.top);
	};
	return handler;
}
