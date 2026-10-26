// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { RaceField, createThinSource, type CourseGrid } from './race-field';
import { computeFieldSpeedProfile } from './race-field-speed';

const STEP = 10;

/** A straight, flat course of `n` nodes 10 m apart — 10·(n-1) metres long. */
function course(n: number): CourseGrid {
	const distance = new Float64Array(n);
	const lat = new Float64Array(n);
	const lng = new Float64Array(n);
	const elevation = new Float64Array(n);
	const gradePercent = new Float64Array(n);
	for (let i = 0; i < n; i++) {
		distance[i] = i * STEP;
		lat[i] = 47.9;
		lng[i] = 8.1 + i * 0.0001;
		elevation[i] = 500;
	}
	return { distance, lat, lng, elevation, gradePercent, stepMeters: STEP };
}

/** Constant-pace arrivals: the whole course covered in `total` seconds. */
function evenArrivals(n: number, total: number): Float32Array {
	const a = new Float32Array(n);
	for (let i = 0; i < n; i++) a[i] = (total * i) / (n - 1);
	return a;
}

describe('computeFieldSpeedProfile', () => {
	it('reports a constant-pace rider at his constant speed across the whole course', () => {
		// 1000 m in 100 s = 10 m/s = 36 km/h, everywhere.
		const g = course(101);
		const field = new RaceField([createThinSource(g, evenArrivals(101, 100))]);

		const profile = computeFieldSpeedProfile(field, 1000, { bins: 100, timeSteps: 400 });

		expect(profile.bins.length).toBeGreaterThan(90);
		for (const bin of profile.bins) {
			expect(bin.median).toBeCloseTo(36, 0);   // within 0.5 km/h
			expect(bin.p10).toBeCloseTo(36, 0);
			expect(bin.p90).toBeCloseTo(36, 0);
		}
		expect(profile.maxSpeedKmh).toBeCloseTo(36, 0);
	});

	it('takes the median across riders, unweighted by how long each dwelt in a bin', () => {
		// One rider at 36 km/h (100 s), one at 18 km/h (200 s). The slow rider spends twice as long in every
		// bin — a time-weighted median would collapse toward 18; the per-rider-per-bin median is the midpoint.
		const g = course(101);
		const field = new RaceField([
			createThinSource(g, evenArrivals(101, 100)),   // 36 km/h
			createThinSource(g, evenArrivals(101, 200)),   // 18 km/h
		]);

		const profile = computeFieldSpeedProfile(field, 1000, { bins: 100, timeSteps: 800 });

		const mid = profile.bins.reduce((best, b) =>
			Math.abs(b.distanceMeters - 500) < Math.abs(best.distanceMeters - 500) ? b : best);
		expect(mid.median).toBeGreaterThan(25);
		expect(mid.median).toBeLessThan(29);           // ≈ (18 + 36) / 2 = 27
		expect(mid.p10).toBeLessThanOrEqual(mid.median);
		expect(mid.median).toBeLessThanOrEqual(mid.p90);
		expect(profile.maxSpeedKmh).toBeGreaterThan(33);   // p90 ≈ 34.2
	});

	it('is empty for an empty field', () => {
		const profile = computeFieldSpeedProfile(new RaceField([]), 1000);
		expect(profile.bins).toEqual([]);
		expect(profile.maxSpeedKmh).toBe(0);
	});
});
