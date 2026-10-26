/**
 * Segment Statistics — Time Distribution chart module
 *
 * Buckets the current user's own efforts by elapsed time and renders
 * a Chart.js bar chart. Highlights the PR bucket in --acc-cyan.
 *
 * The render body is an exported function (renderDistributionChart) so its render
 * path is unit-testable; the top-level block only pre-filters the own efforts,
 * wires the ctx callback, and paints once. Import-time semantics are unchanged.
 *
 * Depends on: ctx (shared context from segment-statistics.ts)
 */
import type { SegmentStatisticsContext } from './segment-stats-page-types';
import type { EffortDateData } from './segment-stats-effort-types';
import { ctx } from './segment-statistics';
import { formatTime } from '../core/format';
import { cssVar } from '../core/css-vars';
import { setChartEmptyState } from '../core/chart-empty-state';
import { must } from '../core/invariant';

/**
 * Render the own-effort time-distribution histogram into #distributionChart.
 * `ownEfforts` is the pre-filtered set (own, not excluded); fewer than 2 shows the
 * shared empty state instead of a blank canvas.
 */
export function renderDistributionChart(ctx: SegmentStatisticsContext, ownEfforts: EffortDateData[]): void {
	const canvas = document.getElementById("distributionChart") as HTMLCanvasElement | null;
	if (!canvas) return;
	const host = canvas.parentElement as HTMLElement | null;

	if (ctx.distributionChart) ctx.distributionChart.destroy();

	if (ownEfforts.length < 2) {
		setChartEmptyState(host, "Need more efforts to bucket — go ride!", true);
		return;
	}

	setChartEmptyState(host, "", false);

	const times = ownEfforts.map(e => e.time);
	const minTime = Math.min(...times);
	const maxTime = Math.max(...times);
	const range = maxTime - minTime;

	const BUCKET_COUNT = 10;
	const bucketSize = range > 0 ? Math.max(1, Math.ceil(range / BUCKET_COUNT)) : 30;
	const actualBuckets = range > 0 ? Math.ceil(range / bucketSize) : 1;

	const counts = new Array(actualBuckets).fill(0);
	times.forEach(t => {
		const idx = range > 0 ? Math.min(Math.floor((t - minTime) / bucketSize), actualBuckets - 1) : 0;
		counts[idx]++;
	});

	// Theme colours through cssVar (item 3) instead of a raw getComputedStyle block, so this
	// chart reads tokens the same way as every other chart (one code path to fix).
	const accentCyan = cssVar("--acc-cyan", "oklch(0.75 0.13 210)");
	const bgFill = cssVar("--bg-4", "oklch(0.22 0.008 240)");
	const fgDim = cssVar("--fg-3", "#666");
	const lineColor = cssVar("--line", "#333");
	const fontMono = cssVar("--font-mono", "monospace");

	// PR bucket = bucket containing the fastest (min) time = bucket 0
	const backgroundColors = counts.map((_, i) => i === 0 ? accentCyan : bgFill);

	const labels = counts.map((_, i) => {
		const bucketStart = minTime + i * bucketSize;
		return formatTime(bucketStart);
	});

	ctx.distributionChart = new Chart(must(canvas.getContext("2d"), 'canvas yields a 2d context'), {
		type: "bar",
		data: {
			labels,
			datasets: [{
				label: "Effort Count",
				data: counts,
				backgroundColor: backgroundColors,
				borderWidth: 0,
				borderRadius: 3,
			}],
		},
		options: {
			responsive: true,
			maintainAspectRatio: false,
			animation: false,
			plugins: {
				legend: { display: false },
				title: {
					display: true,
					text: "Time Distribution (own efforts)",
				},
				tooltip: {
					callbacks: {
						title: (items: import("chart.js").TooltipItem<"bar">[]) => {
							const i = must(items[0], 'tooltip has an item').dataIndex;
							const bucketStart = minTime + i * bucketSize;
							const bucketEnd = bucketStart + bucketSize;
							return formatTime(bucketStart) + " – " + formatTime(bucketEnd);
						},
						label: (item: import("chart.js").TooltipItem<"bar">) => "Efforts: " + item.parsed.y,
					},
				},
			},
			scales: {
				x: {
					type: "category",
					title: { display: true, text: "Segment Time" },
					ticks: {
						font: { family: fontMono, size: 11 },
						color: fgDim,
					},
					grid: { color: lineColor },
				},
				y: {
					type: "linear",
					title: { display: true, text: "Efforts" },
					beginAtZero: true,
					ticks: {
						stepSize: 1,
						font: { family: fontMono, size: 11 },
						color: fgDim,
					},
					grid: { color: lineColor },
				},
			},
		},
	});
}

if (ctx && ctx.config.hasEfforts && ctx.config.effortDates) {
	// Item 25: excluded efforts are dropped from the own-effort distribution histogram.
	const ownEfforts = ctx.config.effortDates.filter(e => e.isOwn === true && !e.excluded);
	ctx._renderDistributionChart = () => renderDistributionChart(ctx, ownEfforts);
	renderDistributionChart(ctx, ownEfforts);
}
