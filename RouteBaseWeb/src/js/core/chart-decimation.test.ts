// @vitest-environment node
import { describe, it, expect } from "vitest";
import { lttbDecimate, nearestIndexForX } from "./chart-decimation";
import { must } from "./invariant";

function ramp(n: number): { x: number; y: number }[] {
	return Array.from({ length: n }, (_, i) => ({ x: i, y: i }));
}


describe("lttbDecimate", () => {
	it("returns the input unchanged when target >= length", () => {
		const data = ramp(5);
		expect(lttbDecimate(data, 5)).toBe(data);
		expect(lttbDecimate(data, 10)).toBe(data);
	});

	it("returns the input unchanged when target < 3", () => {
		const data = ramp(50);
		expect(lttbDecimate(data, 2)).toBe(data);
	});

	it("hits the requested target length and keeps first + last point", () => {
		const data = ramp(1000);
		const out = lttbDecimate(data, 100);
		expect(out).toHaveLength(100);
		expect(out[0]).toEqual(data[0]);
		expect(out[out.length - 1]).toEqual(data[data.length - 1]);
	});

	it("retains an extreme spike point", () => {
		const data = ramp(200);
		data[100] = { x: 100, y: 100000 }; // a lone spike far off the ramp
		const out = lttbDecimate(data, 20);
		expect(out.some((p) => p.y === 100000)).toBe(true);
	});

	it("retains a spike in the LAST interior bucket (empty-next-bucket stand-in)", () => {
		// The last interior bucket's "next bucket" is just the final point; with the 0/0=NaN average,
		// every area comparison failed and the bucket degraded to its first point — a spike late in
		// the series (summit, max speed) silently vanished from the decimated chart.
		const data = ramp(10);
		data[8] = { x: 8, y: 100000 }; // inside the sole interior bucket for target=3, next to the end
		const out = lttbDecimate(data, 3);
		expect(out).toHaveLength(3);
		expect(must(out[1], 'interior point').y).toBe(100000);
	});
});

describe("nearestIndexForX", () => {
	const xs = [0, 10, 20, 30, 40];

	it("returns the exact index on a direct match", () => {
		expect(nearestIndexForX(xs, 20)).toBe(2);
		expect(nearestIndexForX(xs, 0)).toBe(0);
	});

	it("picks the nearer neighbour between points", () => {
		expect(nearestIndexForX(xs, 12)).toBe(1); // closer to 10
		expect(nearestIndexForX(xs, 18)).toBe(2); // closer to 20
	});

	it("clamps below-range to 0 and above-range to the last index", () => {
		expect(nearestIndexForX(xs, -100)).toBe(0);
		expect(nearestIndexForX(xs, 9999)).toBe(4);
	});
});
