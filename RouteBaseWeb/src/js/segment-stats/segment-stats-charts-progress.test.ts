import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

// Null the shared ctx at import so the module's top-level wiring is a no-op; each test drives
// renderProgressChart with its own fake ctx + effortDates.
vi.mock("./segment-statistics", () => ({ ctx: null }));

// The crosshair must go through the shared kernel (verticalLinePlugin was folded into
// core/chart-overlay.strokeCrosshairLine). Mock it so we can assert the plugin calls IT, rather than
// re-drawing a hard-coded #dc3545 line by hand as it once did.
vi.mock("../core/chart-overlay", () => ({ strokeCrosshairLine: vi.fn() }));

import { renderProgressChart } from "./segment-stats-charts-progress";
import { strokeCrosshairLine } from "../core/chart-overlay";
import type { SegmentStatisticsContext } from "./segment-stats-page-types";
import type { EffortDateData } from "./segment-stats-effort-types";
import { must } from '../core/invariant';

interface Built { type: string; data: unknown; options: unknown; plugins: unknown[]; }
let built: Built[] = [];
class FakeChart {
	static destroyed = 0;
	constructor(_ctx: unknown, config: { type: string; data: unknown; options: unknown; plugins?: unknown[] }) {
		built.push({ type: config.type, data: config.data, options: config.options, plugins: config.plugins ?? [] });
	}
	destroy(): void { FakeChart.destroyed++; }
}

// Typed views onto the captured config — only the fields these tests assert on.
interface ScatterDataset { data: Array<{ x: number; y: number }>; pointRadius: number; showLine: boolean; hidden: boolean; }
interface VertPlugin { id: string; afterDatasetsDraw: (chart: unknown) => void; }
const datasets = (b: Built): ScatterDataset[] => (b.data as { datasets: ScatterDataset[] }).datasets;
const options = (b: Built): { scales: { x: { type: string }; y: { reverse: boolean } }; plugins: { title: { text: string } } } =>
	b.options as { scales: { x: { type: string }; y: { reverse: boolean } }; plugins: { title: { text: string } } };
const plugin0 = (b: Built): VertPlugin => b.plugins[0] as VertPlugin;

function fakeCtx(over: Partial<SegmentStatisticsContext> = {}): SegmentStatisticsContext {
	const visible = over.effortVisibility ? [...over.effortVisibility] : [0, 1];
	return {
		progressChart: null,
		progressXAxis: "equal",
		progressYMetric: "time",
		effortColors: ["#aa0000", "#00aa00", "#0000aa"],
		getVisibleEffortIndices: () => visible,
		...over,
	} as unknown as SegmentStatisticsContext;
}

function dates(...rows: Array<Partial<EffortDateData>>): EffortDateData[] {
	return rows.map((r) => ({ time: 0, speed: 0, elevation: 0, ...r }));
}

beforeEach(() => {
	document.body.innerHTML = "";
	built = [];
	FakeChart.destroyed = 0;
	vi.mocked(strokeCrosshairLine).mockClear();
	HTMLCanvasElement.prototype.getContext = vi.fn(() => ({}) as unknown as CanvasRenderingContext2D) as never;
	vi.stubGlobal("Chart", FakeChart);
});

afterEach(() => { vi.unstubAllGlobals(); });

function mountCanvas(): HTMLElement {
	const host = document.createElement("div");
	const canvas = document.createElement("canvas");
	canvas.id = "progressChart";
	host.appendChild(canvas);
	document.body.appendChild(host);
	return host;
}

describe("renderProgressChart — scatter dataset shape", () => {
	it("plots one point per effort in chronological x-position, plus a connector over the visible ones", () => {
		mountCanvas();
		const ed = dates(
			{ date: "2024-01-01", time: 100, name: "A" },
			{ date: "2024-01-02", time: 200, name: "B" },
		);
		renderProgressChart(fakeCtx({ getVisibleEffortIndices: () => [0, 1] }), ed);

		expect(built).toHaveLength(1);
		expect(must(built[0], 'one chart built').type).toBe("scatter");
		const ds = datasets(must(built[0], 'one chart built'));
		// Two effort datasets + one connector line.
		expect(ds).toHaveLength(3);
		expect(must(ds[0], 'dataset 0').data[0]).toEqual({ x: 0, y: 100 }); // A: chrono position 0
		expect(must(ds[1], 'dataset 1').data[0]).toEqual({ x: 1, y: 200 }); // B: chrono position 1
		expect(must(ds[0], 'dataset 0').pointRadius).toBe(8);               // visible + has value → large dot
		expect(must(ds[2], 'dataset 2').showLine).toBe(true);               // connector
		expect(must(ds[2], 'dataset 2').data).toHaveLength(2);
	});

	it("hides non-visible efforts, shrinks their dot, and drops the connector below two points", () => {
		mountCanvas();
		const ed = dates(
			{ date: "2024-01-01", time: 100, name: "A" },
			{ date: "2024-01-02", time: 200, name: "B" },
		);
		renderProgressChart(fakeCtx({ getVisibleEffortIndices: () => [0] }), ed);

		const ds = datasets(must(built[0], 'one chart built'));
		expect(ds).toHaveLength(2);          // no connector: only one visible point
		expect(must(ds[0], 'dataset 0').hidden).toBe(false);
		expect(must(ds[0], 'dataset 0').pointRadius).toBe(8);
		expect(must(ds[1], 'dataset 1').hidden).toBe(true);     // effort 1 not visible
		expect(must(ds[1], 'dataset 1').pointRadius).toBe(3);
	});

	it("an effort with no metric value carries an empty series and is hidden", () => {
		mountCanvas();
		// Power metric, but effort B has no power → hasValue false.
		const ed = dates(
			{ date: "2024-01-01", time: 100, name: "A", power: 250 },
			{ date: "2024-01-02", time: 200, name: "B" },
		);
		renderProgressChart(fakeCtx({ progressYMetric: "power", getVisibleEffortIndices: () => [0, 1] }), ed);

		const ds = datasets(must(built[0], 'one chart built'));
		expect(must(ds[0], 'dataset 0').data).toEqual([{ x: 0, y: 250 }]);
		expect(must(ds[1], 'dataset 1').data).toEqual([]);
		expect(must(ds[1], 'dataset 1').hidden).toBe(true); // visible index but no value → still hidden
	});
});

