// statistics-chart.ts — Statistics page cumulative chart orchestrator (line / multiline / bar).
//
// Renders an accumulated distance / elevation / moving-time / activity-count chart. Data arrives already
// filtered (owner/type/date) from the shared data layer via setStatsChartData() — this module never fetches.
// Pure state/config/date helpers live in statistics-chart-store; series builders in -series; the headline
// widget in -headline. This file owns the Chart.js construction + control wiring.
//
// Depends on: Chart (CDN global), chartjs-plugin-zoom, chartjs-adapter-date-fns, core/chart-zoom-pan.

import { xZoomConfig, finalizeChart } from '../core/chart-zoom-pan';
import { must } from '../core/invariant';
import { datasetPoints, legendItemGroupId, makeGroupLegendItem } from '../core/chart-internals';
import { crosshairPlugin, makeReadoutExternal, hideReadout, areaGradient, applySelectionFill, setReadoutInteractive, setStatHoveredLabel, repaintReadout } from './statistics-hover';
import { buildBarChartConfig, type Bucket, type BarSeriesInput } from './statistics-bars';
import { yearRowPlugin, YEAR_ROW_HEIGHT } from './statistics-year-row';
import type { CumulativeTypePoint } from './statistics-data';
import {
	state, range, ingest, currentSources, selectedMetrics,
	dayGrid, floorUTCDay, YOY_MIN, YOY_MAX, METRICS, METRIC_ORDER, METRIC_COLORS, METRIC_DASH,
	type MetricKey, type ChartType, type ViewKey, type StatsChartRange,
} from './statistics-chart-store';
import { rangeExtentMs, buildLineData, buildSeriesList, buildYoyBarSeries, buildTypeBarSeries, dataExtentMs } from './statistics-chart-series';
import { updateHeadline } from './statistics-chart-headline';
import { setChartEmptyState } from '../core/chart-empty-state';
import { devicePrefKeys, getJson, setJson } from '../core/device-prefs';

type ChartInstance = InstanceType<typeof Chart>;
type LineDataset = import('chart.js').ChartDataset<'line'>;

export type { StatsChartRange };

let chart: ChartInstance | null = null;

// ─── Chart render ──────────────────────────────────────────────────────────────

function showEmpty(show: boolean): void {
	// Shared empty-data contract (item 4) — the message overlays #cumulative-chart-wrap and hides
	// its canvas, replacing the bespoke #cumulative-empty sibling + wrap-visibility toggle.
	const wrap = document.getElementById('cumulative-chart-wrap');
	setChartEmptyState(wrap, 'No activity data to plot yet.', show);
}

function renderBars(canvas: HTMLCanvasElement): void {
	const metric = selectedMetrics()[0] ?? 'distance';
	const isYoy = state.view === 'yoy';

	let lo: number, hi: number;
	let series: BarSeriesInput[];
	if (isYoy) {
		series = buildYoyBarSeries(metric);
		lo = YOY_MIN; hi = YOY_MAX;
	} else {
		series = buildTypeBarSeries(metric);
		const ext = dataExtentMs();
		const from = range.from ? Date.parse(`${range.from}T00:00:00Z`) : null;
		const to = range.to ? Date.parse(`${range.to}T00:00:00Z`) : null;
		lo = from ?? (ext ? ext.min : Date.now());
		hi = to ?? (ext ? ext.max : Date.now());
		if (lo > hi) { const t = lo; lo = hi; hi = t; } // ST-09: swap a backwards From > To range
	}

	if (series.every((s) => s.points.length === 0)) { showEmpty(true); updateHeadline(currentSources(), true); return; }
	showEmpty(false);

	const cfg = buildBarChartConfig({
		series,
		bucket: state.bucket,
		rangeLo: lo,
		rangeHi: hi,
		metricLabel: METRICS[metric].label,
		metricUnit: METRICS[metric].unit,
		formatValue: METRICS[metric].format,
		zoomConfig: xZoomConfig(),
		xIsRefYear: isYoy,
	});
	const ctx2d = canvas.getContext('2d');
	if (!ctx2d) return;
	chart = new Chart(ctx2d, cfg as unknown as import('chart.js').ChartConfiguration);
	finalizeChart(canvas, chart, 'cumulative');
	hideReadout();
	updateHeadline(currentSources());
}

