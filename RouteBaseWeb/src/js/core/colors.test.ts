// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { getColorForValue } from './colors';

describe('getColorForValue', () => {
	it('clamps normalized value, zero-range -> start color, unknown scheme -> #dc3545', () => {
		expect(getColorForValue(5, 10, 10, 'speed')).toBe('rgb(0, 255, 0)'); // zero range
		expect(getColorForValue(5, 0, 10, 'bogus')).toBe('#dc3545'); // unknown
		expect(getColorForValue(999, 0, 10, 'speed')).toBe('rgb(255, 0, 0)'); // clamp above max
	});

	it('per-scheme endpoint colors', () => {
		expect(getColorForValue(0, 0, 10, 'speed')).toBe('rgb(0, 255, 0)'); // green start
		expect(getColorForValue(10, 0, 10, 'speed')).toBe('rgb(255, 0, 0)'); // red end
		expect(getColorForValue(0, 0, 10, 'heartrate')).toBe('rgb(0, 0, 255)'); // blue start
		expect(getColorForValue(0, 0, 10, 'elevation')).toBe('rgb(0, 100, 0)'); // dark green start
	});
});
