// statistics-chart-headline.ts — the headline number + caption DOM widget above the chart.
// Mirrors the small KPI-writer pattern of statistics-summary.ts.

import { must } from '../core/invariant';
import {
	state, range, METRICS, METRIC_COLORS, metricVal, selectedMetrics, refDate, yearOf,
	type MetricKey, type Source,
} from './statistics-chart-store';

function currentYearAcross(sources: Source[]): number | null {
	let y = -Infinity;
	for (const s of sources) for (const p of s.pts) {
		const yr = yearOf(p.date);
		if (yr > y) y = yr;
	}
	return isFinite(y) ? y : null;
}

function sumMetric(pts: { date: string; distanceKm: number; elevationM: number; movingTimeMin: number; count: number }[], m: MetricKey): number {
	let s = 0;
	for (const p of pts) s += metricVal(p, m);
	return s;
}

// Cumulative total for a metric under the active view/mode: YoY line/multiline → current year only;
// otherwise the grand total of the (already date-filtered) data. Summed across the sources.
function metricTotal(m: MetricKey, sources: Source[]): number {
	const yoyCurrentYear = state.view === 'yoy' && state.chartType !== 'bar';
	const cy = yoyCurrentYear ? currentYearAcross(sources) : null;
	let total = 0;
	for (const s of sources) {
		let pts = s.pts;
		if (yoyCurrentYear) pts = pts.filter((p) => yearOf(p.date) === cy);
		total += sumMetric(pts, m);
	}
	return total;
}

// Year-to-date growth of the primary metric vs the prior year at the same calendar point.
function yoyGrowthPct(m: MetricKey, sources: Source[]): number | null {
	const cy = currentYearAcross(sources);
	if (cy == null) return null;
	let cutoff = -Infinity;
	for (const s of sources) for (const p of s.pts) {
		if (yearOf(p.date) === cy) { const rd = refDate(p.date); if (rd > cutoff) cutoff = rd; }
	}
	let cur = 0, prev = 0, hasPrev = false;
	for (const s of sources) for (const p of s.pts) {
		const yr = yearOf(p.date);
		if (yr === cy) cur += metricVal(p, m);
		else if (yr === cy - 1) { hasPrev = true; if (refDate(p.date) <= cutoff) prev += metricVal(p, m); }
	}
	if (!hasPrev || prev <= 0) return null;
	return (cur / prev - 1) * 100;
}

function rangeScoped(): boolean {
	return Boolean(range.from || range.to);
}

export function updateHeadline(sources: Source[], empty = false): void {
	const valueEl = document.getElementById('cum-headline-value');
	const captionEl = document.getElementById('cum-headline-caption');
	const metrics = selectedMetrics();

	if (empty) {
		if (valueEl) valueEl.textContent = '—';
		if (captionEl) captionEl.textContent = '';
		return;
	}

	if (valueEl) {
		const multi = metrics.length > 1;
		// eslint-disable-next-line no-unsanitized/property -- METRICS format/unit + METRIC_COLORS are internal const maps; values numeric
		valueEl.innerHTML = metrics.map((m, i) => {
			const v = METRICS[m].format(metricTotal(m, sources));
			const unit = METRICS[m].unit ? `<span class="cum-metric-unit">${METRICS[m].unit}</span>` : '';
			const cls = i === 0 ? '' : ' class="cum-metric-extra"';
			const style = multi ? ` style="color:${METRIC_COLORS[m]}"` : '';
			return `<span${cls}${style}>${v}${unit}</span>`;
		}).join('');
	}

	if (captionEl) {
		if (state.chartType === 'bar') {
			const period = state.bucket === 'week' ? 'Weekly' : 'Monthly';
			const scope = state.view === 'yoy' ? 'year over year' : rangeScoped() ? 'selected range' : 'all-time';
			captionEl.textContent = `${period} totals · ${scope}`;
		} else if (state.view === 'yoy') {
			const cy = currentYearAcross(sources);
			const pct = yoyGrowthPct(must(metrics[0], 'at least one metric selected'), sources);
			let caption = cy != null ? `${cy} year to date` : 'Year to date';
			if (pct != null) {
				const cls = pct >= 0 ? 'up' : 'down';
				const arrow = pct >= 0 ? '▲' : '▼';
				caption += ` · <span class="${cls}">${arrow}${Math.abs(Math.round(pct))}%</span>`;
			}
			// eslint-disable-next-line no-unsanitized/property -- caption built above from numeric year/pct + literal arrows/classes
			captionEl.innerHTML = caption;
		} else {
			captionEl.textContent = rangeScoped() ? 'Selected range' : 'All-time total';
		}
	}
}
