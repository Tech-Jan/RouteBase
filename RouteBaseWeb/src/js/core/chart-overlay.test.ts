import { describe, it, expect, vi, beforeEach } from "vitest";
import { must } from "./invariant";
import {
	highlightChartIndex,
	destroyChartOverlay,
	registerChartSourceData,
	getCrosshairPixelX,
	showSplitOverlay,
} from "./chart-overlay";

type ChartInstance = InstanceType<typeof Chart>;

// jsdom has no 2d context (see chart-gesture-preview.test.ts) — a method-bag stub is enough,
// the overlay only needs the calls not to throw.
function fakeCtx2d(): CanvasRenderingContext2D {
	return {
		scale: vi.fn(), setTransform: vi.fn(), clearRect: vi.fn(), save: vi.fn(), restore: vi.fn(),
		beginPath: vi.fn(), moveTo: vi.fn(), lineTo: vi.fn(), stroke: vi.fn(),
		arc: vi.fn(), fill: vi.fn(), setLineDash: vi.fn(),
	} as unknown as CanvasRenderingContext2D;
}

// jsdom has no ResizeObserver — record instances so disconnect() can be asserted and the callback
// can be invoked directly (CG-04).
class FakeResizeObserver {
	static instances: FakeResizeObserver[] = [];
	observe = vi.fn();
	disconnect = vi.fn();
	cb: ResizeObserverCallback;
	constructor(cb: ResizeObserverCallback) {
		this.cb = cb;
		FakeResizeObserver.instances.push(this);
	}
	fire(): void { this.cb([], this as unknown as ResizeObserver); }
}

// Minimal chart whose canvas lives in a host div (the overlay is appended next to it).
// No registered source data → _drawOverlay returns right after creating the overlay state.
function makeChart(parent: HTMLElement, canvas?: HTMLCanvasElement) {
	if (!canvas) {
		canvas = document.createElement("canvas");
		parent.appendChild(canvas);
	}
	const chart = {
		canvas,
		chartArea: { top: 0, bottom: 100, left: 0, right: 200 },
		scales: {},
		data: { datasets: [] },
	} as unknown as ChartInstance;
	return { chart, canvas };
}

beforeEach(() => {
	document.body.innerHTML = "";
	FakeResizeObserver.instances = [];
	vi.stubGlobal("ResizeObserver", FakeResizeObserver);
	HTMLCanvasElement.prototype.getContext = vi.fn(fakeCtx2d) as never;
});

describe("destroyChartOverlay", () => {
	it("removes the appended overlay canvas and disconnects its ResizeObserver", () => {
		const parent = document.createElement("div");
		document.body.appendChild(parent);
		const { chart } = makeChart(parent);

		highlightChartIndex(chart, 0, true); // lazily creates overlay canvas + observer
		expect(parent.querySelectorAll("canvas").length).toBe(2); // chart + overlay
		expect(FakeResizeObserver.instances.length).toBe(1);

		destroyChartOverlay(chart);
		expect(parent.querySelectorAll("canvas").length).toBe(1); // overlay gone
		expect(must(FakeResizeObserver.instances[0], 'one observer').disconnect).toHaveBeenCalled();
	});

	it("keeps rebuilds on the same canvas at one overlay (the leak-1 cycle)", () => {
		const parent = document.createElement("div");
		document.body.appendChild(parent);
		const { chart: chartA, canvas } = makeChart(parent);

		highlightChartIndex(chartA, 0, true);
		destroyChartOverlay(chartA); // teardown before the rebuild's destroy()

		const { chart: chartB } = makeChart(parent, canvas); // new chart, same canvas
		highlightChartIndex(chartB, 0, true);
		expect(parent.querySelectorAll("canvas").length).toBe(2); // never 3
	});

	it("no-ops for null and for charts without overlay state", () => {
		expect(() => destroyChartOverlay(null)).not.toThrow();
		const parent = document.createElement("div");
		document.body.appendChild(parent);
		const { chart } = makeChart(parent);
		expect(() => destroyChartOverlay(chart)).not.toThrow();
	});
});