function renderChart(): void {
	const canvas = document.getElementById('cumulativeChart') as HTMLCanvasElement | null;
	if (!canvas || typeof Chart === 'undefined') return;

	if (chart) { chart.destroy(); chart = null; }
	state.selectedLabel = null;

	if (state.chartType === 'bar') { renderBars(canvas); return; }

	const isYoy = state.view === 'yoy';
	const sources = currentSources();
	const metrics = selectedMetrics();
	const multiMetric = metrics.length > 1;
	const seriesList = buildSeriesList(isYoy, sources);

	if (seriesList.every((s) => s.pts.length === 0)) { showEmpty(true); updateHeadline(sources, true); return; }
	showEmpty(false);

	// Shared x-grid (UTC day boundaries) every series accumulates onto so the hover crosshair lines up.
	// YoY uses the reference calendar year; range uses the header extent (or the data's own extent).
	let gridX: number[];
	let rangeLo = 0, rangeHi = 0;
	if (isYoy) {
		gridX = dayGrid(YOY_MIN, YOY_MAX);
	} else {
		const ext = rangeExtentMs(sources);
		rangeLo = ext ? ext.lo : floorUTCDay(Date.now());
		rangeHi = ext ? ext.hi : rangeLo;
		gridX = dayGrid(rangeLo, rangeHi);
	}

	const willFill = seriesList.length === 1 && metrics.length === 1;
	const groupByMetric = new Set(seriesList.map((s) => s.groupId)).size <= 1;

	const datasets: LineDataset[] = [];
	const datasetMetric: MetricKey[] = [];
	const datasetName: string[] = [];
	const datasetDisplay: string[] = [];
	const datasetSeriesKey: string[] = [];
	const datasetGroup: string[] = [];
	const groupMeta = new Map<string, { label: string; color: string }>();
	const groupOrder: string[] = [];

	let seriesIdx = 0;
	for (const s of seriesList) {
		for (const m of metrics) {
			const groupId = groupByMetric ? `m${m}` : s.groupId;
			const groupLabel = groupByMetric ? METRICS[m].label : s.groupLabel;
			const groupColor = groupByMetric ? METRIC_COLORS[m] : s.groupColor;
			const lineColor = groupByMetric ? METRIC_COLORS[m] : s.lineColor;
			const dash = !groupByMetric && multiMetric ? METRIC_DASH[m] : [];

			if (!groupMeta.has(groupId)) { groupMeta.set(groupId, { label: groupLabel, color: groupColor }); groupOrder.push(groupId); }

			const labelParts = groupByMetric ? [METRICS[m].label] : multiMetric ? [s.label, METRICS[m].label] : [s.label];
			const fillIt = state.selectedLabel != null ? s.label === state.selectedLabel : willFill;
			const ds = {
				label: labelParts.join(' · '),
				data: buildLineData(s.pts, m, isYoy, gridX),
				borderColor: lineColor,
				backgroundColor: fillIt ? areaGradient(lineColor) : lineColor,
				borderWidth: s.width,
				borderDash: dash,
				yAxisID: `y_${m}`,
				fill: fillIt ? 'origin' : false,
				pointRadius: 0,
				pointHoverRadius: 0,
				tension: 0.3,
				parsing: false,
				normalized: true,
			} as LineDataset;
			Object.assign(ds, { _seriesLabel: s.label, _defaultFill: willFill, _baseWidth: s.width });
			datasets.push(ds);
			datasetMetric.push(m);
			datasetName.push(s.label);
			datasetDisplay.push(s.readoutLabel);
			datasetSeriesKey.push(String(seriesIdx));
			datasetGroup.push(groupId);
		}
		seriesIdx++;
	}

	const groupIdxs = (g: string): number[] => datasetGroup.flatMap((gg, i) => (gg === g ? [i] : []));

	const monthTime = { unit: 'month', displayFormats: { month: 'MMM' } };
	const xScale = {
		type: 'time',
		...(isYoy
			? { min: YOY_MIN, max: YOY_MAX, time: { ...monthTime } }
			: { min: rangeLo, max: rangeHi, time: { ...monthTime, tooltipFormat: 'PP' } }),
		ticks: { maxRotation: 0, autoSkip: false, align: 'center' },
		grid: { display: true, drawOnChartArea: true, drawTicks: false, color: 'rgba(148,163,184,0.12)', lineWidth: 0.6 },
	} as unknown as import('chart.js').ScaleOptionsByType<'time'>;

	const scales: Record<string, unknown> = { x: xScale };
	metrics.forEach((m) => {
		scales[`y_${m}`] = { type: 'linear', display: false, beginAtZero: true, grid: { display: false } };
	});

	const colWidthCh: Partial<Record<MetricKey, number>> = {};
	datasets.forEach((ds, i) => {
		const m = must(datasetMetric[i], 'datasetMetric lockstep with datasets');
		const data = datasetPoints(ds);
		const last = data.length ? must(data[data.length - 1], 'non-empty data (guarded)').y : 0;
		const chars = METRICS[m].format(last).length;
		if (chars > (colWidthCh[m] ?? 0)) colWidthCh[m] = chars;
	});

	const readoutRow = (datasetIndex: number, y: number) => {
		const m = must(datasetMetric[datasetIndex], 'readout datasetIndex lockstep with datasetMetric');
		return {
			key: datasetSeriesKey[datasetIndex] ?? '',
			label: datasetName[datasetIndex] ?? '',
			display: datasetDisplay[datasetIndex] ?? '',
			rank: METRIC_ORDER.indexOf(m),
			num: METRICS[m].format(y),
			unit: METRICS[m].unit,
			numWidthCh: colWidthCh[m],
		};
	};

	const renderReadout = makeReadoutExternal(isYoy, readoutRow, () => state.selectedLabel);

	const showYearRow = !isYoy;
	const ctx2d = canvas.getContext('2d');
	if (!ctx2d) return;
	chart = new Chart(ctx2d, {
		type: 'line',
		data: { datasets },
		plugins: [crosshairPlugin, yearRowPlugin],
		options: {
			responsive: true,
			maintainAspectRatio: false,
			animation: false,
			layout: { padding: { bottom: showYearRow ? YEAR_ROW_HEIGHT : 0 } },
			interaction: { mode: 'index', axis: 'x', intersect: false },
			plugins: {
				legend: {
					display: groupOrder.length > 1,
					position: 'top',
					labels: {
						boxWidth: 12,
						usePointStyle: true,
						color: '#e2e8f0',
						generateLabels: (c: ChartInstance) => groupOrder.map((g) => {
							const meta = must(groupMeta.get(g), 'g from groupOrder (built together)');
							const allHidden = groupIdxs(g).every((i) => !c.isDatasetVisible(i));
							return makeGroupLegendItem({
								text: meta.label,
								fillStyle: meta.color,
								strokeStyle: meta.color,
								fontColor: '#e2e8f0',
								hidden: allHidden,
								lineWidth: 0,
							}, g);
						}),
					},
					onClick: (_e: unknown, item: import('chart.js').LegendItem, legend: { chart: ChartInstance }) => {
						const g = legendItemGroupId(item);
						const c = legend.chart;
						const idxs = groupIdxs(g);
						const show = idxs.every((i) => !c.isDatasetVisible(i));
						idxs.forEach((i) => c.setDatasetVisibility(i, show));
						c.update();
					},
				} as unknown as import('chart.js').LegendOptions<'line'>,
				tooltip: {
					enabled: false,
					external: renderReadout as unknown as import('chart.js').TooltipOptions<'line'>['external'],
				},
				zoom: xZoomConfig() as unknown,
				yearRow: { show: showYearRow },
			} as NonNullable<import('chart.js').ChartOptions<'line'>['plugins']>,
			scales: scales as unknown as NonNullable<import('chart.js').ChartOptions<'line'>['scales']>,
		},
	});

	finalizeChart(canvas, chart, 'cumulative');

	setReadoutInteractive({
		onRowHover: (label) => {
			setStatHoveredLabel(label);
			if (chart) applySelectionFill(chart, label ?? state.selectedLabel);
			repaintReadout();
		},
		onRowClick: (label) => {
			state.selectedLabel = state.selectedLabel === label ? null : label;
			setStatHoveredLabel(null);
			if (chart) applySelectionFill(chart, state.selectedLabel);
			repaintReadout();
		},
		onHide: () => {
			setStatHoveredLabel(null);
			if (chart) applySelectionFill(chart, state.selectedLabel);
		},
	});

	hideReadout();
	updateHeadline(sources);
}

