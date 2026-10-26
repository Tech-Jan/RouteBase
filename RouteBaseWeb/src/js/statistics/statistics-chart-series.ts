// statistics-chart-series.ts — pure series/point builders (zero DOM). Accumulates the daily
// aggregate/per-type points onto shared x-grids for the line/multiline/bar chart modes.

import { must } from '../core/invariant';
import {
	aggregate, byType, range,
	refDate, dayUTC, metricVal, yearOf, floorUTCDay,
	sortedTypes, typeColor, yearColor, yearAlpha, hexToRgba, YOY_LINE_WIDTH,
	type CumulativePoint, type MetricKey, type Series, type Source,
} from './statistics-chart-store';
import type { BarSeriesInput, BarPoint } from './statistics-bars';

type Point = import('chart.js').Point;

// Range-view x extent (epoch ms): header dates when set, else the data's own extent.
export function rangeExtentMs(sources: Source[]): { lo: number; hi: number } | null {
	let dLo = Infinity, dHi = -Infinity;
	for (const s of sources) {
		for (const p of s.pts) {
			const ms = dayUTC(p);
			if (ms < dLo) dLo = ms;
			if (ms > dHi) dHi = ms;
		}
	}
	const from = range.from ? Date.parse(`${range.from}T00:00:00Z`) : null;
	const to = range.to ? Date.parse(`${range.to}T00:00:00Z`) : null;
	let lo = from ?? dLo;
	let hi = to ?? dHi;
	if (!isFinite(lo) || !isFinite(hi)) return null;
	if (lo > hi) { const t = lo; lo = hi; hi = t; } // ST-09: From > To (fat-finger) → swap, not an inverted axis
	return { lo: floorUTCDay(lo), hi: floorUTCDay(hi) };
}

// Accumulate one series onto the shared x-grid: value at each grid day is the running cumulative total.
export function buildLineData(pts: CumulativePoint[], m: MetricKey, yoy: boolean, gridX: number[]): Point[] {
	const sorted = pts
		.map((p) => ({ x: yoy ? refDate(p.date) : dayUTC(p), v: metricVal(p, m) }))
		.sort((a, b) => a.x - b.x);
	const data: { x: number; y: number }[] = [];
	let acc = 0, j = 0;
	for (const gx of gridX) {
		while (j < sorted.length) {
			const e = must(sorted[j], 'j < length (loop-bounded)');
			if (e.x > gx) break;
			acc += e.v; j++;
		}
		data.push({ x: gx, y: acc });
	}
	return data as unknown as Point[];
}

export function groupByYear(pts: CumulativePoint[]): { year: number; pts: CumulativePoint[] }[] {
	const byYear = new Map<number, CumulativePoint[]>();
	for (const p of pts) {
		const yr = yearOf(p.date);
		let yrPts = byYear.get(yr);
		if (!yrPts) { yrPts = []; byYear.set(yr, yrPts); }
		yrPts.push(p);
	}
	return [...byYear.keys()].sort((a, b) => b - a).map((year) => ({ year, pts: must(byYear.get(year), 'year from byYear.keys()') }));
}

export function buildSeriesList(isYoy: boolean, sources: Source[]): Series[] {
	// YoY + single source (line mode) → one series per calendar year; legend groups by year.
	if (isYoy && sources.length <= 1) {
		const pts = sources[0]?.pts ?? [];
		return groupByYear(pts).map(({ year, pts }, i) => {
			const { color, width } = yearColor(i);
			return { groupId: `y${year}`, groupLabel: String(year), groupColor: color, label: String(year), readoutLabel: String(year), lineColor: color, width, pts };
		});
	}

	// YoY + multiple sources (multiline) → every type × every year; legend groups by type, older years dimmed.
	if (isYoy) {
		const out: Series[] = [];
		for (const src of sources) {
			groupByYear(src.pts).forEach(({ year, pts }, i) => {
				out.push({
					groupId: `t${src.key}`, groupLabel: src.label, groupColor: src.color,
					label: `${src.label} ${year}`,
					readoutLabel: String(year),
					lineColor: hexToRgba(src.color, yearAlpha(i)),
					width: YOY_LINE_WIDTH,
					pts,
				});
			});
		}
		return out;
	}

	// Range → one continuous line per source; legend groups by source (year timeline).
	return sources.map((src) => ({
		groupId: `t${src.key}`, groupLabel: src.label, groupColor: src.color,
		label: src.label, readoutLabel: src.label, lineColor: src.color, width: 2, pts: src.pts,
	}));
}

// Per-year stacked bars (YoY): one series per calendar year from the aggregate, overlaid on the reference year.
export function buildYoyBarSeries(metric: MetricKey): BarSeriesInput[] {
	const byYear = new Map<number, BarPoint[]>();
	for (const p of aggregate) {
		const yr = yearOf(p.date);
		let yrRows = byYear.get(yr);
		if (!yrRows) { yrRows = []; byYear.set(yr, yrRows); }
		yrRows.push({ date: refDate(p.date), value: metricVal(p, metric) });
	}
	if (byYear.size === 0) return [];
	const years = [...byYear.keys()].sort((a, b) => a - b);
	const newest = must(years[years.length - 1], 'non-empty years (guarded above)');
	return years.map((yr) => ({ label: String(yr), color: yearColor(newest - yr).color, points: must(byYear.get(yr), 'yr from byYear.keys()') }));
}

// Per-type stacked bars (range): one series per raw activity type over a real calendar range.
export function buildTypeBarSeries(metric: MetricKey): BarSeriesInput[] {
	const types = sortedTypes();
	return types.map((t) => ({
		label: t || 'Unknown',
		color: typeColor(t, types),
		points: (byType.get(t) ?? []).map((p) => ({ date: dayUTC(p), value: metricVal(p, metric) })),
	}));
}

export function dataExtentMs(): { min: number; max: number } | null {
	let min = Infinity, max = -Infinity;
	for (const p of aggregate) {
		const ms = dayUTC(p);
		if (ms < min) min = ms;
		if (ms > max) max = ms;
	}
	return isFinite(min) ? { min, max } : null;
}
