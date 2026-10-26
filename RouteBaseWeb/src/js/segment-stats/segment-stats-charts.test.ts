import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

/**
 * The perf/elevation/effort-cost render path was trapped in a top-level `if (ctx…)` module IIFE, so it
 * could only be verified by eye in a browser. It is now `initSegmentStatsCharts(ctx)` — this drives that
 * function with a synthetic ctx + performance data and asserts on the three Chart CONFIGS the render
 * path assembles (dataset series, x/y axes, the decimation switch). The dataset math itself lives in the
 * already-tested delta system; what is pinned HERE is the assembly `makeMetricChart` performs — the glue
 * only the browser used to exercise.
 *
 * ctx is the shared singleton every collaborator (delta/aggregate/readout) imports, so the mock provides
 * ONE object and the test passes that same object to init — mirroring production, where the top-level
 * call passes the module's own ctx. The chunked loader is stubbed to call onLoaded synchronously (no
 * fetch), which is the only edge between wiring the card and the first render.
 */

const H = vi.hoisted(() => ({
	// hasEfforts:false at import → the module's top-level guard is a no-op; the test flips it and calls init.
	ctx: { config: { hasEfforts: false } } as Record<string, unknown>,
}));

vi.mock("./segment-statistics", () => ({ ctx: H.ctx }));
vi.mock("./segment-stats-charts-load", () => ({
	createChartLoad: (deps: { onLoaded: () => void }) => ({
		load: () => deps.onLoaded(), // synchronous: reach the first render without a fetch
		maybeAutoAggregate: () => {},
		syncAggregateButton: () => {},
		setAggregateManual: () => {},
	}),
}));

import { initSegmentStatsCharts } from "./segment-stats-charts";
import { ctx } from "./segment-statistics";
import type { SegmentStatisticsContext } from "./segment-stats-page-types";
import { installFakeRaf, type FakeRaf } from '../core/raf-test-harness';
import { must } from '../core/invariant';

interface Built { type: string; data: unknown; options: unknown; }
let built: Built[] = [];

// Chart.js stand-in that records each construction's config and carries the members finalizeChart /
// freezeAxesForXZoom read (scales with axis + options, update). Modelled on core/chart-factory.test.ts.
class FakeChart {
	canvas: HTMLCanvasElement;
	data: unknown;
	options: unknown;
	scales: Record<string, unknown>;
	chartArea = { top: 0, bottom: 100, left: 0, right: 200 };
	constructor(ctx2d: { canvas: HTMLCanvasElement }, config: { type: string; data: unknown; options: unknown }) {
		this.canvas = ctx2d.canvas;
		this.data = config.data;
		this.options = config.options;
		built.push({ type: config.type, data: config.data, options: config.options });
		this.scales = {
			x: { min: 0, max: 100, axis: "x", options: { ticks: {} }, getValueForPixel: (p: number) => p, getPixelForValue: (v: number) => v },
			y: { min: 0, max: 50, axis: "y", options: { ticks: {} }, getPixelForValue: (v: number) => v },
		};
	}
	update(): void {}
	destroy(): void {}
	resetZoom(): void {}
	isZoomedOrPanned(): boolean { return false; }
	getElementsAtEventForMode(): unknown[] { return []; }
	zoomScale(): void {}
}

// Typed views onto the captured config — only the fields these tests assert on.
interface LineDataset { data: Array<{ x: number; y: number }>; hidden: boolean; }
interface LineOptions {
	scales: { x: { type: string; min: number }; y: { min: number; max: number } };
	plugins: { decimation: { enabled: boolean } };
}
const dsOf = (b: Built): LineDataset[] => (b.data as { datasets: LineDataset[] }).datasets;
const optOf = (b: Built): LineOptions => b.options as LineOptions;

interface Pt { distance: number; time: number; speed: number; altitude: number; cumulativeCostKj: number; lat: null; lng: null; }
function pt(distance: number, time: number, speed: number, altitude: number, cost: number): Pt {
	return { distance, time, speed, altitude, cumulativeCostKj: cost, lat: null, lng: null };
}
function effort(id: number, name: string, elapsed: number, points: Pt[]) {
	return { effortId: id, activityId: id, activityName: name, elapsedTime: elapsed, points };
}

/** Two efforts: effort 0 has three points, effort 1 four — so per-chart dataset lengths differ. */
const PERF_DATA = [
	effort(1, "Ride A", 20, [pt(0, 0, 10, 100, 0), pt(100, 10, 12, 110, 5), pt(200, 20, 11, 120, 10)]),
	effort(2, "Ride B", 30, [pt(0, 0, 9, 200, 0), pt(80, 10, 10, 210, 4), pt(170, 20, 11, 220, 8), pt(260, 30, 12, 230, 12)]),
];

/** Reset the shared ctx to a valid, full-effort state before each test. */
function resetCtx(over: Partial<SegmentStatisticsContext> = {}): void {
	const vis = new Set<number>([0, 1]);
	Object.assign(H.ctx, {
		config: { hasEfforts: true },
		effortColors: ["#aa0000", "#00aa00"],
		effortVisibility: vis,
		effortStats: new Set<number>([0, 1]),
		selectedEffortIndex: null,
		performanceChart: null, elevationChart: null, effortChart: null,
		performanceData: PERF_DATA,
		perfPointBudget: 0,
		currentXAxis: "time",
		deltaCharts: new Set<string>(),
		aggregateMode: false,
		getVisibleEffortIndices: () => Array.from(H.ctx.effortVisibility as Set<number>),
		_rerenderStrip: null,
		_renderProgressChart: null,
		...over,
	});
}

