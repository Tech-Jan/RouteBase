/**
 * dashboard-stats.ts — the dashboard's numbers, computed from the feed grid's FILTERED rows.
 *
 * Pure: no DOM, no fetch. The page bootstrap ships every visible activity (Pages/Index.cshtml.cs,
 * unpaginated), so the grid's filtered set is a complete input and every filter — visibility, shared,
 * name, date, distance, HR — reaches the KPI strip, the weekly bars and the calendar. That's the point:
 * the tiles describe exactly the cards below them.
 *
 * Buckets by the row's LOCAL start date (`dateIso`), the same timestamp the cards print and the Date
 * filter matches. Rows without one belong to no day and are dropped from all three widgets.
 */

import type { DashboardGridRow } from '../generated/contracts';
import { must } from '../core/invariant';

// ─── Payload shapes ─────────────────────────────────────────────────────────
// Client-only: these used to arrive from /api/statistics/{kpi,weekly-volume,heatmap}, which no longer
// exist. Field names are unchanged from those DTOs so the renderers stayed as they were.

export interface KpiMetric {
	value: number;
	trendPercent: number;
}

export interface DashboardKpi {
	distance: KpiMetric;
	movingTime: KpiMetric;
	elevation: KpiMetric;
	avgHR: KpiMetric;
	activityCount: KpiMetric;
}

export interface WeeklyVolumeEntry {
	/** ISO date (YYYY-MM-DD) of the week's Monday. */
	weekStart: string;
	weekLabel: string;
	distanceKm: number;
	movingTimeMin: number;
	activityCount: number;
	byType: Record<string, number>;
}

export interface WeeklyVolume {
	weeks: WeeklyVolumeEntry[];
	avgWeeklyDistanceKm: number;
	longestWeekKm: number;
	currentStreakWeeks: number;
	totalElevationM: number;
}

export interface HeatmapDayActivity {
	id: number;
	name: string;
	typeGroup: string;
	distanceKm: number | null;
	durationSeconds: number | null;
}

export interface HeatmapDay {
	/** YYYY-MM-DD, local. */
	date: string;
	activityCount: number;
	activityTypes: string[];
	activities: HeatmapDayActivity[];
}

// ─── Local-day helpers ──────────────────────────────────────────────────────

/** Local calendar day of a row, or null when the row carries no start timestamp. */
export function rowDate(row: DashboardGridRow): Date | null {
	if (!row.dateIso) return null;
	const d = new Date(row.dateIso);
	return Number.isNaN(d.getTime()) ? null : d;
}