describe("renderProgressChart — metric axis config", () => {
	it("reverses the y-axis for time (faster = lower = better) and titles it accordingly", () => {
		mountCanvas();
		renderProgressChart(fakeCtx({ progressYMetric: "time" }), dates(
			{ date: "2024-01-01", time: 100, name: "A" },
			{ date: "2024-01-02", time: 200, name: "B" },
		));
		expect(options(must(built[0], 'one chart built')).scales.y.reverse).toBe(true);
		expect(options(must(built[0], 'one chart built')).plugins.title.text).toBe("Segment Time Over Time");
	});

	it("does NOT reverse the y-axis for speed, and uses a linear x-axis under equal spacing", () => {
		mountCanvas();
		renderProgressChart(fakeCtx({ progressYMetric: "speed", progressXAxis: "equal" }), dates(
			{ date: "2024-01-01", time: 100, name: "A", speed: 20 },
			{ date: "2024-01-02", time: 200, name: "B", speed: 25 },
		));
		expect(options(must(built[0], 'one chart built')).scales.y.reverse).toBe(false);
		expect(options(must(built[0], 'one chart built')).scales.x.type).toBe("linear");
		expect(options(must(built[0], 'one chart built')).plugins.title.text).toBe("Avg Speed Over Time");
	});
});

describe("renderProgressChart — crosshair through the shared kernel", () => {
	it("the vertical-line plugin strokes via strokeCrosshairLine at the active element's x", () => {
		mountCanvas();
		renderProgressChart(fakeCtx(), dates(
			{ date: "2024-01-01", time: 100, name: "A" },
			{ date: "2024-01-02", time: 200, name: "B" },
		));

		const plugin = plugin0(must(built[0], 'one chart built'));
		expect(plugin.id).toBe("segStatProgressVertLine");

		const chartCtx = {} as CanvasRenderingContext2D;
		plugin.afterDatasetsDraw({
			ctx: chartCtx,
			chartArea: { top: 5, bottom: 95 },
			tooltip: { _active: [{ element: { x: 42 } }] },
		});
		expect(strokeCrosshairLine).toHaveBeenCalledWith(chartCtx, 42, 5, 95);
	});

	it("draws nothing when no point is active (empty tooltip)", () => {
		mountCanvas();
		renderProgressChart(fakeCtx(), dates(
			{ date: "2024-01-01", time: 100, name: "A" },
			{ date: "2024-01-02", time: 200, name: "B" },
		));
		plugin0(must(built[0], 'one chart built')).afterDatasetsDraw({
			ctx: {} as CanvasRenderingContext2D,
			chartArea: { top: 5, bottom: 95 },
			tooltip: { _active: [] },
		});
		expect(strokeCrosshairLine).not.toHaveBeenCalled();
	});
});

describe("renderProgressChart — empty state", () => {
	it("shows the shared empty message, builds no chart, and destroys+nulls any prior chart", () => {
		const host = mountCanvas();
		const destroy = vi.fn();
		const ctx = fakeCtx({ progressChart: { destroy } as never });

		renderProgressChart(ctx, dates({ date: "2024-01-01", time: 100, name: "A" }));

		expect(built).toHaveLength(0);
		expect(destroy).toHaveBeenCalledTimes(1);
		expect(ctx.progressChart).toBeNull();
		const overlay = host.querySelector<HTMLElement>(".chart-empty-state");
		expect(overlay?.textContent).toBe("Need at least two efforts to chart progress over time.");
		expect(host.querySelector("canvas")?.style.visibility).toBe("hidden");
	});

	it("no-ops (no throw, no chart) when the canvas is absent", () => {
		expect(() => renderProgressChart(fakeCtx(), dates(
			{ date: "2024-01-01", time: 100, name: "A" },
			{ date: "2024-01-02", time: 200, name: "B" },
		))).not.toThrow();
		expect(built).toHaveLength(0);
	});
});
