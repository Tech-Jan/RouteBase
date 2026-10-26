/**
 * Shared cell-value formatters for grid integrations — one place for the number/date/duration
 * conventions every table uses, so new grids get them for free.
 */

/** Shared `Intl.NumberFormat` instances (constructing them is expensive — reuse, don't re-create). */
export const N0 = new Intl.NumberFormat('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 0 });
export const N1 = new Intl.NumberFormat('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
export const N2 = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** Formats a nullable numeric cell (blank when null), optionally unit-scaled (e.g. m→km ×0.001). */
export function num(v: number | null | undefined, f: Intl.NumberFormat, scale = 1): string {
	return v == null ? '' : f.format(v * scale);
}

export function pad2(n: number): string {
	return String(n).padStart(2, '0');
}

/** dd.mm.yyyy from an ISO date string; blank for null/invalid. */
export function formatDateDMY(iso: string | null | undefined): string {
	if (!iso) return '';
	const d = new Date(iso);
	if (Number.isNaN(d.getTime())) return '';
	return `${pad2(d.getDate())}.${pad2(d.getMonth() + 1)}.${d.getFullYear()}`;
}

/** HH:MM:SS from total seconds (null/negative → 00:00:00). */
export function formatDuration(totalSec: number | null | undefined): string {
	const t = Math.max(0, Math.floor(totalSec ?? 0));
	return `${pad2(Math.floor(t / 3600))}:${pad2(Math.floor((t / 60) % 60))}:${pad2(t % 60)}`;
}
