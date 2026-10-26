/**
 * core/chart-internals.ts — the ONE home for Chart.js private-field and expando access (K4).
 *
 * Chart.js types its config surface, not its live internals; this app additionally hangs its own
 * expando state off chart/dataset objects (selection index, base style, series tags). Every such
 * reach-through lives here as a tiny typed accessor holding a single documented cast that names
 * the field it touches — call sites stay cast-free. Do not add a new `as unknown as` at a call
 * site for chart internals; extend this seam instead (the type-quality ratchet counts casts).
 */

import type { Chart, ChartEvent, LegendItem } from 'chart.js';

/* ── app expandos on the chart object ─────────────────────────────────────── */

/** The app's line-selection state, stored on the chart (`_selectedIndex` expando). */
export function selectedLineIndex(chart: object): number | null {
	// cast: _selectedIndex — this app's own expando slot on the chart instance
	return (chart as { _selectedIndex?: number | null })._selectedIndex ?? null;
}

export function setSelectedLineIndexInternal(chart: object, index: number | null): void {
	// cast: _selectedIndex — this app's own expando slot on the chart instance
	(chart as { _selectedIndex?: number | null })._selectedIndex = index;
}

/* ── dataset internals + style expandos ───────────────────────────────────── */

/** Live dataset style slots + the app's base-style/series expandos. */
export interface DatasetStyleInternals {
	borderColor: string;
	borderWidth: number;
	backgroundColor: unknown;
	fill: unknown;
	_baseWidth?: number | undefined;
	_baseBg?: unknown;
	_seriesLabel?: string | undefined;
	_defaultFill?: boolean | undefined;
}

export function datasetStyle(ds: object): DatasetStyleInternals {
	// cast: dataset style slots — Chart.js types datasets per-config, not per live instance;
	// _baseWidth/_baseBg/_seriesLabel/_defaultFill are this app's expandos
	return ds as DatasetStyleInternals;
}

/** A dataset's parsed points, for read-only sweeps (e.g. column-width measurement). */
export function datasetPoints(ds: { data: unknown }): { x?: number | undefined; y: number }[] {
	// cast: dataset data — typed by chart kind in config, structurally {x?, y} at runtime here
	return ds.data as { x?: number | undefined; y: number }[];
}

/* ── scale internals ──────────────────────────────────────────────────────── */

/** A scale's live window + writable options — Chart.js ships `Scale` without these publicly typed. */
export interface ScaleInternals {
	axis?: string | undefined;
	min?: number | undefined;
	max?: number | undefined;
	bottom?: number | undefined;
	getPixelForValue?: ((v: number) => number) | undefined;
	/** The inverse — a pointer x back to an axis value. Undefined until the scale has been laid out. */
	getValueForPixel?: ((p: number) => number | undefined) | undefined;
	options: {
		min?: number | undefined;
		max?: number | undefined;
		beginAtZero?: boolean | undefined;
		ticks?: Record<string, unknown> | undefined;
		[k: string]: unknown;
	};
}

export function scaleInternals(scale: object): ScaleInternals {
	// cast: scale live window (axis/min/max/bottom) + mutable options bag
	return scale as ScaleInternals;
}

/* ── chart internals ──────────────────────────────────────────────────────── */

/** Chart.js' internal render-time DPR (differs from window DPR while printing/zooming). */
export function chartDevicePixelRatio(chart: object): number | undefined {
	// cast: currentDevicePixelRatio — Chart.js-internal render state
	return (chart as { currentDevicePixelRatio?: number }).currentDevicePixelRatio;
}

/** meta.hidden ?? dataset.hidden, exactly as the renderer resolves visibility (SC2-04). */
export function isDatasetVisible(chart: object, index: number): boolean {
	// cast: isDatasetVisible — public API, absent from the app's minimal structural chart types
	return (chart as { isDatasetVisible(i: number): boolean }).isDatasetVisible(index);
}

/** The rendered x pixel of one dataset element, if the meta has it. */
export function elementXAt(chart: object, datasetIndex: number, pointIndex: number): number | undefined {
	// cast: dataset meta elements — Chart.js types meta.data as Element[], x is runtime state
	const meta = (chart as { getDatasetMeta(i: number): { data: Array<{ x?: number } | undefined> } })
		.getDatasetMeta(datasetIndex);
	return meta.data[pointIndex]?.x;
}

/** The tooltip's active elements (crosshair anchor); [] when none/absent. */
export function tooltipActiveElements(chart: object): { datasetIndex: number; element: { x: number; y: number } }[] {
	// cast: chart.tooltip.getActiveElements — not in the app's structural chart types
	const tt = (chart as { tooltip?: { getActiveElements?: () => { datasetIndex: number; element: { x: number; y: number } }[] } }).tooltip;
	return tt?.getActiveElements?.() ?? [];
}

/** Bridge a structural chart handle to the nominal chart.js Chart (factory helpers want the class). */
export function asChart(chart: object): Chart {
	// cast: nominal bridge only — no field access; the structural handle IS the live Chart
	return chart as Chart;
}

/* ── synthetic events + legend group tags ─────────────────────────────────── */

/**
 * A normalized ChartEvent for hand-driven hover paths (touch scrub, pointer relay). The `native`
 * key must be PRESENT (may be null) so Chart.js' getRelativePosition reads x/y directly.
 */
export function syntheticChartEvent(x: number, y: number, native: Event | null): ChartEvent {
	// cast: ChartEvent — synthesized structurally; Chart.js only reads type/x/y/native
	return { type: 'mousemove', x, y, native } as unknown as ChartEvent;
}

/** Build a legend item carrying the app's `_groupId` tag (grouped-series legends). */
export function makeGroupLegendItem(item: Record<string, unknown>, groupId: string): LegendItem {
	// cast: LegendItem + _groupId expando — the tag rides through generateLabels → onClick
	return { ...item, _groupId: groupId } as unknown as LegendItem;
}

/** Read the `_groupId` tag back off a legend item. */
export function legendItemGroupId(item: object): string {
	// cast: _groupId — this app's expando on the legend item (set by makeGroupLegendItem)
	return (item as { _groupId: string })._groupId;
}
