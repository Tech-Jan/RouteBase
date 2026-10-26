// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { axisValueAtX, xAtAxisValue } from './profile-axis';

// Moved verbatim from race-clock.test.ts when the axis maths left race-sim for core/ — same cases,
// same numbers (a 40 km course, an 800 px plot inset 100 px).
describe('profile axis (checkpoint placement, hover readout)', () => {
	it('maps an x inside the plot to an axis value', () => {
		expect(axisValueAtX(500, 100, 800, 40000)).toBeCloseTo(20000, 0);   // dead centre
	});

	it('clamps outside the plot — dragging off the left edge means the START, not the end', () => {
		expect(axisValueAtX(50, 100, 800, 40000)).toBe(0);
		expect(axisValueAtX(5000, 100, 800, 40000)).toBe(40000);
	});

	it('round-trips against xAtAxisValue', () => {
		const x = xAtAxisValue(21000, 100, 800, 38910);
		expect(axisValueAtX(x, 100, 800, 38910)).toBeCloseTo(21000, 3);
	});

	it('a zero-width plot and a zero-extent axis degrade rather than divide by zero', () => {
		expect(axisValueAtX(500, 100, 0, 40000)).toBe(0);
		expect(xAtAxisValue(500, 100, 800, 0)).toBe(100);
	});
});
