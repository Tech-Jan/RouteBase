// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { dateParts, dayUTC, yearOf, ingest, currentSources, floorUTCDay } from './statistics-chart-store';
import { rangeExtentMs } from './statistics-chart-series';

function point(date: string) {
	return { date, type: 'run', distanceKm: 1, elevationM: 0, movingTimeMin: 0, count: 1 };
}

// ST-01: the server sends a date-only "YYYY-MM-DDT00:00:00" string with no timezone. Parsing it via
// new Date() yields LOCAL midnight; reading it back with getUTC*() then shifts the day for every UTC+
// browser. These pure string-slice helpers must keep the calendar day in ANY timezone.

describe('statistics-chart date helpers (ST-01)', () => {
	it('dayUTC keeps the calendar day in a UTC+ timezone', () => {
		process.env.TZ = 'Pacific/Auckland'; // UTC+12/+13
		expect(dayUTC({ date: '2026-07-10T00:00:00', distanceKm: 0, elevationM: 0, movingTimeMin: 0, count: 0 })).toBe(
			Date.UTC(2026, 6, 10),
		);
	});

	it('dayUTC is unaffected in a UTC- timezone (the DT-02 half stays fixed too)', () => {
		process.env.TZ = 'America/New_York'; // UTC-5/-4
		expect(dayUTC({ date: '2026-07-10T00:00:00', distanceKm: 0, elevationM: 0, movingTimeMin: 0, count: 0 })).toBe(
			Date.UTC(2026, 6, 10),
		);
	});

	it('yearOf does not roll a Jan-1 point into the previous year under UTC+', () => {
		process.env.TZ = 'Pacific/Auckland';
		expect(yearOf('2026-01-01T00:00:00')).toBe(2026);
	});

	it('buckets a Dec-31/Jan-1 pair into two distinct years under UTC+13', () => {
		process.env.TZ = 'Pacific/Auckland';
		expect(yearOf('2025-12-31T00:00:00')).toBe(2025);
		expect(yearOf('2026-01-01T00:00:00')).toBe(2026);
		expect(yearOf('2025-12-31T00:00:00')).not.toBe(yearOf('2026-01-01T00:00:00'));
	});

	it('dateParts splits the ISO prefix into 0-based month parts', () => {
		expect(dateParts('2026-07-10T00:00:00')).toEqual({ y: 2026, m: 6, d: 10 });
	});
});

// ST-09: a backwards header range (From after To) must resolve to the same bounds as the
// correctly-ordered range, not an inverted axis.
describe('rangeExtentMs backwards range (ST-09)', () => {
	it('swaps a From > To range to the correct bounds', () => {
		ingest([point('2026-03-01T00:00:00')], { from: '2026-06-01', to: '2026-01-01' });
		const backwards = rangeExtentMs(currentSources());

		ingest([point('2026-03-01T00:00:00')], { from: '2026-01-01', to: '2026-06-01' });
		const forward = rangeExtentMs(currentSources());

		expect(backwards).not.toBeNull();
		expect(backwards!.lo).toBeLessThanOrEqual(backwards!.hi);
		expect(backwards).toEqual(forward); // same rendered extent either way
		expect(backwards!.lo).toBe(floorUTCDay(Date.parse('2026-01-01T00:00:00Z')));
		expect(backwards!.hi).toBe(floorUTCDay(Date.parse('2026-06-01T00:00:00Z')));
	});
});