// A chart with linear identity scales + registered source data, so a highlight actually draws
// (sets lastPixelX) instead of returning early for a data-less stub.
function makeDrawableChart(parent: HTMLElement) {
	const canvas = document.createElement("canvas");
	parent.appendChild(canvas);
	const chart = {
		canvas,
		chartArea: { top: 0, bottom: 100, left: 0, right: 200 },
		scales: { x: { getPixelForValue: (v: number) => v }, y: { getPixelForValue: (v: number) => v } },
		data: { datasets: [{ borderColor: "#000" }] },
	} as unknown as ChartInstance;
	registerChartSourceData(chart, [0, 10, 20, 30], [{ x: 0, y: 1 }, { x: 10, y: 2 }, { x: 20, y: 3 }, { x: 30, y: 4 }]);
	return { chart, canvas };
}

describe("overlay resize invalidation (CG-04)", () => {
	it("resets currentIndex/lastPixelX so a re-highlight redraws and the stale crosshair-x is dropped", () => {
		const parent = document.createElement("div");
		document.body.appendChild(parent);
		const { chart } = makeDrawableChart(parent);

		highlightChartIndex(chart, 3, true); // draws crosshair at x=30
		expect(getCrosshairPixelX(chart)).toBe(30);

		must(FakeResizeObserver.instances[0], 'one observer').fire(); // canvas resized → pixels wiped
		expect(getCrosshairPixelX(chart)).toBeNull(); // stale x invalidated

		highlightChartIndex(chart, 3, true); // same index must NOT early-return now
		expect(getCrosshairPixelX(chart)).toBe(30); // redrawn
	});
});

describe("DPR-safe overlay clear", () => {
	it("resets the transform to identity before clearing so the whole physical canvas is wiped", () => {
		// Capture the ctx the overlay canvas is handed (first getContext call), so we can assert on it.
		const created: CanvasRenderingContext2D[] = [];
		HTMLCanvasElement.prototype.getContext = vi.fn(() => {
			const c = fakeCtx2d();
			created.push(c);
			return c;
		}) as never;

		const parent = document.createElement("div");
		document.body.appendChild(parent);
		const { chart } = makeDrawableChart(parent);

		highlightChartIndex(chart, 3, true); // draws → clears the overlay via clearOverlayCanvas

		const overlayCtx = must(created[0], 'overlay ctx'); // the overlay canvas' ctx (chart canvas ctx isn't taken by the overlay)
		expect(overlayCtx.setTransform).toHaveBeenCalledWith(1, 0, 0, 1, 0, 0);
		expect(overlayCtx.clearRect).toHaveBeenCalled();
	});
});

describe("showSplitOverlay guards (CG-05)", () => {
	it("no-ops (no throw) when the canvas is detached", () => {
		const canvas = document.createElement("canvas"); // no parent
		const chart = {
			canvas,
			chartArea: { top: 0, bottom: 100, left: 0, right: 200 },
			scales: { x: { min: 0, max: 30, getPixelForValue: (v: number) => v } },
			data: { labels: [0, 10, 20, 30] },
		} as unknown as ChartInstance;
		expect(() => showSplitOverlay(chart, 0, 1)).not.toThrow();
	});

	it("no-ops when startIndex > endIndex (no overlay div created)", () => {
		const parent = document.createElement("div");
		document.body.appendChild(parent);
		const canvas = document.createElement("canvas");
		parent.appendChild(canvas);
		const chart = {
			canvas,
			chartArea: { top: 0, bottom: 100, left: 0, right: 200 },
			scales: { x: { min: 0, max: 30, getPixelForValue: (v: number) => v } },
			data: { labels: [0, 10, 20, 30] },
		} as unknown as ChartInstance;

		showSplitOverlay(chart, 2, 1); // reversed range
		expect(parent.querySelectorAll("div").length).toBe(0); // nothing appended
	});
});
