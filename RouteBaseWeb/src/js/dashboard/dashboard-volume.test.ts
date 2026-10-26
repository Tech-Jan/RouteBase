// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { parseIsoDateLocal } from './dashboard-volume';

// DT-02: parse the ISO day as local midnight so the calendar day survives in every timezone
// (new Date('YYYY-MM-DD') would parse UTC and shift a day for UTC-minus users).
describe('parseIsoDateLocal (DT-02)', () => {
	it('keeps the calendar day in every timezone', () => {
		process.env.TZ = 'America/New_York'; // UTC-5/-4
		expect(parseIsoDateLocal('2026-07-06').getDate()).toBe(6);
		expect(parseIsoDateLocal('2026-07-06').getMonth()).toBe(6);
	});
});
