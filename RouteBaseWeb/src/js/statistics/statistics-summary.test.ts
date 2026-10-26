// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { paceStr } from './statistics-summary';

// ST-06: paceStr — the one branch (zero/negative speed) with no positive-path coverage risk.
describe('paceStr', () => {
	it('formats a zero or negative speed as --', () => {
		expect(paceStr(0)).toBe('--');
		expect(paceStr(-5)).toBe('--');
	});

	it('formats a positive speed as min:sec per km', () => {
		expect(paceStr(12)).toBe('5:00'); // 12 km/h → 5:00 min/km
		expect(paceStr(10)).toBe('6:00'); // 10 km/h → 6:00 min/km
	});
});
