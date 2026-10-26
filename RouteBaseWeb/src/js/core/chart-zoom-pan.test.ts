import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { xZoomConfig, clampRangeToBounds, finalizeChart, makeAxisHeightLock } from "./chart-zoom-pan";
import { must } from "./invariant";
import { isChartPanning } from "./chart-pan-state";

type ChartInstance = InstanceType<typeof Chart>;

// Fake Chart.js chart carrying just what finalizeChart/setupPanning read: full-res data (for the
// bounds cache), a live x-scale, a chartArea for pixel math, and zoomScale/update spies. No
// previewGroup → pan applies via chart.zoomScale directly.
function makeFakeChart(canvas: HTMLCanvasElement, data: { x: number; y: number }[]) {
	const xMin = must(data[0], 'non-empty fixture').x, xMax = must(data.at(-1), 'non-empty fixture').x;
	const chart = {
		canvas,
		chartArea: { left: 0, top: 0, right: 100, bottom: 100 },
		width: 500,
		data: { datasets: [{ data }] },
		scales: {
			x: { min: xMin, max: xMax, axis: "x", options: { ticks: {} }, getValueForPixel: (px: number) => px },
			y: { min: 0, max: 50, axis: "y", options: { ticks: {} } },
		},
		options: { scales: { x: { ticks: {} } } },
		zoomScale: vi.fn(),
		update: vi.fn(),
	};
	return chart as unknown as ChartInstance & { zoomScale: ReturnType<typeof vi.fn> };
}

/** A TouchEvent jsdom will dispatch — `touches` is what both the pinch and the refusal handlers read. */
function touch(type: string, points: { clientX: number; clientY?: number }[]): Event {
	const ev = new Event(type, { bubbles: true, cancelable: true });
	(ev as unknown as { touches: unknown[] }).touches = points.map((p) => ({ clientX: p.clientX, clientY: p.clientY ?? 0 }));
	return ev;
}

describe("makeAxisHeightLock (CG-06)", () => {
	it("pins the height while the layout width is unchanged (anti-flicker)", () => {
		const lock = makeAxisHeightLock();
		const first = { height: 30, chart: { width: 500 } };
		lock(first); // captures 30 @ width 500
		const zoomFit = { height: 99, chart: { width: 500 } };
		lock(zoomFit);
		expect(zoomFit.height).toBe(30); // forced back — a tick label reflow can't resize the plot
	});

	it("re-measures when the layout width changes (real resize)", () => {
		const lock = makeAxisHeightLock();
		lock({ height: 30, chart: { width: 500 } });
		const resized = { height: 77, chart: { width: 800 } };
		lock(resized);
		expect(resized.height).toBe(77); // adopted, not clamped to the stale 30
	});
});

describe("finalizeChart pan-bounds cache (CG-01)", () => {
	beforeEach(() => {
		// Return 0 (not a truthy id): the pan code sets `_panRafId = requestAnimationFrame(cb)` AFTER
		// cb has synchronously reset it to 0, so a truthy return would leave a phantom pending frame
		// that mouseup re-applies (a real async rAF never has this ordering problem).
		vi.stubGlobal("requestAnimationFrame", (cb: FrameRequestCallback): number => { cb(0); return 0; });
		vi.stubGlobal("cancelAnimationFrame", () => {});
	});
	afterEach(() => { vi.unstubAllGlobals(); });

	it("captures full-data bounds at finalize, not first interaction (pan survives a later redecimation)", () => {
		const canvas = document.createElement("canvas");
		const chart = makeFakeChart(canvas, [{ x: 0, y: 0 }, { x: 50, y: 5 }, { x: 100, y: 0 }]);
		finalizeChart(canvas, chart, "t");

		// Simulate a wheel apply BEFORE the first mouse interaction: dataset replaced with the
		// zoomed slice + scale narrowed. The eager cache must still hold the full [0,100] extent.
		(chart.data.datasets[0] as { data: unknown }).data = [{ x: 40, y: 4 }, { x: 60, y: 6 }];
		const xs = must(chart.scales.x, 'fake has an x scale');
		xs.min = 40; xs.max = 60;

		canvas.dispatchEvent(new MouseEvent("mousedown", { button: 0, clientX: 0 }));
		canvas.dispatchEvent(new MouseEvent("mousemove", { clientX: 30 }));
		canvas.dispatchEvent(new MouseEvent("mouseup"));

		expect(chart.zoomScale).toHaveBeenCalledTimes(1);
		const range = must(chart.zoomScale.mock.calls[0], 'one zoomScale call')[1] as { min: number; max: number };
		expect(range.min).toBeGreaterThanOrEqual(0);
		expect(range.max).toBeLessThanOrEqual(100);
		expect(range.min).toBeLessThan(40); // pan was allowed to leave the [40,60] zoom window
	});

	it("refuses to pan at full extent (view == full data)", () => {
		const canvas = document.createElement("canvas");
		const chart = makeFakeChart(canvas, [{ x: 0, y: 0 }, { x: 50, y: 5 }, { x: 100, y: 0 }]);
		finalizeChart(canvas, chart, "t"); // scales stay at [0,100]

		canvas.dispatchEvent(new MouseEvent("mousedown", { button: 0, clientX: 0 }));
		canvas.dispatchEvent(new MouseEvent("mousemove", { clientX: 30 }));
		canvas.dispatchEvent(new MouseEvent("mouseup"));

		expect(chart.zoomScale).not.toHaveBeenCalled();
	});
});