// ─── Controls ────────────────────────────────────────────────────────────────

function syncActive(groupId: string, attr: string, set: Set<string>): void {
	const group = document.getElementById(groupId);
	if (!group) return;
	group.querySelectorAll<HTMLElement>(`[data-${attr}]`).forEach((b) => {
		const val = b.dataset[attr];
		b.classList.toggle('active', val != null && set.has(val));
	});
}

/**
 * Which series the chart draws — a DEVICE preference: it configures one screen, and a user coming back
 * to this page wants the metrics they were reading, not the shipped default.
 *
 * Only the metric SELECTION is stored. Chart type, bucket and view are the same tier and were not part
 * of this step's brief; if they are ever added, they belong under the same chart namespace.
 */
const STATS_CHARTS = 'statistics';
const SERIES_KEY = devicePrefKeys.chartSeries(STATS_CHARTS);

function persistMetrics(): void {
	setJson(SERIES_KEY, [...state.metrics]);
}

/**
 * Restore the stored selection, filtered to metrics that still exist and narrowed to one in bar mode
 * (the same constraint the toggle enforces). An empty or unusable stored set is IGNORED rather than
 * applied — restoring it would open the page on a chart with no series on it at all.
 */
function restoreMetrics(): void {
	const stored = getJson<unknown>(SERIES_KEY, null);
	if (!Array.isArray(stored)) return;
	const valid = stored.filter((m): m is MetricKey => METRIC_ORDER.includes(m as MetricKey));
	if (valid.length === 0) return;
	state.metrics = new Set<MetricKey>(state.chartType === 'bar' ? valid.slice(0, 1) : valid);
}

