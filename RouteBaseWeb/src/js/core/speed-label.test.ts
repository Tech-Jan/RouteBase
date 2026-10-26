// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { createSpeedLabel } from './speed-label';

describe('createSpeedLabel', () => {
	it('shows the first value straight away — a fresh label has nothing to hold', () => {
		expect(createSpeedLabel().read(24.8)).toBe(25);
	});

	it('rounds to whole km/h — a decimal that nobody can read mid-replay is not a readout', () => {
		expect(createSpeedLabel().read(24.4)).toBe(24);
		expect(createSpeedLabel().read(24.5)).toBe(25);
	});

	it('does not chatter when the true speed sits on a rounding boundary', () => {
		// The defect this module exists for: plain Math.round would flip 24/25/24/25 forever here.
		const label = createSpeedLabel();
		const first = label.read(24.5);
		for (const v of [24.4, 24.6, 24.5, 24.55, 24.45, 24.6, 24.4]) {
			expect(label.read(v)).toBe(first);
		}
	});

	it('follows the value once it has genuinely left the shown number', () => {
		const label = createSpeedLabel();
		expect(label.read(24)).toBe(24);
		expect(label.read(24.7)).toBe(24);   // inside the 0.8 deadband
		expect(label.read(24.85)).toBe(25);  // past it
	});

	it('lands a big change immediately — the deadband is not a rate limit', () => {
		// Pins that no separate "jump override" constant is needed: a real event clears the band by
		// itself and goes straight to the rounded value, however many whole numbers away.
		const label = createSpeedLabel();
		expect(label.read(24)).toBe(24);
		expect(label.read(38.2)).toBe(38);
	});

	it('settles rather than oscillating after it moves', () => {
		// Whatever it just showed is within 0.5 of the value, and 0.5 is inside the band, so the very
		// next identical read cannot move it back.
		const label = createSpeedLabel();
		label.read(24);
		expect(label.read(24.85)).toBe(25);
		expect(label.read(24.85)).toBe(25);
	});

	it('drops to a standstill at once when the rider stops', () => {
		const label = createSpeedLabel();
		label.read(31);
		expect(label.read(0)).toBe(0);
	});

	it('treats a negative or non-finite value as a standstill rather than showing NaN', () => {
		expect(createSpeedLabel().read(Number.NaN)).toBe(0);
		expect(createSpeedLabel().read(-5)).toBe(0);
		expect(createSpeedLabel().read(Number.POSITIVE_INFINITY)).toBe(0);
	});

	it('reset makes the next value show at once — the label changed subject', () => {
		const label = createSpeedLabel();
		label.read(40);
		label.reset();
		expect(label.read(10)).toBe(10);
	});

	it('takes a wider or narrower deadband', () => {
		const wide = createSpeedLabel({ hysteresisKmh: 2 });
		expect(wide.read(24)).toBe(24);
		expect(wide.read(26)).toBe(24);
		expect(wide.read(26.6)).toBe(27);
	});
});
