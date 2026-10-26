// statistics-bars.ts — bar-chart mode for the Statistics cumulative panel.
//
// The panel's default chart is a cumulative *line* (statistics.ts). This module renders the alternate
// *bar* view: per-period (weekly / monthly) stacked volume — the classic training-volume chart. The
// stacked series are whatever the caller passes: activity types (all-time / custom) or calendar years
// overlaid on a reference year (year-over-year, via xIsRefYear). It re-buckets the same per-day points
// the line chart already has (no refetch), so it stays fully decoupled from statistics.ts: the caller
// passes already-prepared series plus the metric's formatter/labels, and gets back a ready Chart.js config.
//
// Depends on: Chart (CDN global), chartjs-adapter-date-fns (time axis), core/chart-zoom-pan (zoom config).

import { yearRowPlugin, YEAR_ROW_HEIGHT } from "./statistics-year-row";
import { must } from '../core/invariant';
import { cssVar } from "../core/css-vars";
import type { XZoomConfigResult } from "../core/chart-zoom-pan";

type BarConfig = import("chart.js").ChartConfiguration<"bar">;

export type Bucket = "week" | "month";

// One activity-day value for a series (already reduced to the chosen metric).
export interface BarPoint { date: number; value: number } // date = epoch ms (UTC day), value = metric amount

// One series = one activity type (or "All"): its colour, legend/tooltip label and per-day points.
export interface BarSeriesInput {
	label: string;
	color: string;
	points: BarPoint[];
}

export interface BarChartParams {
	series: BarSeriesInput[];
	bucket: Bucket;
	rangeLo: number; // inclusive epoch ms
	rangeHi: number; // inclusive epoch ms
	metricLabel: string;
	metricUnit: string;
	formatValue: (v: number) => string;
	zoomConfig: XZoomConfigResult;
	// Year-over-year: x is a single reference year onto which every calendar year is overlaid, so the
	// axis/tooltip drop the (meaningless) year and series are the calendar years themselves.
	xIsRefYear?: boolean;
}

const DAY_MS = 86_400_000;

function floorUTCDay(ms: number): number {
	return ms - (((ms % DAY_MS) + DAY_MS) % DAY_MS);
}

// Start of the bucket containing `ms` (UTC). Weekly → Monday of that ISO week; Monthly → first of month.
export function bucketStartMs(ms: number, bucket: Bucket): number {
	const d = new Date(ms);
	if (bucket === "month") return Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), 1);
	const day = d.getUTCDay();            // 0=Sun … 6=Sat
	const offset = day === 0 ? -6 : 1 - day; // back to Monday
	return floorUTCDay(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + offset));
}

function nextBucket(ms: number, bucket: Bucket): number {
	if (bucket === "month") {
		const d = new Date(ms);
		return Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + 1, 1);
	}
	return ms + 7 * DAY_MS;
}

// Inclusive list of bucket-start ms spanning [lo, hi] — the shared x-grid for every stacked series.
function bucketGrid(lo: number, hi: number, bucket: Bucket): number[] {
	const grid: number[] = [];
	for (let t = bucketStartMs(lo, bucket); t <= hi; t = nextBucket(t, bucket)) grid.push(t);
	return grid;
}

function bucketTitle(ms: number, bucket: Bucket, omitYear = false): string {
	const d = new Date(ms);
	const year = omitYear ? undefined : ("numeric" as const);
	if (bucket === "month") {
		return d.toLocaleDateString(undefined, { month: "short", year, timeZone: "UTC" });
	}
	return "Week of " + d.toLocaleDateString(undefined, { day: "numeric", month: "short", year, timeZone: "UTC" });
}