function toggleMetric(v: MetricKey): void {
	if (state.chartType === 'bar') { state.metrics = new Set<MetricKey>([v]); return; }
	if (state.metrics.has(v)) {
		if (state.metrics.size > 1) state.metrics.delete(v);
	} else {
		state.metrics.add(v);
	}
}

function wireGroup(groupId: string, attr: string, onPick: (value: string) => void): void {
	const group = document.getElementById(groupId);
	if (!group) return;
	group.addEventListener('click', (e) => {
		const btn = (e.target as HTMLElement).closest<HTMLElement>(`[data-${attr}]`);
		const value = btn?.dataset[attr];
		if (value != null) onPick(value);
	});
}

/** Wires the chart's control groups. Called once by the page entry. */
export function initStatsChartControls(): void {
	// Before any handler runs and before the first render: the markup hard-codes `active` on Distance,
	// so a restored selection has to be reflected onto the buttons or the control lies about the chart.
	restoreMetrics();
	syncActive('cumulative-metric', 'metric', state.metrics as Set<string>);

	wireGroup('cumulative-charttype', 'charttype', (v) => {
		state.chartType = v as ChartType;
		syncActive('cumulative-charttype', 'charttype', new Set([state.chartType]));
		const bucketGroup = document.getElementById('cumulative-bucket');
		if (bucketGroup) bucketGroup.style.display = state.chartType === 'bar' ? '' : 'none';
		if (state.chartType === 'bar' && state.metrics.size > 1) {
			state.metrics = new Set<MetricKey>([must(selectedMetrics()[0], 'metrics.size > 1 (guarded)')]);
			syncActive('cumulative-metric', 'metric', state.metrics as Set<string>);
			persistMetrics();   // bar mode NARROWED the selection — store what the user is actually left with
		}
		renderChart();
	});
	wireGroup('cumulative-bucket', 'bucket', (v) => {
		state.bucket = v as Bucket;
		syncActive('cumulative-bucket', 'bucket', new Set([state.bucket]));
		renderChart();
	});
	wireGroup('cumulative-view', 'view', (v) => {
		state.view = v as ViewKey;
		syncActive('cumulative-view', 'view', new Set([state.view]));
		renderChart();
	});
	wireGroup('cumulative-metric', 'metric', (v) => {
		toggleMetric(v as MetricKey);
		syncActive('cumulative-metric', 'metric', state.metrics as Set<string>);
		persistMetrics();
		renderChart();
	});
}

/** Feeds the chart new (already-filtered) data + the active header date range, then re-renders. */
export function setStatsChartData(points: CumulativeTypePoint[], activeRange: StatsChartRange): void {
	ingest(points, activeRange);
	renderChart();
}