describe("pinch keeps isChartPanning (CG-03)", () => {
	it("stays true from pan→pinch until the pinch ends", () => {
		const canvas = document.createElement("canvas");
		const chart = makeFakeChart(canvas, [{ x: 0, y: 0 }, { x: 50, y: 5 }, { x: 100, y: 0 }]);
		finalizeChart(canvas, chart, "t");
		const xs = must(chart.scales.x, 'fake has an x scale');
		xs.min = 40; xs.max = 60; // zoomed in so a 1-finger pan can start

		canvas.dispatchEvent(touch("touchstart", [{ clientX: 10 }]));   // 1-finger pan begins
		canvas.dispatchEvent(touch("touchstart", [{ clientX: 10 }, { clientX: 30 }])); // 2nd finger → pinch
		expect(isChartPanning()).toBe(true); // flag re-asserted, not dropped

		canvas.dispatchEvent(touch("touchend", [])); // all fingers up
		expect(isChartPanning()).toBe(false);
	});
});

// The gap this closes: the canvas claimed the pinch with touch-action + a preventDefault on its OWN
// pinch path only, and iOS Safari ignores touch-action for page zoom — so pinching an elevation chart
// zoomed the chart on Android and the whole document on iPhone. The refusal is pinned in full in
// claim-pinch-gesture.test.ts; what matters here is that finalizeChart wires it onto the canvas.
describe("finalizeChart refuses the browser's page pinch", () => {
	function gesture(canvas: HTMLCanvasElement, type: string): boolean {
		const e = new Event(type, { bubbles: true, cancelable: true });
		canvas.dispatchEvent(e);
		return e.defaultPrevented;
	}

	function setup() {
		const canvas = document.createElement("canvas");
		const chart = makeFakeChart(canvas, [{ x: 0, y: 0 }, { x: 50, y: 5 }, { x: 100, y: 0 }]);
		finalizeChart(canvas, chart, "t");
		return canvas;
	}

	it("refuses the iOS gesture events, which touch-action cannot cover", () => {
		const canvas = setup();
		for (const type of ["gesturestart", "gesturechange", "gestureend"]) {
			expect(gesture(canvas, type), type).toBe(true);
		}
	});

	it("refuses a two-finger touchmove even on the paths the pinch handler early-returns from", () => {
		const canvas = setup();
		// No preceding touchstart → _pinchActive is false, so the chart's own pinch handler bails.
		// The gesture must still not reach the browser.
		const e = touch("touchmove", [{ clientX: 10 }, { clientX: 90 }]);
		canvas.dispatchEvent(e);
		expect(e.defaultPrevented).toBe(true);
	});

	it("leaves a one-finger touchmove alone, so the page still scrolls over a chart", () => {
		const canvas = setup();
		const e = touch("touchmove", [{ clientX: 10 }]);
		canvas.dispatchEvent(e);
		expect(e.defaultPrevented).toBe(false);
	});
});

describe("xZoomConfig", () => {
	it("enables wheel zoom by default", () => {
		const cfg = xZoomConfig();
		expect(cfg.zoom.wheel.enabled).toBe(true);
	});

	it("disables wheel when opts.wheel === false", () => {
		const cfg = xZoomConfig(null, { wheel: false });
		expect(cfg.zoom.wheel.enabled).toBe(false);
	});

	it("pins limits to the original axis extent", () => {
		const cfg = xZoomConfig();
		expect(cfg.limits.x.min).toBe("original");
		expect(cfg.limits.x.max).toBe("original");
	});
});

describe("clampRangeToBounds", () => {
	it("returns the range unchanged when inside the bounds", () => {
		expect(clampRangeToBounds(2, 8, 0, 10)).toEqual({ min: 2, max: 8 });
	});

	it("shifts right when past the left bound, preserving span", () => {
		expect(clampRangeToBounds(-3, 3, 0, 10)).toEqual({ min: 0, max: 6 });
	});

	it("shifts left when past the right bound, preserving span", () => {
		expect(clampRangeToBounds(7, 13, 0, 10)).toEqual({ min: 4, max: 10 });
	});

	it("collapses to the full extent when the span exceeds the bounds — never spills past a bound", () => {
		// span 20 > bounds span 10: it can't fit inside, so show the whole [0,10] extent. The result
		// must stay within bounds — the old form returned {min:-10,...}, left of boundsMin, which is a
		// contract violation (a view starting before the data, showing empty space).
		expect(clampRangeToBounds(-5, 15, 0, 10)).toEqual({ min: 0, max: 10 });
	});

	it("collapses a span equal to the bounds to exactly the bounds", () => {
		expect(clampRangeToBounds(-5, 5, 0, 10)).toEqual({ min: 0, max: 10 });
		expect(clampRangeToBounds(3, 13, 0, 10)).toEqual({ min: 0, max: 10 });
	});
});
