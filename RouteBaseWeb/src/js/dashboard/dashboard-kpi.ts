// dashboard-kpi.ts — top KPI strip: the month selected in the calendar, over the feed's filtered rows.

import { MONTH_NAMES } from './dashboard-shared';
import { computeKpi, type DashboardKpi as KpiData } from './dashboard-stats';
import type { DashboardGridRow } from '../generated/contracts';

function renderTrendBadge(trendPercent: number): string {
	if (Math.abs(trendPercent) < 0.1) return '<span class="kpi-trend flat mono">—</span>';
	const arrow = trendPercent > 0 ? "▲" : "▼";
	const cls   = trendPercent > 0 ? "up" : "down";
	return `<span class="kpi-trend ${cls} mono">${arrow} ${Math.abs(trendPercent).toFixed(1)}%</span>`;
}

function formatMovingTimeHtml(minutes: number): string {
	const h = Math.floor(minutes / 60);
	const m = Math.floor(minutes % 60);
	if (h > 0)
		return `${h}<span style='font-size:18px;'>h</span>${String(m).padStart(2, "0")}`;
	return `${m}<span style='font-size:18px;'>m</span>`;
}

function updateKpiStrip(data: KpiData, periodLabel: string): void {
	const patches: Array<{
		labelId: string; rowId: string; trendId: string;
		label: string; rowHtml: string; trendPercent: number;
	}> = [
		{
			labelId: "kpi-label-distance", rowId: "kpi-value-row-distance", trendId: "kpi-trend-distance",
			label: "Distance",
			rowHtml: `<span class="kpi-value mono tnum" style="color:var(--acc-cyan);">${Math.round(data.distance.value).toLocaleString()}</span><span class="kpi-unit mono">km</span>`,
			trendPercent: data.distance.trendPercent,
		},
		{
			labelId: "kpi-label-time", rowId: "kpi-value-row-time", trendId: "kpi-trend-time",
			label: "Moving time",
			rowHtml: `<span class="kpi-value mono tnum" style="color:var(--fg-0);">${formatMovingTimeHtml(data.movingTime.value)}</span>`,
			trendPercent: data.movingTime.trendPercent,
		},
		{
			labelId: "kpi-label-elevation", rowId: "kpi-value-row-elevation", trendId: "kpi-trend-elevation",
			label: "Elevation",
			rowHtml: `<span class="kpi-value mono tnum" style="color:var(--acc-lime);">${Math.round(data.elevation.value).toLocaleString()}</span><span class="kpi-unit mono">m</span>`,
			trendPercent: data.elevation.trendPercent,
		},
		{
			labelId: "kpi-label-hr", rowId: "kpi-value-row-hr", trendId: "kpi-trend-hr",
			label: "Avg HR",
			rowHtml: data.avgHR.value > 0
				? `<span class="kpi-value mono tnum" style="color:var(--acc-amber);">${Math.round(data.avgHR.value).toLocaleString()}</span><span class="kpi-unit mono">bpm</span>`
				: `<span class="kpi-value mono tnum" style="color:var(--acc-amber);">—</span>`,
			trendPercent: data.avgHR.trendPercent,
		},
		{
			labelId: "kpi-label-activities", rowId: "kpi-value-row-activities", trendId: "kpi-trend-activities",
			label: "Activities",
			rowHtml: `<span class="kpi-value mono tnum" style="color:var(--fg-1);">${Math.round(data.activityCount.value)}</span>`,
			trendPercent: data.activityCount.trendPercent,
		},
	];

	const periodEl = document.getElementById("kpi-period-label");
	if (periodEl) periodEl.textContent = periodLabel;

	for (const p of patches) {
		const labelEl = document.getElementById(p.labelId);
		const rowEl   = document.getElementById(p.rowId);
		const trendEl = document.getElementById(p.trendId);
		if (labelEl) labelEl.textContent = p.label;
		// eslint-disable-next-line no-unsanitized/property -- rowHtml is an internal template built above from numeric KPI values only
		if (rowEl)   rowEl.innerHTML = p.rowHtml;
		// eslint-disable-next-line no-unsanitized/property -- renderTrendBadge interpolates a numeric percent + literal arrows only
		if (trendEl) trendEl.innerHTML = renderTrendBadge(p.trendPercent);
	}
}

/** Paints the strip for the calendar's month, labelled with it and trended against the month before. */
export function renderKpi(rows: DashboardGridRow[], year: number, month: number): void {
	updateKpiStrip(computeKpi(rows, year, month), `${MONTH_NAMES[month - 1]} ${year}`);
}
