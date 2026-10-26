/**
 * shape-filter/grid-filters-format.ts — compact human summary of adopted activities-table filters.
 *
 * Pure formatting, display-only: turns a GridFiltersMap into a short clause for the rule summary
 * sentence ("distance 10-20 km, name morning, type Run/Ride"). The column keys + display units
 * mirror the grid schema in `overview/activities-grid.ts` (and the backend `ActivityGridColumns`);
 * this map is a label lookup only — it never drives matching, so drift here is cosmetic.
 */

import type { ColumnFilterDto, GridFiltersMap } from './rule-document';

const LABELS: Record<string, { label: string; unit?: string }> = {
	date: { label: 'date' },
	name: { label: 'name' },
	type: { label: 'type' },
	distance: { label: 'distance', unit: 'km' },
	ascent: { label: 'ascent', unit: 'm' },
	descent: { label: 'descent', unit: 'm' },
	time: { label: 'time', unit: 'min' },
	movingtime: { label: 'moving time', unit: 'min' },
	avgspeed: { label: 'avg speed', unit: 'km/h' },
	maxspeed: { label: 'max speed', unit: 'km/h' },
	avghr: { label: 'avg HR' },
	maxhr: { label: 'max HR' },
	avgcadence: { label: 'avg cadence' },
	avgpower: { label: 'avg power', unit: 'W' },
	photos: { label: 'photos' },
};

/** One-line summary of the adopted filters, or '' when there are none. */
export function formatGridFilters(filters: GridFiltersMap | null | undefined): string {
	if (!filters) return '';
	const parts: string[] = [];
	for (const [key, f] of Object.entries(filters)) {
		const meta = LABELS[key] ?? { label: key };
		const value = formatValue(f, meta.unit);
		if (value) parts.push(`${meta.label} ${value}`);
	}
	return parts.join(', ');
}

/** Render one column's filter value; range/date carry the display unit, text/select do not. */
function formatValue(f: ColumnFilterDto, unit?: string): string {
	const suffix = unit ? ` ${unit}` : '';
	if (f.range && f.range.trim()) return `${f.range.trim()}${suffix}`;
	// Legacy two-box bounds still constrain matching server-side (SP-09) — render them like from/to.
	if (f.min || f.max) return `${f.min ?? '…'}–${f.max ?? '…'}${suffix}`;
	if (f.from || f.to) return `${f.from ?? '…'}–${f.to ?? '…'}`;
	if (f.in && f.in.length) return f.in.join('/');
	if (f.text && f.text.trim()) return f.text.trim();
	return '';
}
