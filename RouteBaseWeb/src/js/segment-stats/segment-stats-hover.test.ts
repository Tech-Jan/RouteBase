import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import type { EffortDataPoint, EffortPerformanceData } from "./segment-stats-effort-types";
import type { SegmentStatisticsContext } from "./segment-stats-page-types";

/**
 * The map↔chart cross-hover bridge (findClosestPointByX + the highlight/crosshair coordination) was
 * trapped in a top-level module body — reachable only as an import side effect on the real ctx
 * singleton. It is now the module-level pure `findClosestPointByX` + `initSegmentStatsHover(ctx)`.
 *
 * These drive both directly with a synthetic ctx + a recording maplibre fake:
 *  - findClosestPointByX hard: exact hit, midpoint tie → LOWER index, before/after the ends, a single
 *    point, an empty series, distance mode, percent via segmentProgress, the legacy percent→distance
 *    fallback, and duplicate/unsorted x-values (robustness, not contract).
 *  - the bridge: a chart hover paints one marker per visible effort AND a crosshair on every synced
 *    sibling; a miss and a leave BOTH bring the markers + crosshairs down (the classic stuck-highlight).
 *  - the slot round-trip: initSegmentStatsHover must assign ctx.findClosestPointByX, or the reader at
 *    updateMapMarkersForX silently falls back to `?? 0` (a wrong-point highlight with no error).
 */

// Shared singleton: importing the module runs its top-level init on this throwaway object (harmless —
// it only populates the object's function slots). Each test then drives init with its own fresh ctx.
const H = vi.hoisted(() => ({ ctx: {} as Record<string, unknown> }));
vi.mock("./segment-statistics", () => ({ ctx: H.ctx }));

// The bridge reaches the live map through map-state; return the per-test recording fake.
const mapCtl = vi.hoisted(() => ({ map: null as unknown }));
vi.mock("../core/map-state", () => ({ getMaplibreMap: () => mapCtl.map }));

// Overlay-canvas crosshair: spy so draw/clear are observable without a real canvas context.
const overlay = vi.hoisted(() => ({ draw: vi.fn(), clear: vi.fn() }));
vi.mock("../core/chart-overlay", () => ({
	drawVerticalCrosshair: overlay.draw,
	clearVerticalCrosshair: overlay.clear,
}));
vi.mock("../core/chart-error-log", () => ({ reportChartError: () => {} }));

import { findClosestPointByX, initSegmentStatsHover } from "./segment-stats-hover";
import { must } from '../core/invariant';

// ── builders ────────────────────────────────────────────────────────────────
function pt(over: Partial<EffortDataPoint>): EffortDataPoint {
	return over as unknown as EffortDataPoint;
}
/** times 0,10,20,30 · distances 0,100,200,300 · progress 0,.25,.5,.75 (index i). */
function ramp(n: number, latBase = 0, lngBase = 0): EffortDataPoint[] {
	return Array.from({ length: n }, (_, i) =>
		pt({ time: i * 10, distance: i * 100, segmentProgress: n > 1 ? i / (n - 1) : 0, lat: latBase + i, lng: lngBase + i }));
}

interface RecSource { setData: ReturnType<typeof vi.fn>; }
function makeRecordingMap(): { ml: unknown; source: () => RecSource | undefined } {
	const sources = new Map<string, RecSource>();
	const ml = {
		addSource(id: string): void { sources.set(id, { setData: vi.fn() }); },
		addLayer(): void {},
		getSource(id: string): RecSource | undefined { return sources.get(id); },
	};
	return { ml, source: () => sources.get("seg-hover-markers") };
}

const PERF: EffortPerformanceData[] = [
	{ effortId: 0, activityId: 100, activityName: "A0", activityDate: "2026-01-01", elapsedTime: 30, points: ramp(4, 1, 10) },
	{ effortId: 1, activityId: 101, activityName: "A1", activityDate: "2026-01-02", elapsedTime: 30, points: ramp(4, 50, 100) },
] as unknown as EffortPerformanceData[];

function mkCtx(over: Partial<SegmentStatisticsContext> = {}): SegmentStatisticsContext {
	return {
		config: {},
		performanceData: PERF,
		effortVisibility: new Set<number>([0, 1]),
		effortColors: ["#aa0000", "#00aa00"],
		aggregateMode: false,
		currentXAxis: "time",
		selectedEffortIndex: null,
		hoveredEffortIndex: null,
		performanceChart: null, elevationChart: null, effortChart: null, progressChart: null,
		lastHoverX: undefined,
		...over,
	} as unknown as SegmentStatisticsContext;
}

