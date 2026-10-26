// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { formatElapsed, formatGap, formatTime, formatBytes } from './format';

describe('formatTime', () => {
	it('formats MM:SS / HH:MM:SS, guards NaN/null, uses absolute value', () => {
		expect(formatTime(NaN)).toBe('--:--');
		expect(formatTime(null as unknown as number)).toBe('--:--');
		expect(formatTime(65)).toBe('1:05');
		expect(formatTime(3661)).toBe('1:01:01');
		expect(formatTime(-65)).toBe('1:05');
	});
});

// Moved verbatim from race-clock.test.ts when the formatters left race-sim for core/.
describe('formatElapsed / formatGap', () => {
	it.each([
		[4619, '1:16:59'],
		[3552, '59:12'],
		[0, '0:00'],
		[-1, '—'],
	])('formatElapsed(%i) = %s', (seconds, expected) => {
		expect(formatElapsed(seconds)).toBe(expected);
	});

	it.each([
		[0, '—'],
		[340, '340 m'],
		[1200, '1.2 km'],
	])('formatGap(%i) = %s', (metres, expected) => {
		expect(formatGap(metres)).toBe(expected);
	});

	it('a negative elapsed reads as "no time", where formatTime reads the magnitude', () => {
		expect(formatElapsed(-65)).toBe('—');
		expect(formatTime(-65)).toBe('1:05');
	});
});

describe('formatBytes', () => {
	it('scales B → KB → MB → GB, one decimal below 10 units', () => {
		expect(formatBytes(0)).toBe('0 B');
		expect(formatBytes(999)).toBe('999 B');
		expect(formatBytes(1500)).toBe('1.5 KB');
		expect(formatBytes(45_000)).toBe('45 KB');
		expect(formatBytes(2_400_000)).toBe('2.4 MB');
		expect(formatBytes(88_100_000)).toBe('88 MB');
		expect(formatBytes(2_500_000_000)).toBe('2.5 GB');
	});

	it('guards NaN/null/negative', () => {
		expect(formatBytes(NaN)).toBe('--');
		expect(formatBytes(null as unknown as number)).toBe('--');
		expect(formatBytes(-1)).toBe('--');
	});
});
