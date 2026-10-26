import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

// CF-02: the chart-click hook must report the distance from the chart's BUILD-TIME data (the
// closure `dataSource`), not a later-swapped getGpsData() — otherwise `idx` (from the build-time
// `distances`) could index a different-length live array. gps-data is mocked so we can swap what
// getGpsData() returns after the chart is built.
let gpsReturn: { distance: number; speed: number }[] = [];
vi.mock("./gps-data", () => ({
	getGpsData: () => gpsReturn,
	// These fixtures carry no timestamps, so the factory takes its no-time-axis path: the
	// sample-indexed fallback kernel and no spread band. That is what these zoom tests want —
	// the band's own re-decimation is pinned in chart-decimation.test.ts.
	trackSeconds: () => null,
}));

import { createGpsLineChart, redecimateChartForRange } from "./chart-factory";
import { installFakeRaf, type FakeRaf } from './raf-test-harness';
import { must } from "./invariant";

// Minimal Chart.js stand-in: stores config, exposes an identity x-scale (getValueForPixel(px)=px)
// so a click at clientX=N resolves to xVal=N, plus the members finalizeChart/overlay read.
class FakeChart {
	canvas: HTMLCanvasElement;
	data: unknown;
	options: unknown;
	scales: Record<string, unknown>;
	constructor(ctx: { canvas: HTMLCanvasElement }, config: { data: unknown; options: unknown }) {
		this.canvas = ctx.canvas;
		this.data = config.data;
		this.options = config.options;
		this.scales = {
			x: { min: 0, max: 100, axis: "x", options: { ticks: {} }, getValueForPixel: (px: number) => px },
			y: { min: 0, max: 50, axis: "y", options: { ticks: {} } },
		};
	}
	update(): void {}
	destroy(): void {}
	resetZoom(): void {}
	isZoomedOrPanned(): boolean { return false; }
	getElementsAtEventForMode(): unknown[] { return []; }
}

let raf: FakeRaf;

beforeEach(() => {
	document.body.innerHTML = "";
	// getContext must be non-null (createGpsLineChart passes it to Chart) and carry a canvas backref +
	// the 2d methods the overlay/gradient paths call.
	HTMLCanvasElement.prototype.getContext = vi.fn(function (this: HTMLCanvasElement) {
		return {
			canvas: this,
			scale: () => {}, clearRect: () => {}, save: () => {}, restore: () => {},
			beginPath: () => {}, moveTo: () => {}, lineTo: () => {}, stroke: () => {},
			arc: () => {}, fill: () => {}, drawImage: () => {}, setTransform: () => {},
			rect: () => {}, clip: () => {},
			createLinearGradient: () => ({ addColorStop: () => {} }),
		};
	}) as never;
	raf = installFakeRaf();
	vi.stubGlobal("Chart", FakeChart);
});

afterEach(() => {
	raf.restore();
	vi.unstubAllGlobals();
	vi.restoreAllMocks();
});

describe("createGpsLineChart onChartClick (CF-02)", () => {
	it("reports the distance from build-time data, not a later-swapped getGpsData()", () => {
		const host = document.createElement("div");
		const canvas = document.createElement("canvas");
		canvas.id = "c";
		host.appendChild(canvas);
		document.body.appendChild(host);

		const build = [
			{ distance: 0, speed: 1 },
			{ distance: 5, speed: 2 },
			{ distance: 10, speed: 3 },
		];
		gpsReturn = build;

		const onChartClick = vi.fn();
		const chart = createGpsLineChart({
			canvasId: "c",
			label: "S",
			dataField: "speed",
			// These pin the zoom re-slice, not the estimator: the raw view keeps y === x so a
			// sliced point is checkable by eye.
			seriesMode: "recorded",
			borderColor: "#000",
			backgroundColor: "#000",
			yFormat: "decimal",
			tooltipPrefix: "S",
			tooltipUnit: "x",
			chartName: "s",
			fill: true, // skip enableLineSelect (needs Chart hit-testing)
			onChartClick,
		});
		expect(chart).not.toBeNull();

		// Click at pixel 5 → xVal 5 → nearest index 1 (distance 5).
		canvas.dispatchEvent(new MouseEvent("click", { clientX: 5 }));
		expect(onChartClick).toHaveBeenLastCalledWith(5, 1);

		// Swap what getGpsData() returns to a shorter, different array. The click hook must ignore
		// it (reads the build-time closure) and still report the original point.
		gpsReturn = [{ distance: 999, speed: 9 }];
		canvas.dispatchEvent(new MouseEvent("click", { clientX: 5 }));
		expect(onChartClick).toHaveBeenLastCalledWith(5, 1);
		expect(onChartClick).toHaveBeenCalledTimes(2); // not dropped by a stale-array undefined guard
	});
});

