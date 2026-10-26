// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { N0, N1, formatDateDMY, formatDuration, num } from './grid-format';

describe('grid-format', () => {
	it('num formats, scales, and blanks null', () => {
		expect(num(null, N0)).toBe('');
		expect(num(undefined, N1)).toBe('');
		expect(num(12345, N0)).toBe('12,345');
		expect(num(12345, N1, 0.001)).toBe('12.3'); // m → km
	});

	it('formatDateDMY renders the local calendar day and blanks invalid input', () => {
		expect(formatDateDMY('2026-07-08T14:30:00')).toBe('08.07.2026'); // naive local — tz-stable
		expect(formatDateDMY(null)).toBe('');
		expect(formatDateDMY('not a date')).toBe('');
	});

	it('formatDuration renders HH:MM:SS with clamping', () => {
		expect(formatDuration(3661)).toBe('01:01:01');
		expect(formatDuration(0)).toBe('00:00:00');
		expect(formatDuration(null)).toBe('00:00:00');
		expect(formatDuration(-5)).toBe('00:00:00');
		expect(formatDuration(86399)).toBe('23:59:59');
	});
});