// Chart.js stand-in: nearest-x hit-test result + a scale that resolves the cursor x.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function stubChart(hoveredX: number, hit: { datasetIndex: number; index: number }): any {
	return {
		canvas: document.createElement("canvas"),
		data: { datasets: [{ data: [{ x: 0 }], hidden: false }, { data: [{ x: 0 }], hidden: false }] },
		scales: { x: { getValueForPixel: (_px: number) => hoveredX } },
		getElementsAtEventForMode: () => [hit],
	};
}
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function missChart(): any {
	return {
		canvas: document.createElement("canvas"),
		data: { datasets: [{ data: [{ x: 0 }], hidden: false }] },
		scales: { x: { getValueForPixel: () => 0 } },
		getElementsAtEventForMode: () => [],
	};
}

const microtask = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

beforeEach(() => { mapCtl.map = null; overlay.draw.mockClear(); overlay.clear.mockClear(); });
afterEach(() => { vi.restoreAllMocks(); });

describe("findClosestPointByX — point resolution search", () => {
	const p = ramp(4); // time 0,10,20,30

	it("resolves an exact hit (time)", () => {
		expect(findClosestPointByX(p, 20, "time")).toBe(2);
	});
	it("breaks a midpoint tie toward the LOWER index (time 15 → idx 1)", () => {
		expect(findClosestPointByX(p, 15, "time")).toBe(1);
	});
	it("clamps a target before the first point to index 0", () => {
		expect(findClosestPointByX(p, -5, "time")).toBe(0);
	});
	it("clamps a target past the last point to the final index", () => {
		expect(findClosestPointByX(p, 9999, "time")).toBe(3);
	});
	it("returns index 0 for a single-point series", () => {
		expect(findClosestPointByX(ramp(1), 12345, "time")).toBe(0);
	});
	it("returns 0 (no throw) for an empty series", () => {
		expect(findClosestPointByX([], 5, "time")).toBe(0);
	});
	it("resolves by distance (240 → idx 2, 200 nearer than 300)", () => {
		expect(findClosestPointByX(p, 240, "distance")).toBe(2);
	});
	it("resolves percent via segmentProgress*100 (60 → idx 2 = 50)", () => {
		expect(findClosestPointByX(p, 60, "percent")).toBe(2);
	});
	it("percent midpoint tie → LOWER index (62.5 between 50 and 75 → idx 2)", () => {
		expect(findClosestPointByX(p, 62.5, "percent")).toBe(2);
	});
	it("percent legacy fallback: no segmentProgress → convert to the effort's own distance", () => {
		// distances 0,100,200,300; percent 50 → (50/100)*300 = 150 → idx 1 (100) & idx 2 (200) equidistant → LOWER.
		const noProg = ramp(4).map((q) => pt({ time: q.time, distance: q.distance }));
		expect(findClosestPointByX(noProg, 50, "percent")).toBe(1);
		// percent 100 maps to the full distance (300) → last index.
		expect(findClosestPointByX(noProg, 100, "percent")).toBe(3);
	});
	it("returns the first of duplicate x-values without throwing", () => {
		const dup = [pt({ time: 0 }), pt({ time: 10 }), pt({ time: 10 }), pt({ time: 20 })];
		expect(findClosestPointByX(dup, 10, "time")).toBe(1);
	});
	it("never throws and stays in range on unsorted input (contract needs sorted, but must not crash)", () => {
		const unsorted = [pt({ time: 30 }), pt({ time: 0 }), pt({ time: 20 }), pt({ time: 10 })];
		const r = findClosestPointByX(unsorted, 15, "time");
		expect(Number.isInteger(r)).toBe(true);
		expect(r).toBeGreaterThanOrEqual(0);
		expect(r).toBeLessThan(unsorted.length);
	});
});

describe("initSegmentStatsHover — slot round-trip", () => {
	it("assigns ctx.findClosestPointByX to the pure export (else updateMapMarkersForX falls back to 0)", () => {
		const ctx = mkCtx();
		initSegmentStatsHover(ctx);
		expect(ctx.findClosestPointByX).toBe(findClosestPointByX);
		expect(ctx.findClosestPointByX!(ramp(4), 20, "time")).toBe(2);
	});
	it("assigns the marker + bridge seams", () => {
		const ctx = mkCtx();
		initSegmentStatsHover(ctx);
		expect(typeof ctx.showMapHighlight).toBe("function");
		expect(typeof ctx.hideMapHighlight).toBe("function");
		expect(typeof ctx.onChartHover).toBe("function");
		expect(typeof ctx.onChartLeave).toBe("function");
	});
});

