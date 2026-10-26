// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { LatestRun } from './latest-run';

describe('LatestRun', () => {
	it('begin aborts the previous signal and returns a fresh unaborted one', () => {
		const run = new LatestRun();
		const s1 = run.begin();
		expect(s1.aborted).toBe(false);

		const s2 = run.begin();
		expect(s1.aborted).toBe(true); // previous run superseded
		expect(s2.aborted).toBe(false);
		expect(s2).not.toBe(s1);
	});

	it('cancel aborts the current run and is safe before begin / twice', () => {
		const run = new LatestRun();
		expect(() => run.cancel()).not.toThrow(); // before any begin

		const s = run.begin();
		run.cancel();
		expect(s.aborted).toBe(true);
		expect(() => run.cancel()).not.toThrow(); // second cancel is a no-op
	});
});
