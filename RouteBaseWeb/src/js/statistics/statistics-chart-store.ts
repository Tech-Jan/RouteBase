// statistics-chart-store.ts — types, config, pure date/color helpers, and the shared mutable
// chart state (ingested per data change). No DOM, no Chart.js. The series/headline/orchestrator
// modules import from here; this module never imports back (one-directional dependency graph).

import type { Bucket } from './statistics-bars';
import { must } from '../core/invariant';
import type { CumulativeTypePoint } from './statistics-data';

// ─── Types ──────────────────────────────────────────────────────────────────

/** One day's aggregated totals (type stripped — the source series already scopes the type). */
export interface CumulativePoint {
	date: string; // ISO "YYYY-MM-DDT00:00:00"
	distanceKm: number;
	elevationM: number;
	movingTimeMin: number;
	count: number;
}

export type MetricKey = 'distance' | 'elevation' | 'time' | 'count';
export type ChartType = 'line' | 'multiline' | 'bar';
export type ViewKey = 'yoy' | 'range';

/** Header date range (local `YYYY-MM-DD`); clamps the `range`-view x-axis when set. */
export interface StatsChartRange {
	from?: string | undefined;
	to?: string | undefined;
}

export interface MetricMeta {
	label: string;
	unit: string;
	value: (p: CumulativePoint) => number;
	format: (v: number) => string;
}

// A data series before it is split per-metric. `group*` drives the (grouped) legend.
export interface Series {
	groupId: string;
	groupLabel: string;
	groupColor: string;
	label: string;
	readoutLabel: string;
	lineColor: string;
	width: number;
	pts: CumulativePoint[];
}

// One render source: the aggregate line, or one raw activity type.
export interface Source {
	key: string;
	label: string;
	color: string; // hex — dimmed per-year in YoY multi-source mode
	pts: CumulativePoint[];
}

// ─── Config ───────────────────────────────────────────────────────────────────

export const METRICS: Record<MetricKey, MetricMeta> = {
	distance: { label: 'Distance', unit: 'km', value: (p) => p.distanceKm, format: (v) => v.toFixed(0) },
	elevation: { label: 'Elevation', unit: 'm', value: (p) => p.elevationM, format: (v) => v.toFixed(0) },
	time: { label: 'Time', unit: 'h', value: (p) => p.movingTimeMin, format: (v) => v.toFixed(1) },
	count: { label: 'Activities', unit: '', value: (p) => p.count, format: (v) => v.toFixed(0) },
};

export const METRIC_ORDER: MetricKey[] = ['distance', 'elevation', 'time', 'count'];

// Moving time is summed in minutes but displayed in hours.
const TIME_MIN_TO_DISPLAY = 1 / 60;

export const ACCENT = '#22d3ee'; // --acc-cyan

export const METRIC_COLORS: Record<MetricKey, string> = {
	distance: '#22d3ee', elevation: '#84cc16', time: '#f59e0b', count: '#a78bfa',
};
export const METRIC_DASH: Record<MetricKey, number[]> = {
	distance: [], elevation: [6, 4], time: [2, 3], count: [8, 3, 2, 3],
};

// Stable per-type colour: the type-group palette (TS port of ActivityTypeHelper.GetTypeGroup) when the raw
// type maps to bike/run/walk, else a categorical colour by the type's sorted index.
const GROUP_COLOR: Record<string, string> = { bike: '#22d3ee', run: '#f59e0b', walk: '#84cc16' };
const CATEGORICAL = ['#a78bfa', '#f472b6', '#38bdf8', '#fb923c', '#34d399', '#e879f9', '#facc15', '#94a3b8', '#4ade80', '#f87171'];

function typeGroup(type: string): string {
	const t = type.toLowerCase();
	if (!t) return 'other';
	if (t.includes('cycl') || t.includes('bik') || t.includes('ride') || t.includes('mtb')) return 'bike';
	if (t.includes('run') || t.includes('trail')) return 'run';
	if (t.includes('walk') || t.includes('hik')) return 'walk';
	return 'other';
}
export function typeColor(type: string, sorted: string[]): string {
	const gc = GROUP_COLOR[typeGroup(type)];
	if (gc) return gc;
	const i = Math.max(0, sorted.indexOf(type));
	return must(CATEGORICAL[i % CATEGORICAL.length], 'modulo index within CATEGORICAL');
}

// Year-over-year overlays every year onto a single reference (leap) year so the chart uses a real time axis.
export const REF_YEAR = 2000;
export const YOY_MIN = Date.UTC(REF_YEAR, 0, 1);
export const YOY_MAX = Date.UTC(REF_YEAR, 11, 31);