/** Add the three comparison-chart canvases the render path looks up by id. */
function mountCanvases(): void {
	for (const id of ["performanceChart", "elevationChart", "effortChart"]) {
		const wrap = document.createElement("div");
		const c = document.createElement("canvas");
		c.id = id;
		wrap.appendChild(c);
		document.body.appendChild(wrap);
	}
}

let raf: FakeRaf;

beforeEach(() => {
	document.body.innerHTML = "";
	built = [];
	resetCtx();
	HTMLCanvasElement.prototype.getContext = vi.fn(function (this: HTMLCanvasElement) {
		return {
			canvas: this,
			scale: () => {}, clearRect: () => {}, save: () => {}, restore: () => {},
			beginPath: () => {}, moveTo: () => {}, lineTo: () => {}, stroke: () => {},
			arc: () => {}, fill: () => {}, drawImage: () => {}, setTransform: () => {},
			rect: () => {}, clip: () => {}, createLinearGradient: () => ({ addColorStop: () => {} }),
		};
	}) as never;
	HTMLCanvasElement.prototype.getBoundingClientRect = vi.fn(
		() => ({ left: 0, top: 0, width: 400, height: 200, right: 400, bottom: 200 })) as never;
	raf = installFakeRaf();
	vi.stubGlobal("Chart", FakeChart);
});

afterEach(() => {
	raf.restore(); vi.unstubAllGlobals(); });

describe("initSegmentStatsCharts — three line charts from performance data", () => {
	it("builds perf, elevation and effort-cost charts, one dataset per effort", () => {
		mountCanvases();
		initSegmentStatsCharts(ctx);

		expect(built).toHaveLength(3);
		expect(built.every((b) => b.type === "line")).toBe(true);
		for (const b of built) expect(dsOf(b)).toHaveLength(2); // two efforts
		expect(ctx.performanceChart).not.toBeNull();
		expect(ctx.elevationChart).not.toBeNull();
		expect(ctx.effortChart).not.toBeNull();
	});

	it("maps the elevation series straight off the points (x=time, y=altitude), unsmoothed", () => {
		mountCanvases();
		initSegmentStatsCharts(ctx);

		// must(built[1], 'chart 1 built') is the elevation chart (perf=0, elev=1, effort=2). Altitude is not smoothed, so the
		// series is exactly the input — a clean pin on "dataset shape from a known input".
		const elev = dsOf(must(built[1], 'chart 1 built'));
		expect(must(elev[0], 'dataset 0').data).toEqual([{ x: 0, y: 100 }, { x: 10, y: 110 }, { x: 20, y: 120 }]);
		expect(must(elev[1], 'dataset 1').data).toEqual([{ x: 0, y: 200 }, { x: 10, y: 210 }, { x: 20, y: 220 }, { x: 30, y: 230 }]);
	});

	it("plots each effort against its own point times on a linear x-axis anchored at 0", () => {
		mountCanvases();
		initSegmentStatsCharts(ctx);

		const perf = must(built[0], 'chart 0 built');
		expect(optOf(perf).scales.x.type).toBe("linear");
		expect(optOf(perf).scales.x.min).toBe(0);
		expect(must(dsOf(perf)[0], 'dataset 0').data.map((p) => p.x)).toEqual([0, 10, 20]);
	});

	it("fits the y-axis to robust bounds (numbers), so an outlier can't squash the plot", () => {
		mountCanvases();
		initSegmentStatsCharts(ctx);
		const y = optOf(must(built[1], 'chart 1 built')).scales.y; // elevation
		expect(typeof y.min).toBe("number");
		expect(typeof y.max).toBe("number");
		expect(y.max).toBeGreaterThan(y.min);
	});
});

describe("initSegmentStatsCharts — visibility + decimation switch", () => {
	it("marks the hidden effort's dataset hidden on every chart", () => {
		mountCanvases();
		resetCtx({ effortVisibility: new Set<number>([0]) }); // effort 1 hidden
		initSegmentStatsCharts(ctx);

		for (const b of built) {
			expect(must(dsOf(b)[0], 'dataset 0').hidden).toBe(false);
			expect(must(dsOf(b)[1], 'dataset 1').hidden).toBe(true);
		}
	});

	it("enables client LTTB decimation only at full-res budget (0), off otherwise", () => {
		mountCanvases();
		initSegmentStatsCharts(ctx); // perfPointBudget 0
		expect(optOf(must(built[0], 'chart 0 built')).plugins.decimation.enabled).toBe(true);

		built = [];
		resetCtx({ perfPointBudget: 250 }); // server already downsampled → no client re-slice
		initSegmentStatsCharts(ctx);
		expect(optOf(must(built[0], 'chart 0 built')).plugins.decimation.enabled).toBe(false);
	});
});

describe("initSegmentStatsCharts — guards", () => {
	it("renders no chart and does not throw when the canvases are absent (the chartless-card guard)", () => {
		// No mountCanvases() — the whole render/load block is skipped.
		expect(() => initSegmentStatsCharts(ctx)).not.toThrow();
		expect(built).toHaveLength(0);
		expect(ctx.performanceChart).toBeNull();
	});

	it("returns early inside render when performance data is null", () => {
		mountCanvases();
		resetCtx({ performanceData: null });
		initSegmentStatsCharts(ctx);
		expect(built).toHaveLength(0); // renderChart's `if (!ctx.performanceData) return`
	});
});
