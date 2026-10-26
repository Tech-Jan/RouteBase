/**
 * By-type widget: renders one `.stat-panel` per raw activity type into `#statsTypeGrid` from the API
 * `byType` breakdown, and toggles the server-rendered `#statsTypeEmpty` empty state. Mirrors the original
 * Razor panel markup/formatting, adds a Descent row, and escapes the (user-supplied) type names.
 */

import type { TypeStatsDto } from './statistics-data';
import { paceStr } from './statistics-summary';
import { escapeHtml } from '../core/html';

const GRID_ID = 'statsTypeGrid';
const EMPTY_ID = 'statsTypeEmpty';

export function renderStatsByType(byType: TypeStatsDto[]): void {
	const grid = document.getElementById(GRID_ID);
	const empty = document.getElementById(EMPTY_ID);
	if (!grid) return;

	if (byType.length === 0) {
		grid.innerHTML = '';
		grid.style.display = 'none';
		if (empty) empty.style.display = '';
		return;
	}

	grid.style.display = '';
	if (empty) empty.style.display = 'none';
	// eslint-disable-next-line no-unsanitized/property -- panel() escapeHtml's t.type; remaining interpolants are numeric stats
	grid.innerHTML = byType.map(panel).join('');
}

function panel(t: TypeStatsDto): string {
	const s = t.stats;
	const speedKmh = s.averageSpeed * 3.6;
	const rows = [
		row('Activities', String(s.totalActivities)),
		row('Distance', `${n2(s.totalDistance / 1000)} km`),
		row('Ascent', `${n0(s.totalAscent)} m`),
		row('Descent', `${n0(s.totalDescent)} m`),
		row('Total Time', `${n0(Math.floor(s.totalTime / 3600))}h`),
		row('Avg Speed', `${n1(speedKmh)} km/h`),
		row('Avg Pace', `${paceStr(speedKmh)} min/km`),
	].join('');

	return `<div class="stat-panel">
		<div class="stat-panel-header">${escapeHtml(t.type)}</div>
		<div class="stat-panel-body">${rows}</div>
	</div>`;
}

function row(label: string, value: string): string {
	return `<div class="stat-row"><span class="stat-row-label">${label}</span><span class="stat-row-value">${value}</span></div>`;
}

function n0(v: number): string {
	return Math.round(v).toLocaleString(undefined, { maximumFractionDigits: 0 });
}
function n1(v: number): string {
	return v.toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}
function n2(v: number): string {
	return v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}