// Date-only parts straight from the ISO prefix. CumulativePoint.date carries no timezone meaning
// (server's local calendar date) — new Date(iso) + getUTC*() is wrong: a "T00:00:00" (no Z) string
// parses as LOCAL midnight, and UTC getters then shift the day for every UTC+ browser timezone.
export function dateParts(iso: string): { y: number; m: number; d: number } {
	return { y: +iso.slice(0, 4), m: +iso.slice(5, 7) - 1, d: +iso.slice(8, 10) };
}
export function refDate(iso: string): number {
	const { m, d } = dateParts(iso);
	return Date.UTC(REF_YEAR, m, d);
}
export function yearOf(iso: string): number {
	return dateParts(iso).y;
}

export const DAY_MS = 86_400_000;

export function dayUTC(p: CumulativePoint): number {
	const { y, m, d } = dateParts(p.date);
	return Date.UTC(y, m, d);
}
export function floorUTCDay(ms: number): number {
	return ms - (((ms % DAY_MS) + DAY_MS) % DAY_MS);
}
export function dayGrid(loMs: number, hiMs: number): number[] {
	const grid: number[] = [];
	for (let t = loMs; t <= hiMs; t += DAY_MS) grid.push(t);
	return grid;
}

// ─── State + data ──────────────────────────────────────────────────────────────

export const state = {
	view: 'yoy' as ViewKey,
	chartType: 'line' as ChartType,
	bucket: 'week' as Bucket,
	metrics: new Set<MetricKey>(['distance']),
	selectedLabel: null as string | null, // series clicked to pin its area fill
};

// Derived once per data change (ingest): the summed daily aggregate + one series per raw type.
export let aggregate: CumulativePoint[] = [];
export const byType = new Map<string, CumulativePoint[]>();
export let range: StatsChartRange = {};

/** Rebuild the aggregate + per-type series from freshly-fetched points and the active header range. */
export function ingest(points: CumulativeTypePoint[], activeRange: StatsChartRange): void {
	range = activeRange;
	byType.clear();
	const byDate = new Map<string, CumulativePoint>();
	for (const p of points) {
		const list = byType.get(p.type) ?? [];
		list.push({ date: p.date, distanceKm: p.distanceKm, elevationM: p.elevationM, movingTimeMin: p.movingTimeMin, count: p.count });
		byType.set(p.type, list);

		let agg = byDate.get(p.date);
		if (!agg) { agg = { date: p.date, distanceKm: 0, elevationM: 0, movingTimeMin: 0, count: 0 }; byDate.set(p.date, agg); }
		agg.distanceKm += p.distanceKm;
		agg.elevationM += p.elevationM;
		agg.movingTimeMin += p.movingTimeMin;
		agg.count += p.count;
	}
	aggregate = [...byDate.values()].sort((a, b) => a.date.localeCompare(b.date));
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

export function metricVal(p: CumulativePoint, m: MetricKey): number {
	const raw = METRICS[m].value(p);
	return m === 'time' ? raw * TIME_MIN_TO_DISPLAY : raw;
}

export const YOY_LINE_WIDTH = 2;
export function yearColor(idxFromNewest: number): { color: string; width: number } {
	if (idxFromNewest === 0) return { color: ACCENT, width: YOY_LINE_WIDTH };
	const opacity = Math.max(0.25, 0.7 - (idxFromNewest - 1) * 0.12);
	return { color: `rgba(148,163,184,${opacity.toFixed(2)})`, width: YOY_LINE_WIDTH };
}
export function yearAlpha(idxFromNewest: number): number {
	return idxFromNewest === 0 ? 1 : Math.max(0.3, 0.85 - (idxFromNewest - 1) * 0.13);
}
export function hexToRgba(hex: string, alpha: number): string {
	const n = parseInt(hex.replace('#', ''), 16);
	const r = (n >> 16) & 255, g = (n >> 8) & 255, b = n & 255;
	return `rgba(${r},${g},${b},${alpha.toFixed(2)})`;
}

export function selectedMetrics(): MetricKey[] {
	return METRIC_ORDER.filter((m) => state.metrics.has(m));
}

// The render sources for the current chart type: one aggregate line, or one per raw type.
export function sortedTypes(): string[] {
	return [...byType.keys()].sort();
}
export function currentSources(): Source[] {
	if (state.chartType === 'multiline') {
		const types = sortedTypes();
		return types.map((t) => ({ key: t, label: t || 'Unknown', color: typeColor(t, types), pts: byType.get(t) ?? [] }));
	}
	return [{ key: 'all', label: 'All', color: ACCENT, pts: aggregate }];
}
