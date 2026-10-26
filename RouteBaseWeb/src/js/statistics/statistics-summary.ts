/**
 * Summary KPI widget: fills the server-rendered KPI skeleton in place from the API `overall` totals.
 * Each value span carries a `data-stat-kpi="<key>"` hook and starts as an "—" placeholder; this only
 * writes the numeric text (units are static in the markup), so it never rebuilds the layout.
 */

import type { ActivityStatsDto } from './statistics-data';

/** Renders each `[data-stat-kpi]` value from `overall`. Scans within `root` (defaults to document). */
export function renderStatsSummary(overall: ActivityStatsDto, root: ParentNode = document): void {
	const speedKmh = overall.averageSpeed * 3.6;
	const values: Record<string, string> = {
		activities: n0(overall.totalActivities),
		distance: n0(overall.totalDistance / 1000),
		ascent: n0(overall.totalAscent),
		descent: n0(overall.totalDescent),
		totalTime: n0(Math.floor(overall.totalTime / 3600)),
		movingTime: n0(Math.floor(overall.totalMovingTime / 3600)),
		avgSpeed: n1(speedKmh),
		avgPace: paceStr(speedKmh),
	};

	for (const [key, text] of Object.entries(values)) {
		const el = root.querySelector<HTMLElement>(`[data-stat-kpi="${key}"]`);
		if (el) el.textContent = text;
	}
}

function n0(v: number): string {
	return Math.round(v).toLocaleString(undefined, { maximumFractionDigits: 0 });
}

function n1(v: number): string {
	return v.toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

// Minutes-per-km from km/h — the TS port of the Razor `PaceStr` helper.
export function paceStr(speedKmh: number): string {
	if (speedKmh <= 0) return '--';
	const totalSeconds = Math.round((60 / speedKmh) * 60);
	const mins = Math.floor(totalSeconds / 60);
	const secs = totalSeconds % 60;
	return `${mins}:${String(secs).padStart(2, '0')}`;
}
