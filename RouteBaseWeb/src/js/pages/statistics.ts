// statistics.ts — Statistics page entry. Wires the shared filter header (owner/type/date) to the three
// client-rendered widgets — summary KPIs, by-type boxes, cumulative chart — through one data layer.
// All rendering/logic lives in ../statistics/*; this file only bootstraps and connects.

import { createStatsFilterBar } from '../statistics/statistics-filters';
import { createStatsData } from '../statistics/statistics-data';
import { renderStatsSummary } from '../statistics/statistics-summary';
import { renderStatsByType } from '../statistics/statistics-by-type';
import { initStatsChartControls, setStatsChartData, type StatsChartRange } from '../statistics/statistics-chart';
import { buildStatsColumns, DATE_KEY } from '../statistics/statistics-filter-schema';
import { readIsland } from '../core/page-bootstrap';
import type { StatisticsBootstrap } from '../statistics/statistics-types';
import type { GridFilterState } from '../core/grid/grid-filter-state';

// The header date filter, in the shape the chart uses to clamp its range-view x-axis.
function currentRange(state: GridFilterState): StatsChartRange {
	const d = state.getFilter(DATE_KEY);
	return { from: d?.from || undefined, to: d?.to || undefined };
}

function init(): void {
	const boot = readIsland<StatisticsBootstrap | null>('statisticsBootstrap', null);
	const mount = document.getElementById('statsFilterBar');
	if (!boot || !mount) return;

	const state = createStatsFilterBar(mount, boot);
	const columns = buildStatsColumns(boot.typeOptions, boot.ownerOptions);
	const data = createStatsData(state, columns);

	data.onSummary((s) => {
		setStatsError(false); // a good render arrived — clear any earlier first-load failure
		renderStatsSummary(s.overall);
		renderStatsByType(s.byType);
	});
	data.onCumulative((points) => setStatsChartData(points, currentRange(state)));
	// FUX-06: a first-load fetch failure used to leave every KPI/chart shimmering at "—" with only a
	// console warning. Surface a quiet, retry-able message instead of a permanent skeleton.
	data.onError(() => setStatsError(true));

	initStatsChartControls();
	data.refresh(); // initial load for the default (Me) filter state
}

/** Show/hide a page-level "couldn't load" banner below the filter bar (created lazily, once). */
function setStatsError(show: boolean): void {
	let el = document.getElementById('statsError');
	if (!el) {
		if (!show) return;
		el = document.createElement('div');
		el.id = 'statsError';
		el.className = 'mono';
		el.setAttribute('role', 'alert');
		el.style.cssText = 'padding:16px;text-align:center;color:var(--fg-2);font-size:13px;';
		el.textContent = "Couldn't load statistics. Check your connection and refresh.";
		document.getElementById('statsFilterBar')?.after(el);
	}
	el.hidden = !show;
}

if (document.readyState === 'loading') {
	document.addEventListener('DOMContentLoaded', init);
} else {
	init();
}

export {};