/** YYYY-MM-DD in local time — `toISOString()` would shift the day for every non-UTC viewer. */
export function localDayIso(d: Date): string {
	return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** Monday 00:00 local of the ISO week containing `date`. */
export function isoWeekMonday(date: Date): Date {
	const d = new Date(date);
	const day = d.getDay(); // 0=Sun … 6=Sat
	d.setDate(d.getDate() + (day === 0 ? -6 : 1 - day));
	d.setHours(0, 0, 0, 0);
	return d;
}

/** ISO-8601 week number (weeks start Monday; week 1 holds the first Thursday). */
export function isoWeekNumber(monday: Date): number {
	const thursday = new Date(monday);
	thursday.setDate(monday.getDate() + 3);
	const jan1 = new Date(thursday.getFullYear(), 0, 1);
	const days = Math.round((thursday.getTime() - jan1.getTime()) / 86_400_000);
	return Math.floor(days / 7) + 1;
}

function sum(rows: DashboardGridRow[], pick: (r: DashboardGridRow) => number | null | undefined): number {
	return rows.reduce((acc, r) => acc + (pick(r) ?? 0), 0);
}

function round(value: number, decimals: number): number {
	const f = 10 ** decimals;
	return Math.round(value * f) / f;
}

// ─── KPI strip ──────────────────────────────────────────────────────────────

function inMonth(rows: DashboardGridRow[], year: number, month: number): DashboardGridRow[] {
	return rows.filter((r) => {
		const d = rowDate(r);
		return d !== null && d.getFullYear() === year && d.getMonth() + 1 === month;
	});
}

/** A metric plus its change against the previous period. No previous data → +100% if there is any now. */
function metric(current: number, previous: number): KpiMetric {
	return {
		value: round(current, 1),
		trendPercent: previous === 0
			? (current > 0 ? 100 : 0)
			: round(((current - previous) / previous) * 100, 1),
	};
}

/** Mean of the per-activity averages, over the rows that recorded HR. 0 when none did. */
function avgHr(rows: DashboardGridRow[]): number {
	const withHr = rows.filter((r) => r.heartRateAvg != null);
	if (withHr.length === 0) return 0;
	return round(sum(withHr, (r) => r.heartRateAvg) / withHr.length, 1);
}

/** KPIs for the selected calendar month, trended against the month before it. */
export function computeKpi(rows: DashboardGridRow[], year: number, month: number): DashboardKpi {
	const prev = month === 1 ? { year: year - 1, month: 12 } : { year, month: month - 1 };
	const cur = inMonth(rows, year, month);
	const before = inMonth(rows, prev.year, prev.month);

	return {
		distance: metric(sum(cur, (r) => r.distanceMeters) / 1000, sum(before, (r) => r.distanceMeters) / 1000),
		movingTime: metric(sum(cur, (r) => r.timeMovingSeconds) / 60, sum(before, (r) => r.timeMovingSeconds) / 60),
		elevation: metric(sum(cur, (r) => r.ascentMeters), sum(before, (r) => r.ascentMeters)),
		avgHR: metric(avgHr(cur), avgHr(before)),
		activityCount: metric(cur.length, before.length),
	};
}

// ─── Weekly volume ──────────────────────────────────────────────────────────

const VOLUME_WEEKS = 12;

/**
 * The 12-week window ending `weeksOffset` weeks before the current one, oldest first, empty weeks
 * included. `today` is injectable so the window is testable without freezing the clock.
 */
export function computeWeeklyVolume(
	rows: DashboardGridRow[], weeksOffset: number, today: Date = new Date(),
): WeeklyVolume {
	const endMonday = isoWeekMonday(today);
	endMonday.setDate(endMonday.getDate() - weeksOffset * 7);

	const byWeek = new Map<string, DashboardGridRow[]>();
	for (const row of rows) {
		const d = rowDate(row);
		if (!d) continue;
		const key = localDayIso(isoWeekMonday(d));
		const bucket = byWeek.get(key);
		if (bucket) bucket.push(row);
		else byWeek.set(key, [row]);
	}

	const weeks: WeeklyVolumeEntry[] = [];
	for (let i = 0; i < VOLUME_WEEKS; i++) {
		const monday = new Date(endMonday);
		monday.setDate(endMonday.getDate() - (VOLUME_WEEKS - 1 - i) * 7);
		const weekRows = byWeek.get(localDayIso(monday)) ?? [];

		const byType: Record<string, number> = {};
		for (const r of weekRows) {
			byType[r.typeGroup] = (byType[r.typeGroup] ?? 0) + (r.distanceMeters ?? 0) / 1000;
		}
		for (const [type, v] of Object.entries(byType)) byType[type] = round(v, 1);

		weeks.push({
			weekStart: localDayIso(monday),
			weekLabel: `W${isoWeekNumber(monday)}`,
			distanceKm: round(sum(weekRows, (r) => r.distanceMeters) / 1000, 1),
			movingTimeMin: Math.round(sum(weekRows, (r) => r.timeMovingSeconds) / 60),
			activityCount: weekRows.length,
			byType,
		});
	}

	const active = weeks.filter((w) => w.activityCount > 0);
	const windowRows = weeks.flatMap((w) => byWeek.get(w.weekStart) ?? []);

	return {
		weeks,
		avgWeeklyDistanceKm: active.length === 0 ? 0 : round(active.reduce((s, w) => s + w.distanceKm, 0) / active.length, 1),
		longestWeekKm: active.length === 0 ? 0 : Math.max(...active.map((w) => w.distanceKm)),
		// Anchored to the current week, so a scrolled-back window would always report 0 anyway.
		currentStreakWeeks: weeksOffset === 0 ? weekStreak(weeks, today) : 0,
		totalElevationM: Math.round(sum(windowRows, (r) => r.ascentMeters)),
	};
}

/** Consecutive active weeks counting back from the current one. */
function weekStreak(weeks: WeeklyVolumeEntry[], today: Date): number {
	const thisMonday = isoWeekMonday(today);
	let streak = 0;
	for (let i = weeks.length - 1; i >= 0; i--) {
		const expected = new Date(thisMonday);
		expected.setDate(thisMonday.getDate() - streak * 7);
		const w = must(weeks[i], 'weeks[i] (loop-bounded)');
		if (w.weekStart === localDayIso(expected) && w.activityCount > 0) streak++;
		else break;
	}
	return streak;
}

// ─── Calendar heatmap ───────────────────────────────────────────────────────

/** One entry per day of the selected month that has activities, oldest first. */
export function computeHeatmapDays(rows: DashboardGridRow[], year: number, month: number): HeatmapDay[] {
	const byDay = new Map<string, DashboardGridRow[]>();
	for (const row of inMonth(rows, year, month)) {
		const iso = localDayIso(must(rowDate(row), 'inMonth yields only dated rows'));
		const bucket = byDay.get(iso);
		if (bucket) bucket.push(row);
		else byDay.set(iso, [row]);
	}

	return [...byDay.entries()]
		.sort(([a], [b]) => a.localeCompare(b))
		.map(([date, dayRows]) => {
			const activities: HeatmapDayActivity[] = dayRows.map((r) => ({
				id: r.id,
				name: r.name ?? '',
				typeGroup: r.typeGroup,
				distanceKm: r.distanceMeters == null ? null : r.distanceMeters / 1000,
				durationSeconds: r.timeMovingSeconds == null ? null : Math.round(r.timeMovingSeconds),
			}));
			return {
				date,
				activityCount: activities.length,
				activityTypes: [...new Set(activities.map((a) => a.typeGroup))].sort(),
				activities,
			};
		});
}
