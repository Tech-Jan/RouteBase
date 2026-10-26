// @vitest-environment node
import type { GpsPoint } from './core-types';
import { describe, it, expect } from 'vitest';
import { buildSpeedScale, buildGpsSpeedScale } from './speed-scale';

describe('buildSpeedScale', () => {
	it('empty and all-invalid samples yield the empty scale', () => {
		const empty = buildSpeedScale([]);
		expect(empty.isEmpty).toBe(true);
		expect(empty.fill(42)).toBe(0);

		const invalid = buildSpeedScale([
			{ speed: NaN, weight: 1 },
			{ speed: -5, weight: 1 },
			{ speed: 10, weight: 0 },
			{ speed: 10, weight: -3 },
			{ speed: Infinity, weight: 1 },
			{ speed: 10, weight: NaN },
		]);
		expect(invalid.isEmpty).toBe(true);
		expect(invalid.fill(10)).toBe(0);
	});

	it('filters non-finite/negative speed and non-positive weight samples', () => {
		// Only the two valid samples (speed 10 w1, speed 20 w1) survive.
		const scale = buildSpeedScale([
			{ speed: 10, weight: 1 },
			{ speed: -1, weight: 5 },
			{ speed: 20, weight: 1 },
			{ speed: 30, weight: 0 },
		]);
		expect(scale.isEmpty).toBe(false);
		// Below slowest positive: cumWeight[0]/total = 1/2 = 50%.
		expect(scale.fill(5)).toBe(50);
		expect(scale.fill(20)).toBe(100); // at max sample
	});

	it('fill boundaries: <=0 -> 0, at/above max -> 100, below slowest positive -> bottom bucket share', () => {
		const scale = buildSpeedScale([
			{ speed: 10, weight: 1 },
			{ speed: 20, weight: 1 },
			{ speed: 30, weight: 2 },
		]);
		expect(scale.fill(5)).toBe(25); // cumWeight[0]/total = 1/4
		expect(scale.fill(0)).toBe(0);
		expect(scale.fill(-3)).toBe(0);
		expect(scale.fill(30)).toBe(100);
		expect(scale.fill(31)).toBe(100);
		expect(scale.fill(NaN)).toBe(0);
	});

	it('weighted CDF interior values (rightmost <= semantics)', () => {
		const scale = buildSpeedScale([
			{ speed: 10, weight: 1 },
			{ speed: 20, weight: 1 },
			{ speed: 30, weight: 2 },
		]);
		expect(scale.fill(20)).toBe(50); // cumWeight through 20 = 2/4
		expect(scale.fill(25)).toBe(50); // rightmost speed <= 25 is 20
	});

	it('duplicate speeds accumulate weight (interior ties resolve to rightmost)', () => {
		// sorted speeds [10,20,20,30] weights [1,3,2,4] -> cumWeight [1,4,6,10], total 10.
		const scale = buildSpeedScale([
			{ speed: 10, weight: 1 },
			{ speed: 20, weight: 3 },
			{ speed: 20, weight: 2 },
			{ speed: 30, weight: 4 },
		]);
		expect(scale.fill(20)).toBe(60); // rightmost 20-bucket => cumWeight[2]=6 of 10
		expect(scale.fill(25)).toBe(60); // rightmost <= 25 is still the 20-bucket
		expect(scale.fill(10)).toBe(10); // speeds[0] boundary uses cumWeight[0] only
		expect(scale.fill(30)).toBe(100);
	});
});

describe('buildGpsSpeedScale', () => {
	it('weights by time delta (ms), falls back to weight 1 on unparseable timestamps', () => {
		// Inter-point samples (i over points[0..len-2]):
		//   i=0: speed 10, dt = 10s = 10000ms  -> weight 10000
		//   i=1: speed 20, next time unparseable -> dt NaN -> weight 1 fallback
		const points = [
			{ lat: 0, lng: 0, altitude: 0, speed: 10, distance: 0, time: '2020-01-01T00:00:00Z' },
			{ lat: 0, lng: 0, altitude: 0, speed: 20, distance: 0, time: '2020-01-01T00:00:10Z' },
			{ lat: 0, lng: 0, altitude: 0, speed: 20, distance: 0, time: 'not-a-date' },
		] as GpsPoint[];
		const scale = buildGpsSpeedScale(points);
		expect(scale.isEmpty).toBe(false);
		// total = 10001; the slow 10 km/h dwell dominates via its time weight.
		expect(scale.fill(10)).toBeCloseTo((10000 / 10001) * 100, 5);
		expect(scale.fill(15)).toBeCloseTo((10000 / 10001) * 100, 5); // rightmost <= 15 is the 10-bucket
		expect(scale.fill(20)).toBe(100);
	});

	it('empty / single-point input yields the empty scale', () => {
		expect(buildGpsSpeedScale([]).isEmpty).toBe(true);
		expect(
			buildGpsSpeedScale([
				{ lat: 0, lng: 0, altitude: 0, speed: 5, distance: 0 },
			] as GpsPoint[]).isEmpty,
		).toBe(true);
	});
});
