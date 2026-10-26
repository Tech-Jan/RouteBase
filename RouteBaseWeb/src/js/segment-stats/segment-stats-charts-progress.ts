/**
 * Segment Statistics — Progress Over Time chart module
 *
 * Extracted from segment-stats-charts.ts to keep that file focused on
 * the perf/elevation/comparison charts and their zoom sync.
 *
 * The render body is an exported function (renderProgressChart) so its render
 * path is unit-testable; the top-level block only wires the ctx callback. The
 * first paint is driven by finishChartInit in the charts module — as before,
 * this module does NOT paint at import time.
 *
 * Depends on: ctx (shared context from segment-statistics.ts)
 */
import type { EffortDateData } from './segment-stats-effort-types';
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import { ctx } from './segment-statistics';
import { strokeCrosshairLine } from '../core/chart-overlay';
import { setChartEmptyState } from '../core/chart-empty-state';
import { formatDateDMY } from '../core/grid/grid-format';
import { must } from '../core/invariant';

// Mouseleave handler ref (one progress chart per page).
const _progLeave: { current: EventListener | null } = { current: null };

function setMouseLeave(canvas: HTMLElement, handlerRef: { current: EventListener | null }, fn: EventListener): void {
	if (handlerRef.current) {
		canvas.removeEventListener("mouseleave", handlerRef.current);
	}
	handlerRef.current = fn;
	canvas.addEventListener("mouseleave", fn);
}

const verticalLinePlugin = {
	id: "segStatProgressVertLine",
	afterDatasetsDraw: (chart: InstanceType<typeof Chart>) => {
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const active = (chart.tooltip as any)?._active?.length ? (chart.tooltip as any)._active[0] : null;
		if (active) {
			// Shared crosshair kernel — default style (themed --chart-crosshair, width 2), replacing
			// the hard-coded #dc3545 line this plugin used to re-draw by hand (items 2 + 3).
			strokeCrosshairLine(chart.ctx, active.element.x, chart.chartArea.top, chart.chartArea.bottom);
		}
	},
};

// Y-axis metric configuration
const progressYMetricConfig: Record<string, {
	label: string;
	unit: string;
	reverse: boolean;
	getValue(e: EffortDateData): number | null | undefined;
	formatTick(v: string | number): string;
	formatTooltip(v: number | null | undefined): string;
}> = {
	time: {
		label: "Segment Time",
		unit: "",
		reverse: true,
		getValue: (e: EffortDateData) => e.time,
		formatTick: (v: string | number) => {
			if (typeof v !== "number") return v;
			const h = Math.floor(v / 3600);
			const m = Math.floor((v % 3600) / 60);
			const s = Math.floor(v % 60);
			if (h > 0)
				return (
					h +
					":" +
					m.toString().padStart(2, "0") +
					":" +
					s.toString().padStart(2, "0")
				);
			return m + ":" + s.toString().padStart(2, "0");
		},
		formatTooltip: (v: number) => {
			const h = Math.floor(v / 3600);
			const m = Math.floor((v % 3600) / 60);
			const s = Math.floor(v % 60);
			const t =
				h > 0
					? h +
						":" +
						m.toString().padStart(2, "0") +
						":" +
						s.toString().padStart(2, "0")
					: m + ":" + s.toString().padStart(2, "0");
			return "Time: " + t;
		},
	},
	speed: {
		label: "Avg Speed (km/h)",
		unit: "km/h",
		reverse: false,
		getValue: (e: EffortDateData) => e.speed || 0,
		formatTick: (v: string | number) => typeof v === "number" ? v.toFixed(1) + " km/h" : v,
		formatTooltip: (v: number) => "Speed: " + v.toFixed(1) + " km/h",
	},
	pace: {
		label: "Avg Pace (min/km)",
		unit: "min/km",
		reverse: true,
		getValue: (e: EffortDateData) => (e.speed && e.speed > 0 ? 60 / e.speed : null),
		formatTick: (v: string | number) => {
			if (typeof v !== "number") return v;
			const m = Math.floor(v);
			const s = Math.round((v - m) * 60);
			return m + ":" + s.toString().padStart(2, "0") + " /km";
		},
		formatTooltip: (v: number) => {
			const m = Math.floor(v);
			const s = Math.round((v - m) * 60);
			return "Pace: " + m + ":" + s.toString().padStart(2, "0") + " /km";
		},
	},
	heartRate: {
		label: "Avg Heart Rate (bpm)",
		unit: "bpm",
		reverse: false,
		getValue: (e: EffortDateData) => (e.heartRate && e.heartRate > 0 ? e.heartRate : null),
		formatTick: (v: string | number) => typeof v === "number" ? Math.round(v) + " bpm" : v,
		formatTooltip: (v: number) => "HR: " + Math.round(v) + " bpm",
	},
	power: {
		label: "Avg Power (W)",
		unit: "W",
		reverse: false,
		getValue: (e: EffortDateData) => (e.power && e.power > 0 ? e.power : null),
		formatTick: (v: string | number) => typeof v === "number" ? Math.round(v) + " W" : v,
		formatTooltip: (v: number) => "Power: " + Math.round(v) + " W",
	},
	elevation: {
		label: "Elevation Gain (m)",
		unit: "m",
		reverse: false,
		getValue: (e: EffortDateData) => e.elevation || 0,
		formatTick: (v: string | number) => typeof v === "number" ? Math.round(v) + " m" : v,
		formatTooltip: (v: number) => "Elevation: " + Math.round(v) + " m",
	},
};

