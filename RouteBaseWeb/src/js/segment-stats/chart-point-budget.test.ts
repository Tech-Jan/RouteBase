// @vitest-environment node
import { describe, it, expect } from 'vitest';
import {
	computeChartPointBudget,
	resolveAutoDetail,
	shouldClientDecimate,
	MIN_POINTS_PER_EFFORT,
	MAX_POINTS_PER_EFFORT,
} from './chart-point-budget';

describe('chart-point-budget — PS-02 adaptive budget', () => {
	it('full detail → 0 (server full-res sentinel)', () => {
		expect(computeChartPointBudget(744, 'full')).toBe(0);
		expect(computeChartPointBudget(1, 'full')).toBe(0);
	});

	it('auto resolves to high under 50 efforts, normal at/above (never full)', () => {
		expect(resolveAutoDetail(1)).toBe('high');
		expect(resolveAutoDetail(49)).toBe('high');
		expect(resolveAutoDetail(50)).toBe('normal');
		expect(resolveAutoDetail(744)).toBe('normal');
	});

	it('auto budget matches its resolved concrete detail (never 0/full)', () => {
		expect(computeChartPointBudget(30, 'auto')).toBe(computeChartPointBudget(30, 'high'));
		expect(computeChartPointBudget(744, 'auto')).toBe(computeChartPointBudget(744, 'normal'));
		expect(computeChartPointBudget(1, 'auto')).toBeGreaterThan(0);
	});

	it('normal detail at 744 efforts → 202 (live-scale seg 42)', () => {
		expect(computeChartPointBudget(744, 'normal')).toBe(202);
	});

	it('single effort clamps to the MAX ceiling', () => {
		expect(computeChartPointBudget(1, 'normal')).toBe(MAX_POINTS_PER_EFFORT);
	});

	it('very high effort count clamps to the MIN floor', () => {
		expect(computeChartPointBudget(5000, 'normal')).toBe(MIN_POINTS_PER_EFFORT);
	});

	it('low/high multipliers scale the budget (0.5× / 2×)', () => {
		expect(computeChartPointBudget(744, 'low')).toBe(101); // 75000/744 → 100.8 → 101
		expect(computeChartPointBudget(744, 'high')).toBe(403); // 300000/744 → 403.2 → 403
	});

	it('budget depends only on effort count, never on per-effort durations', () => {
		// The function has no way to see durations — a pure count-in / count-out contract.
		// Two segments with the same effort count get the same budget regardless of length.
		expect(computeChartPointBudget(285, 'normal')).toBe(computeChartPointBudget(285, 'normal'));
		expect(computeChartPointBudget(285, 'normal')).toBe(526); // 150000/285 → 526.3 → 526
	});
});

describe('shouldClientDecimate — PS-11 fixed-resolution zoom truth table', () => {
	it('LTTB on only for full-res (budget 0) outside aggregate mode', () => {
		expect(shouldClientDecimate(0, false)).toBe(true);   // knob Full, individual mode → keep LTTB
		expect(shouldClientDecimate(0, true)).toBe(false);   // aggregate always off
		expect(shouldClientDecimate(202, false)).toBe(false); // server-downsampled → fixed-res, off
		expect(shouldClientDecimate(4000, false)).toBe(false);
		expect(shouldClientDecimate(202, true)).toBe(false);
	});
});
