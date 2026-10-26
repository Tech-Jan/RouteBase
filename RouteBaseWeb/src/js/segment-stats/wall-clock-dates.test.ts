// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { formatDateDMY } from '../core/grid/grid-format';
import { formatElapsedHms } from './seg-stats-shared';
import { mapDataApi } from './segment-stats-map-data';
import type { SegmentEffort } from './segment-stats-effort-types';

// B3: the segment producers now send day-at-midnight wall-clock ISO ("2026-07-01T00:00:00")
// instead of bare "2026-07-01". ES parses a date-only string as UTC midnight, so every consumer
// that did `new Date(wire)` + local render (segment-stats-efforts-table, segment-stats-replay-ui,
// activity-segments) showed 30.06 for a UTC-minus viewer. Those three become correct with no code
// change — the parse-rule pin below is their regression guard.

describe('wall-clock date wire strings (B3)', () => {
	it('wall-clock ISO parses as LOCAL midnight — the day survives a UTC-minus timezone', () => {
		process.env.TZ = 'America/Denver'; // UTC-7/-6
		expect(new Date('2026-07-01T00:00:00').getDate()).toBe(1);
		// The old bare-date wire is what regressed: UTC midnight read back in local components.
		expect(new Date('2026-07-01').getDate()).toBe(30);
	});

	it('formatDateDMY renders the calendar day of a wall-clock ISO under UTC-minus', () => {
		process.env.TZ = 'America/Denver';
		expect(formatDateDMY('2026-07-01T00:00:00')).toBe('01.07.2026');
		expect(formatDateDMY(null)).toBe('');
		expect(formatDateDMY('not-a-date')).toBe('');
	});

	it('effort popup formats the date via formatDateDMY and elapsed via formatElapsedHms', () => {
		process.env.TZ = 'America/Denver';
		const effort = {
			name: 'S', rank: 1, date: '2026-07-01T00:00:00', time: 90_000,
		} as unknown as SegmentEffort;

		const html = mapDataApi.buildEffortPopupHtml(effort);

		expect(html).toContain('Date: 01.07.2026'); // not the raw ISO stamp
		expect(html).not.toContain('2026-07-01T00:00:00');
		// 90 000 s = 25:00:00 — toISOString().substr(11,8) wrapped this to 01:00:00 (SC2-10).
		expect(html).toContain('Time: 25:00:00');
	});

	it('formatElapsedHms does not wrap past 24 h', () => {
		expect(formatElapsedHms(90_000)).toBe('25:00:00');
		expect(formatElapsedHms(59)).toBe('00:00:59');
	});
});