// The module's entire computational reason for existing (finding #5): a re-slice + re-decimate of the
// full-resolution series to whatever x-window is visible after a zoom. FakeChart's zoomScale has no real
// effect, so this never ran under any test — pin `redecimateChartForRange` (the same code path zoomScale's
// callback drives) directly instead of trying to make the fake simulate Chart.js zoom mechanics.
describe("redecimateChartForRange (CF zoom re-slice)", () => {
	interface Point { x: number; y: number }
	interface ChartLike { data: { datasets: { data: Point[] }[] }; scales: { x: { min: number; max: number } } }

	function buildChart(n: number, extra: Record<string, unknown> = {}): ChartLike {
		document.body.innerHTML = "";
		const canvas = document.createElement("canvas");
		canvas.id = "c";
		document.body.appendChild(canvas);

		gpsReturn = Array.from({ length: n }, (_, i) => ({ distance: i, speed: i }));

		const chart = createGpsLineChart({
			canvasId: "c",
			label: "S",
			dataField: "speed",
			seriesMode: "recorded" as const,
			borderColor: "#000",
			backgroundColor: "#000",
			yFormat: "decimal",
			tooltipPrefix: "S",
			tooltipUnit: "x",
			chartName: "s",
			fill: true, // skip enableLineSelect (needs Chart hit-testing)
			smoothingWindow: 1, // gaussianSmooth's own windowSize<3 guard: returns the raw data untouched
			...extra,
		});
		expect(chart).not.toBeNull();
		return chart as unknown as ChartLike;
	}

	it("re-slices to the requested x-window, padded by one point on each side, below the decimate target", () => {
		const chart = buildChart(2000); // > DECIMATE_TARGET (1000), so redecimation actually runs

		redecimateChartForRange(chart as unknown as Parameters<typeof redecimateChartForRange>[0], { min: 500, max: 600 });

		const data = must(chart.data.datasets[0], 'main dataset').data;
		expect(data).toHaveLength(103); // [499..601] inclusive — one padding point past each edge
		expect(data[0]).toEqual({ x: 499, y: 499 });
		expect(data[data.length - 1]).toEqual({ x: 601, y: 601 });
		expect(data.every((p) => p.x === p.y)).toBe(true); // untouched by smoothing (window=1)
	});

	it("falls back to LTTB when the visible window still exceeds the decimate target", () => {
		const chart = buildChart(2000);

		redecimateChartForRange(chart as unknown as Parameters<typeof redecimateChartForRange>[0], { min: 0, max: 1999 });

		const data = must(chart.data.datasets[0], 'main dataset').data;
		expect(data).toHaveLength(1000); // capped at DECIMATE_TARGET, not the full 2000
		expect(data[0]).toEqual({ x: 0, y: 0 });        // LTTB always keeps the first point
		expect(data[data.length - 1]).toEqual({ x: 1999, y: 1999 }); // and the last
	});

	it("omitting `range` uses the chart's live scale window instead", () => {
		const chart = buildChart(2000);
		chart.scales.x.min = 10;
		chart.scales.x.max = 20;

		redecimateChartForRange(chart as unknown as Parameters<typeof redecimateChartForRange>[0]);

		const data = must(chart.data.datasets[0], 'main dataset').data;
		expect(must(data[0], 'first point').x).toBe(9);
		expect(must(data.at(-1), 'last point').x).toBe(21);
	});

	it("re-decimates the background series over the SAME x-window, independently of the main series", () => {
		const chart = buildChart(2000, { backgroundField: "altitude" });
		// altitude wasn't seeded on gpsReturn points above, so every raw value is 0 (the `|| 0` fallback) —
		// only the x-window matters for this assertion.

		redecimateChartForRange(chart as unknown as Parameters<typeof redecimateChartForRange>[0], { min: 500, max: 600 });

		const bg = must(chart.data.datasets[1], 'background dataset').data;
		expect(bg).toHaveLength(103);
		expect(must(bg[0], 'first point').x).toBe(499);
		expect(must(bg.at(-1), 'last point').x).toBe(601);
	});

	it("a dataset at or below the decimate target is never redecimated (no-op)", () => {
		const chart = buildChart(50);
		const before = must(chart.data.datasets[0], 'main dataset').data;

		redecimateChartForRange(chart as unknown as Parameters<typeof redecimateChartForRange>[0], { min: 10, max: 20 });

		expect(must(chart.data.datasets[0], 'main dataset').data).toBe(before); // same array reference — the early return fired
	});

	it("a null chart is a no-op, not a throw", () => {
		expect(() => redecimateChartForRange(null)).not.toThrow();
	});
});