// Build the full stacked-bar Chart.js config from prepared series. Every series is summed onto the shared
// bucket grid (0-fill gaps) so bars align and stack cleanly and bar widths stay uniform.
export function buildBarChartConfig(p: BarChartParams): BarConfig {
	const fgDim = cssVar("--fg-3", "#666");
	const lineColor = cssVar("--line", "#333");
	const fontMono = cssVar("--font-mono", "monospace");

	const grid = bucketGrid(p.rangeLo, p.rangeHi, p.bucket);
	const gridIndex = new Map<number, number>();
	grid.forEach((t, i) => gridIndex.set(t, i));

	const datasets = p.series.map((s) => {
		const sums = new Array(grid.length).fill(0) as number[];
		for (const pt of s.points) {
			if (pt.date < p.rangeLo || pt.date > p.rangeHi) continue;
			const idx = gridIndex.get(bucketStartMs(pt.date, p.bucket));
			if (idx !== undefined) sums[idx] = (sums[idx] ?? 0) + pt.value; // dense pre-filled array — ?? 0 unreachable
		}
		return {
			label: s.label,
			data: grid.map((t, i) => ({ x: t, y: sums[i] })),
			backgroundColor: s.color,
			borderWidth: 0,
			borderRadius: 2,
			stack: "vol",
		};
	}) as unknown as BarConfig["data"]["datasets"];

	const unitSuffix = p.metricUnit ? " " + p.metricUnit : "";
	const multiSeries = p.series.length > 1;

	// Year tier under the month labels on multi-year views (everything except the YoY overlay).
	const showYearRow = !p.xIsRefYear;

	return {
		type: "bar",
		data: { datasets },
		plugins: [yearRowPlugin as unknown as import("chart.js").Plugin<"bar">],
		options: {
			responsive: true,
			maintainAspectRatio: false,
			animation: false,
			// Reserve room under the month labels for the year tier.
			layout: { padding: { bottom: showYearRow ? YEAR_ROW_HEIGHT : 0 } },
			interaction: { mode: "index", axis: "x", intersect: false },
			plugins: {
				legend: { display: multiSeries, position: "top", labels: { boxWidth: 12, usePointStyle: true, color: "#e2e8f0" } },
				tooltip: {
					callbacks: {
						title: (items: import("chart.js").TooltipItem<"bar">[]) =>
							items.length ? bucketTitle((must(items[0], 'non-empty tooltip items (guarded)').raw as { x: number }).x, p.bucket, p.xIsRefYear) : "",
						label: (item: import("chart.js").TooltipItem<"bar">) => {
							// Chart.js SERIES object, not a DOM data-* map — bound so the dataset ratchet stays exact.
							const series = item.dataset;
							return `${series.label}: ${p.formatValue(item.parsed.y ?? 0)}${unitSuffix}`;
						},
						footer: (items: import("chart.js").TooltipItem<"bar">[]) => {
							if (!multiSeries) return "";
							const total = items.reduce((a, it) => a + (it.parsed.y || 0), 0);
							return `Total: ${p.formatValue(total)}${unitSuffix}`;
						},
					},
				},
				zoom: p.zoomConfig as never,
				yearRow: { show: showYearRow },
			},
			scales: {
				x: {
					type: "time",
					offset: true, // pad so edge bars aren't clipped
					min: p.rangeLo,
					max: p.rangeHi,
					stacked: true,
					// Always label by month name ("Jan", "Feb", …) regardless of bucket: monthly bars get
					// one label each; weekly bars get a label at each month boundary. tooltipFormat still
					// shows the full date on hover (the title callback prints the exact bucket).
					time: { unit: "month", displayFormats: { month: "MMM" }, ...(p.xIsRefYear ? {} : { tooltipFormat: "PP" }) },
					// Center the month label under its bar (was "inner", which shifted labels off the bars).
					ticks: { maxRotation: 0, autoSkip: true, autoSkipPadding: 8, align: "center", font: { family: fontMono, size: 11 }, color: fgDim },
					grid: { display: false },
				},
				y: {
					type: "linear",
					stacked: true,
					beginAtZero: true,
					// No axis title — the metric is already chosen by the user and the unit shows in the tooltip.
					title: { display: false },
					ticks: { padding: 2, font: { family: fontMono, size: 11 }, color: fgDim },
					grid: { color: lineColor },
				},
			},
		},
	} as BarConfig;
}
