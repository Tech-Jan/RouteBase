// @vitest-environment node
import { describe, it, expect } from "vitest";
import { robustYBounds, robustXMax, visibleYSeries } from "./chart-robust-scale";

// Pure percentile scale math, moved to core/ (item 7) and given the render-adjacent coverage it
// never had (item 8). The claim these pin: one outlier can't stretch the axis and squash every
// real line — the bounds fit the P1..P99 bulk, snapped OUTWARD to a nice grid.

describe("robustYBounds", () => {
	const series = (ys: number[]) => [ys.map((y) => ({ y }))];

	it("returns null when there is nothing to measure", () => {
		expect(robustYBounds([], { beginAtZero: false })).toBeNull();
		expect(robustYBounds(series([]), { beginAtZero: false })).toBeNull();
		expect(robustYBounds([[{ y: null }, { y: NaN }]], { beginAtZero: false })).toBeNull();
	});

	it("fits the P1..P99 bulk and snaps outward to a nice grid", () => {
		// 1..100: p1≈2, p99≈99, padded 5% then snapped to the 10s grid → [-10, 110].
		const b = robustYBounds(series(Array.from({ length: 100 }, (_, i) => i + 1)), { beginAtZero: false });
		expect(b).toEqual({ min: -10, max: 110 });
	});

	it("beginAtZero pins the floor to 0", () => {
		const b = robustYBounds(series(Array.from({ length: 100 }, (_, i) => i + 1)), { beginAtZero: true });
		expect(b).toEqual({ min: 0, max: 110 });
	});

	it("a lone spike clips off the top instead of stretching the axis", () => {
		// 99 values in 0..10 plus one 5000 spike: p99 stays in the bulk, so max is nowhere near 5000.
		const ys = Array.from({ length: 99 }, (_, i) => (i % 11)).concat([5000]);
		const b = robustYBounds(series(ys), { beginAtZero: true })!;
		expect(b.max).toBeLessThan(100);
	});
});

describe("robustXMax", () => {
	it("undefined when there is no positive extent", () => {
		expect(robustXMax([])).toBeUndefined();
		expect(robustXMax([0, -1, NaN])).toBeUndefined();
	});

	it("returns the true max when it is not an outlier", () => {
		expect(robustXMax([10, 11, 12, 13])).toBe(13);
	});

	it("opens at ~P90 when the longest effort is a wild outlier (zoom-out still reaches it)", () => {
		const xs = [10, 10, 10, 10, 10, 10, 10, 10, 10, 1000];
		// p90 of the bulk is 10, absMax 1000 > 10×1.3 → view opens at 10×1.05.
		expect(robustXMax(xs)).toBeCloseTo(10.5, 5);
	});

	it("too few points to judge an outlier → the absolute max", () => {
		expect(robustXMax([5, 500])).toBe(500);
	});
});

describe("visibleYSeries", () => {
	it("selects only the requested (non-empty) dataset data arrays", () => {
		const datasets = [
			{ data: [{ y: 1 }] },
			{ data: [] },
			{ data: [{ y: 2 }, { y: 3 }] },
		];
		const out = visibleYSeries(datasets, [0, 1, 2, 5]); // 1 is empty, 5 is missing
		expect(out).toHaveLength(2);
		expect(out[0]).toEqual([{ y: 1 }]);
		expect(out[1]).toEqual([{ y: 2 }, { y: 3 }]);
	});
});
