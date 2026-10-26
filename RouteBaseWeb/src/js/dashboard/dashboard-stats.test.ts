import { describe, it, expect, beforeEach } from 'vitest';
import { must } from '../core/invariant';
import { computeKpi, computeWeeklyVolume, computeHeatmapDays } from './dashboard-stats';
import { renderKpi } from './dashboard-kpi';
import type { DashboardGridRow } from '../generated/contracts';

// The dashboard's numbers come from the feed grid's FILTERED rows — these pin what each widget
// makes of a given set, so "filter the feed → the tiles follow" is a property of the data, not of
// a request the server happened to scope.

function row(over: Partial<DashboardGridRow> & { dateIso: string | null }): DashboardGridRow {
	return {
		id: 1, name: 'Ride', activityType: 'cycling', typeGroup: 'bike',
		distanceMeters: 10_000, ascentMeters: 100, timeTotalSeconds: 3600, timeMovingSeconds: 3000,
		speedAvgMs: 5, heartRateAvg: null, visibility: 'private', owner: 'Me',
		isShared: false, wasShared: false,
		...over,
	};
}

// Fri 24 Jul 2026 — its ISO week starts Mon 20 Jul.
const TODAY = new Date(2026, 6, 24);

describe('computeKpi', () => {
	it('totals the selected month only and trends against the one before', () => {
		const rows = [
			row({ dateIso: '2026-07-02T09:00:00', distanceMeters: 30_000, ascentMeters: 400, timeMovingSeconds: 3600 }),
			row({ dateIso: '2026-07-20T09:00:00', distanceMeters: 20_000, ascentMeters: 100, timeMovingSeconds: 1800 }),
			row({ dateIso: '2026-06-15T09:00:00', distanceMeters: 25_000, ascentMeters: 250, timeMovingSeconds: 2700 }),
		];

		const kpi = computeKpi(rows, 2026, 7);

		expect(kpi.distance.value).toBe(50);
		expect(kpi.elevation.value).toBe(500);
		expect(kpi.movingTime.value).toBe(90);
		expect(kpi.activityCount.value).toBe(2);
		expect(kpi.distance.trendPercent).toBe(100); // 50 km vs 25 km
		expect(kpi.activityCount.trendPercent).toBe(100); // 2 vs 1
	});

	it('crosses the year boundary for January\'s previous month', () => {
		const rows = [
			row({ dateIso: '2026-01-10T09:00:00', distanceMeters: 10_000 }),
			row({ dateIso: '2025-12-10T09:00:00', distanceMeters: 20_000 }),
		];

		expect(computeKpi(rows, 2026, 1).distance.trendPercent).toBe(-50);
	});

	it('averages HR over the rows that recorded it, and reports none as —-worthy zero', () => {
		const rows = [
			row({ dateIso: '2026-07-02T09:00:00', heartRateAvg: 140 }),
			row({ dateIso: '2026-07-03T09:00:00', heartRateAvg: 160 }),
			row({ dateIso: '2026-07-04T09:00:00', heartRateAvg: null }), // no HR strap — not a zero
		];

		expect(computeKpi(rows, 2026, 7).avgHR.value).toBe(150);
		expect(computeKpi([row({ dateIso: '2026-07-02T09:00:00' })], 2026, 7).avgHR.value).toBe(0);
	});

	it('reports zeros and a flat trend for a month with nothing in it', () => {
		const kpi = computeKpi([], 2026, 7);

		expect(kpi.distance.value).toBe(0);
		expect(kpi.distance.trendPercent).toBe(0); // no data either side — flat, not +100%
	});

	it('drops rows that carry no start date', () => {
		const rows = [row({ dateIso: null, distanceMeters: 99_000 }), row({ dateIso: '2026-07-02T09:00:00' })];

		expect(computeKpi(rows, 2026, 7).activityCount.value).toBe(1);
	});

	// The bug this slice exists to fix: the tiles used to ignore the Visibility filter.
	it('follows the filtered set — hiding friends-only rows drops them from the tiles', () => {
		const all = [
			row({ id: 1, dateIso: '2026-07-02T09:00:00', distanceMeters: 30_000, visibility: 'private' }),
			row({ id: 2, dateIso: '2026-07-05T09:00:00', distanceMeters: 20_000, visibility: 'friends' }),
		];
		const privateOnly = all.filter((r) => r.visibility === 'private');

		expect(computeKpi(all, 2026, 7).distance.value).toBe(50);
		expect(computeKpi(privateOnly, 2026, 7).distance.value).toBe(30);
		expect(computeKpi(privateOnly, 2026, 7).activityCount.value).toBe(1);
	});
});