/**
 * Render the progress-over-time scatter into #progressChart from `effortDates`.
 * Fewer than 2 efforts can't show a trend → shared empty state (not a blank canvas).
 */
export function renderProgressChart(ctx: SegmentStatisticsContext, effortDates: EffortDateData[]): void {
	const canvas = document.getElementById("progressChart") as HTMLCanvasElement | null;
	if (!canvas) return;
	const host = canvas.parentElement as HTMLElement | null;
	// One effort (or none) can't show a trend — show the shared empty state instead of leaving
	// a blank canvas (the silent-empty failure this chart had before item 4).
	if (effortDates.length < 2) {
		if (ctx.progressChart) { ctx.progressChart.destroy(); ctx.progressChart = null; }
		setChartEmptyState(host, "Need at least two efforts to chart progress over time.", true);
		return;
	}
	setChartEmptyState(host, "", false);
	if (ctx.progressChart) ctx.progressChart.destroy();

	const visible = ctx.getVisibleEffortIndices();
	const useEqualSpacing = ctx.progressXAxis === "equal";
	const metricKey = ctx.progressYMetric || "time";
	const metric = progressYMetricConfig[metricKey] || must(progressYMetricConfig.time, "'time' is a declared metric");

	const sorted = effortDates.map((e: EffortDateData, idx: number) => ({
		effort: e,
		origIdx: idx,
	}));
	sorted.sort((a, b) => (a.effort.date || "").localeCompare(b.effort.date || ""));

	// formatDateDMY, not the wire string verbatim: date is wall-clock ISO now (B3) and a raw echo
	// would put the full stamp on the equal-spacing tick labels.
	const labels = sorted.map((s) => formatDateDMY(s.effort.date));

	// O(1) lookups per effort (chronoPos was a linear scan nested in the map → O(n²) per rebuild).
	const chronoPosByOrigIdx = new Map<number, number>();
	sorted.forEach((s, pos) => chronoPosByOrigIdx.set(s.origIdx, pos));
	const visibleSet = new Set(visible);

	const datasets = effortDates.map((e: EffortDateData, idx: number) => {
		const color = ctx.effortColors[idx % ctx.effortColors.length];
		const yVal = metric.getValue(e);
		const hasValue = yVal !== null && yVal !== undefined && yVal > 0;

		let xVal: number | string | null;
		if (useEqualSpacing) {
			xVal = chronoPosByOrigIdx.get(idx) ?? -1;
		} else {
			xVal = e.date ?? null;
		}

		return {
			label: e.name,
			data: hasValue ? [{ x: xVal, y: yVal }] : [],
			backgroundColor: color,
			borderColor: color,
			pointRadius: visibleSet.has(idx) && hasValue ? 8 : 3,
			pointHoverRadius: 12,
			pointStyle: "circle",
			showLine: false,
			hidden: !visibleSet.has(idx) || !hasValue,
		};
	});

	// Connector line threading all visible points in chronological order
	const connectorPoints = sorted.flatMap((s, sortedIdx) => {
		if (!visibleSet.has(s.origIdx)) return [];
		const yVal = metric.getValue(s.effort);
		if (yVal === null || yVal === undefined || !(yVal > 0)) return [];
		const xVal = useEqualSpacing ? sortedIdx : (s.effort.date ?? null);
		return [{ x: xVal, y: yVal }];
	});
	if (connectorPoints.length >= 2) {
		datasets.push({
			label: "",
			data: connectorPoints,
			backgroundColor: "transparent",
			borderColor: "rgba(255,255,255,0.18)",
			borderWidth: 1,
			pointRadius: 0,
			pointHoverRadius: 0,
			pointStyle: "circle",
			showLine: true,
			hidden: false,
		} as unknown as typeof datasets[number]);
	}

	let xScaleConfig: Record<string, unknown>;
	if (useEqualSpacing) {
		xScaleConfig = {
			type: "linear",
			display: true,
			title: { display: true, text: "Date" },
			min: -0.5,
			max: sorted.length - 0.5,
			ticks: {
				stepSize: 1,
				callback: (v: string | number) => {
					if (typeof v !== "number") return v;
					const i = Math.round(v);
					return i >= 0 && i < labels.length ? labels[i] : "";
				},
			},
		};
	} else {
		xScaleConfig = {
			type: "time",
			display: true,
			title: { display: true, text: "Date" },
			time: {
				unit: "day",
				tooltipFormat: "MMM d, yyyy",
				displayFormats: { day: "MMM d" },
			},
		};
	}

	const chartTitle =
		metricKey === "time"
			? "Segment Time Over Time"
			: metricKey === "speed"
				? "Avg Speed Over Time"
				: metricKey === "pace"
					? "Avg Pace Over Time"
					: metricKey === "heartRate"
						? "Avg Heart Rate Over Time"
						: metricKey === "power"
							? "Avg Power Over Time"
							: "Elevation Gain Over Time";

	ctx.progressChart = new Chart(must(canvas.getContext("2d"), 'canvas yields a 2d context'), {
		type: "scatter",
		data: { datasets: datasets },
		options: {
			responsive: true,
			maintainAspectRatio: false,
			animation: false,
			interaction: { mode: "nearest", intersect: true },
			// SH-02: consume the shared ID-16-guarded helpers (segment-stats-hover.ts) instead of
			// re-implementing the unguarded setActiveElements shape a second time. _setChartActive /
			// _clearChartActive draw on change, matching this block's prior c.draw() behaviour.
			onHover: (_event: import("chart.js").ChartEvent, elements: import("chart.js").ActiveElement[]) => {
				const charts = [ctx.performanceChart, ctx.elevationChart, ctx.effortChart];
				if (elements.length > 0) {
					const dsIdx = must(elements[0], 'length > 0 (guarded above)').datasetIndex;
					for (const c of charts) {
						const ds = c ? c.data.datasets[dsIdx] : undefined;
						if (c && ds && !ds.hidden) {
							ctx._setChartActive?.(c, dsIdx, 0);
						}
					}
				} else {
					for (const c of charts) ctx._clearChartActive?.(c);
				}
			},
			plugins: {
				legend: { display: false },
				title: { display: true, text: chartTitle },
				tooltip: {
					animation: false,
					callbacks: {
						title: (tooltipCtx: import("chart.js").TooltipItem<"scatter">[]) => (must(tooltipCtx[0], 'tooltip has an item').dataset as { label?: string }).label ?? "",
						label: (tooltipCtx: import("chart.js").TooltipItem<"scatter">) => {
							const metricStr = metric.formatTooltip(tooltipCtx.parsed.y);
							const dateStr = effortDates[tooltipCtx.datasetIndex]?.date || "";
							return metricStr + " | Date: " + dateStr;
						},
					},
				},
			},
			scales: {
				x: xScaleConfig,
				y: {
					type: "linear",
					display: true,
					title: { display: true, text: metric.label },
					reverse: metric.reverse,
					ticks: { callback: metric.formatTick },
				},
			},
		},
		plugins: [verticalLinePlugin],
	});

	setMouseLeave(canvas, _progLeave, () => ctx.onChartLeave?.());
}

if (ctx && ctx.config.hasEfforts && ctx.config.effortDates) {
	const effortDates = ctx.config.effortDates;
	ctx._renderProgressChart = () => renderProgressChart(ctx, effortDates);
}
