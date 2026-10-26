// @vitest-environment node
import { describe, it, expect } from "vitest";
import { robustValueRange, accumulateDensity, toneMapDensity } from "./segment-stats-density";
import { must } from '../core/invariant';

// segment-stats-density's accumulate/tonemap/range are pure "on purpose" (see the header) but were
// never tested. These pin the count grid, the asinh tone map, and the outlier-tolerant range.

describe("robustValueRange", () => {
	it("null when there is no finite coverage", () => {
		expect(robustValueRange([])).toBeNull();
		expect(robustValueRange([Float64Array.from([NaN, NaN])])).toBeNull();
	});

	it("returns padded P1..P99 (unsnapped), so a spike can't stretch the grid", () => {
		const rows = [Float64Array.from(Array.from({ length: 100 }, (_, i) => i + 1))];
		const r = robustValueRange(rows)!;
		expect(r.min).toBeCloseTo(-2.85, 2);
		expect(r.max).toBeCloseTo(103.85, 2);
	});
});

describe("accumulateDensity", () => {
	it("deposits ~unit mass per column into the right cell (index = cellY*w + x)", () => {
		// Two columns, both at y=0 (bottom row). Grid 2×2 over [0,2].
		const counts = accumulateDensity([Float64Array.from([0, 0])], 2, 2, 0, 2);
		expect(Array.from(counts)).toEqual([1, 1, 0, 0]); // bottom row filled, top row empty
	});

	it("splits one column's unit mass across the cells its segment crosses", () => {
		// Column 0 rises 0→1.5 over a 2-row grid [0,2] (scale 1): value 0→cell 0, 1.5→cell 1, so
		// the segment spans 2 cells and its unit mass splits 0.5 / 0.5 between them.
		const counts = accumulateDensity([Float64Array.from([0, 1.5])], 2, 2, 0, 2);
		expect(counts[0]).toBeCloseTo(0.5, 5); // bottom, col 0
		expect(counts[2]).toBeCloseTo(0.5, 5); // top, col 0
	});

	it("returns all-zero when the y-span is degenerate", () => {
		const counts = accumulateDensity([Float64Array.from([1, 1])], 2, 2, 5, 5);
		expect(Array.from(counts)).toEqual([0, 0, 0, 0]);
	});
});

describe("toneMapDensity", () => {
	it("maps 0→0 and max→1 via asinh, monotonically (sparse regions stay visible)", () => {
		const out = toneMapDensity(Float32Array.from([0, 1, 2]));
		expect(out[0]).toBe(0);
		expect(out[2]).toBeCloseTo(1, 5);
		expect(out[1]).toBeGreaterThan(0);
		expect(out[1]).toBeLessThan(must(out[2], 'index 2'));
	});

	it("honours an external denominator so multi-region grids share one scale", () => {
		const out = toneMapDensity(Float32Array.from([0, 2]), 4); // max is 4, not the array's own 2
		expect(out[1]).toBeLessThan(1); // 2 is no longer the top of the scale
	});
});