describe('computeWeeklyVolume', () => {
	it('fills all 12 week slots, oldest first, including the empty ones', () => {
		const vol = computeWeeklyVolume([row({ dateIso: '2026-07-21T09:00:00' })], 0, TODAY);

		expect(vol.weeks).toHaveLength(12);
		const w0 = must(vol.weeks[0], 'week 0 of 12');
		const w11 = must(vol.weeks[11], 'week 11 of 12');
		expect(w0.weekStart).toBe('2026-05-04');
		expect(w11.weekStart).toBe('2026-07-20');
		expect(w11.weekLabel).toBe('W30');
		expect(w0.activityCount).toBe(0);
		expect(w0.byType).toEqual({});
	});

	it('stacks a week by type group and totals its distance, time and elevation', () => {
		const rows = [
			row({ dateIso: '2026-07-21T09:00:00', typeGroup: 'bike', distanceMeters: 40_000, ascentMeters: 300, timeMovingSeconds: 3600 }),
			row({ dateIso: '2026-07-22T09:00:00', typeGroup: 'run', distanceMeters: 10_000, ascentMeters: 100, timeMovingSeconds: 1800 }),
		];

		const week = must(computeWeeklyVolume(rows, 0, TODAY).weeks[11], 'week 11 of 12');

		expect(week.byType).toEqual({ bike: 40, run: 10 });
		expect(week.distanceKm).toBe(50);
		expect(week.movingTimeMin).toBe(90);
		expect(week.activityCount).toBe(2);
		expect(computeWeeklyVolume(rows, 0, TODAY).totalElevationM).toBe(400);
	});

	it('averages over active weeks only and reports the longest', () => {
		const vol = computeWeeklyVolume([
			row({ dateIso: '2026-07-21T09:00:00', distanceMeters: 60_000 }),
			row({ dateIso: '2026-07-14T09:00:00', distanceMeters: 20_000 }),
		], 0, TODAY);

		expect(vol.avgWeeklyDistanceKm).toBe(40); // (60 + 20) / 2 active weeks, not / 12
		expect(vol.longestWeekKm).toBe(60);
	});

	it('counts the streak back from the current week, and reports none for a scrolled-back window', () => {
		const rows = [
			row({ dateIso: '2026-07-21T09:00:00' }), // week of Mon 20 Jul — the current one
			row({ dateIso: '2026-07-15T09:00:00' }), // week of Mon 13 Jul
		];

		expect(computeWeeklyVolume(rows, 0, TODAY).currentStreakWeeks).toBe(2);
		expect(computeWeeklyVolume(rows, 4, TODAY).currentStreakWeeks).toBe(0);
	});

	it('breaks the streak at a week with nothing in it', () => {
		const rows = [
			row({ dateIso: '2026-07-21T09:00:00' }),
			row({ dateIso: '2026-07-08T09:00:00' }), // skips the week of Mon 13 Jul
		];

		expect(computeWeeklyVolume(rows, 0, TODAY).currentStreakWeeks).toBe(1);
	});
});

describe('computeHeatmapDays', () => {
	it('buckets by the activity\'s LOCAL day, the one its card prints', () => {
		// 23:30 on 30 Jun local is already July in UTC — the old server bucketing put it in the wrong month.
		const rows = [row({ dateIso: '2026-06-30T23:30:00' }), row({ dateIso: '2026-07-01T00:30:00' })];

		expect(computeHeatmapDays(rows, 2026, 6).map((d) => d.date)).toEqual(['2026-06-30']);
		expect(computeHeatmapDays(rows, 2026, 7).map((d) => d.date)).toEqual(['2026-07-01']);
	});

	it('lists a day\'s activities with the fields the popup renders', () => {
		const rows = [row({ id: 42, name: 'Evening ride', dateIso: '2026-07-02T18:00:00', distanceMeters: 12_500, timeMovingSeconds: 2400 })];

		const day = must(computeHeatmapDays(rows, 2026, 7)[0], 'single fixture day');

		expect(day.activityCount).toBe(1);
		expect(day.activities[0]).toEqual({
			id: 42, name: 'Evening ride', typeGroup: 'bike', distanceKm: 12.5, durationSeconds: 2400,
		});
	});

	it('reports one distinct sorted type list per day, and orders days oldest first', () => {
		const rows = [
			row({ dateIso: '2026-07-09T09:00:00', typeGroup: 'run' }),
			row({ dateIso: '2026-07-02T09:00:00', typeGroup: 'run' }),
			row({ dateIso: '2026-07-02T17:00:00', typeGroup: 'bike' }),
		];

		const days = computeHeatmapDays(rows, 2026, 7);

		expect(days.map((d) => d.date)).toEqual(['2026-07-02', '2026-07-09']);
		expect(must(days[0], 'day 0').activityTypes).toEqual(['bike', 'run']);
		expect(must(days[1], 'day 1').activityTypes).toEqual(['run']);
	});
});

describe('renderKpi', () => {
	beforeEach(() => {
		document.body.innerHTML = ['distance', 'time', 'elevation', 'hr', 'activities']
			.map((k) => `<div id="kpi-label-${k}"></div><div id="kpi-value-row-${k}">—</div><span id="kpi-trend-${k}"></span>`)
			.join('') + '<span id="kpi-period-label"></span>';
	});

	it('fills the strip from the rows and labels it with the selected month', () => {
		renderKpi([row({ dateIso: '2026-07-02T09:00:00', distanceMeters: 30_000 })], 2026, 7);

		expect(document.getElementById('kpi-period-label')!.textContent).toBe('July 2026');
		expect(document.getElementById('kpi-value-row-distance')!.textContent).toContain('30');
		expect(document.getElementById('kpi-trend-distance')!.innerHTML).toContain('▲');
		expect(document.getElementById('kpi-value-row-hr')!.textContent).toContain('—'); // no HR recorded
	});
});