describe("initSegmentStatsHover — chart hover → map highlight", () => {
	it("paints one marker per visible effort at the resolved [lng,lat], highlighting the pinned one", async () => {
		const { ml, source } = makeRecordingMap();
		mapCtl.map = ml;
		const ctx = mkCtx({ selectedEffortIndex: 1 });
		initSegmentStatsHover(ctx);

		ctx.onChartHover!(stubChart(20, { datasetIndex: 0, index: 2 }), { x: 50, type: "mousemove" } as never);
		await microtask();

		const setData = source()!.setData;
		expect(setData).toHaveBeenCalledTimes(1);
		const fc = setData.mock.calls.at(-1)![0] as { features: Array<{ properties: { highlighted: boolean }; geometry: { coordinates: [number, number] } }> };
		expect(fc.features).toHaveLength(2);
		// effort 0 idx-2 = lat 3, lng 12 → GeoJSON [lng,lat]; effort 1 idx-2 = lat 52, lng 102 (highlighted).
		expect(must(fc.features[0], 'feature 0').geometry.coordinates).toEqual([12, 3]);
		expect(must(fc.features[0], 'feature 0').properties.highlighted).toBe(false);
		expect(must(fc.features[1], 'feature 1').geometry.coordinates).toEqual([102, 52]);
		expect(must(fc.features[1], 'feature 1').properties.highlighted).toBe(true);
	});

	it("skips a hidden effort's marker", async () => {
		const { ml, source } = makeRecordingMap();
		mapCtl.map = ml;
		const ctx = mkCtx({ effortVisibility: new Set<number>([0]) }); // effort 1 hidden
		initSegmentStatsHover(ctx);

		ctx.onChartHover!(stubChart(20, { datasetIndex: 0, index: 2 }), { x: 50, type: "mousemove" } as never);
		await microtask();
		const fc = source()!.setData.mock.calls.at(-1)![0] as { features: unknown[] };
		expect(fc.features).toHaveLength(1);
	});

	it("draws the crosshair on the hovered chart and every synced sibling at the hovered x", () => {
		const ctx = mkCtx({
			performanceChart: { id: "perf" } as never,
			elevationChart: { id: "elev" } as never,
			effortChart: { id: "cost" } as never,
		});
		initSegmentStatsHover(ctx);
		const hovered = ctx.performanceChart; // pass the same instance so it's the "origin" chart
		const chart = stubChart(20, { datasetIndex: 0, index: 2 });
		// splice the origin chart's identity in so otherCharts excludes it
		Object.assign(chart, { id: "perf" });
		ctx.performanceChart = chart as never;
		ctx.onChartHover!(chart, { x: 50, type: "mousemove" } as never);

		// crosshair on hovered + the two siblings, all at x=20
		const xs = overlay.draw.mock.calls.map((c) => c[1]);
		expect(overlay.draw).toHaveBeenCalledTimes(3);
		expect(xs).toEqual([20, 20, 20]);
		void hovered;
	});
});

describe("initSegmentStatsHover — clearing (no stuck highlight)", () => {
	it("a hover MISS drops the markers and clears the crosshair", async () => {
		const { ml, source } = makeRecordingMap();
		mapCtl.map = ml;
		const ctx = mkCtx();
		initSegmentStatsHover(ctx);

		// prime a marker set, then miss
		ctx.onChartHover!(stubChart(20, { datasetIndex: 0, index: 2 }), { x: 50, type: "mousemove" } as never);
		await microtask();
		ctx.onChartHover!(missChart(), { x: 999, type: "mousemove" } as never);
		await microtask();

		const fc = source()!.setData.mock.calls.at(-1)![0] as { features: unknown[] };
		expect(fc.features).toHaveLength(0);
		expect(overlay.clear).toHaveBeenCalled();
	});

	it("onChartLeave brings BOTH the markers and every crosshair down", async () => {
		const { ml, source } = makeRecordingMap();
		mapCtl.map = ml;
		const progress = {
			data: { datasets: [{ data: [] }] },
			getActiveElements: () => [{ datasetIndex: 0, index: 0 }],
			setActiveElements: vi.fn(),
			tooltip: undefined,
			draw: vi.fn(),
		};
		const ctx = mkCtx({
			performanceChart: { id: "perf" } as never,
			elevationChart: { id: "elev" } as never,
			effortChart: { id: "cost" } as never,
			progressChart: progress as never,
		});
		initSegmentStatsHover(ctx);

		ctx.onChartHover!(stubChart(20, { datasetIndex: 0, index: 2 }), { x: 50, type: "mousemove" } as never);
		await microtask();
		overlay.clear.mockClear();

		ctx.onChartLeave!();
		await microtask();

		const fc = source()!.setData.mock.calls.at(-1)![0] as { features: unknown[] };
		expect(fc.features).toHaveLength(0);                 // markers gone
		expect(overlay.clear).toHaveBeenCalledTimes(3);      // perf + elev + effort crosshairs
		expect(progress.setActiveElements).toHaveBeenCalledWith([]); // progress active element cleared
	});
});
