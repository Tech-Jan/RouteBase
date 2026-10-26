import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

// The module reads the shared ctx singleton at import to decide whether to auto-paint. Null it so the
// import is a clean no-op and each test drives renderDistributionChart with its own fake ctx + inputs.
vi.mock("./segment-statistics", () => ({ ctx: null }));

// Theme colours must come from cssVar (the item-3 regression: -charts-distribution.ts:55-60 once read a
// raw getComputedStyle block). Mock cssVar to a per-name sentinel so the assertions PROVE the chart's
// colours are cssVar lookups, not literals — a hard-coded colour would fail these.
vi.mock("../core/css-vars", () => ({ cssVar: (name: string) => "VAR" + name }));

import { renderDistributionChart } from "./segment-stats-charts-distribution";
import type { SegmentStatisticsContext } from "./segment-stats-page-types";
import type { EffortDateData } from "./segment-stats-effort-types";
import { must } from '../core/invariant';

// Minimal Chart.js stand-in: records every construction's (type, data, options) so we can assert on the
// dataset/labels/scales the render path actually built. destroy() lets us pin the rebuild teardown.
interface Built { type: string; data: unknown; options: unknown; }
let built: Built[] = [];
class FakeChart {
	static destroyed = 0;
	constructor(_ctx: unknown, config: { type: string; data: unknown; options: unknown }) {
		built.push({ type: config.type, data: config.data, options: config.options });
	}
	destroy(): void { FakeChart.destroyed++; }
}

// Typed views onto the captured config — only the fields these tests assert on.
interface BarData { labels: unknown[]; datasets: Array<{ data: number[]; backgroundColor: string[] }>; }
interface BarOptions { scales: { x: { grid: { color: string }; ticks: { color: string } } }; }
const barData = (b: Built): BarData => b.data as BarData;
const barOptions = (b: Built): BarOptions => b.options as BarOptions;

/** ctx carrying only what the distribution render path reads. */
function fakeCtx(): SegmentStatisticsContext {
	return { distributionChart: null } as unknown as SegmentStatisticsContext;
}

function own(times: number[]): EffortDateData[] {
	return times.map((t) => ({ time: t, speed: 0, elevation: 0, isOwn: true }));
}

beforeEach(() => {
	document.body.innerHTML = "";
	built = [];
	FakeChart.destroyed = 0;
	HTMLCanvasElement.prototype.getContext = vi.fn(() => ({}) as unknown as CanvasRenderingContext2D) as never;
	vi.stubGlobal("Chart", FakeChart);
});

afterEach(() => {
	vi.unstubAllGlobals();
});

/** A canvas inside a host div, as the page renders it. */
function mountCanvas(): HTMLElement {
	const host = document.createElement("div");
	const canvas = document.createElement("canvas");
	canvas.id = "distributionChart";
	host.appendChild(canvas);
	document.body.appendChild(host);
	return host;
}

describe("renderDistributionChart — dataset shape", () => {
	it("buckets ten evenly-spaced own efforts into ten one-count buckets", () => {
		mountCanvas();
		// 100..370 step 30: range 270, bucketSize ceil(270/10)=27, ten buckets, one effort each.
		renderDistributionChart(fakeCtx(), own([100, 130, 160, 190, 220, 250, 280, 310, 340, 370]));

		expect(built).toHaveLength(1);
		expect(must(built[0], 'one chart built').type).toBe("bar");
		const ds = must(barData(must(built[0], 'one chart built')).datasets[0], 'one dataset');
		expect(ds.data).toEqual([1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);
		expect(barData(must(built[0], 'one chart built')).labels).toHaveLength(10);
	});

	it("piles ties into one bucket and lands the slowest at the far end", () => {
		mountCanvas();
		// 100×3 then 500: range 400, bucketSize 40, ten buckets; bucket0 gets the three PRs, bucket9 the 500.
		renderDistributionChart(fakeCtx(), own([100, 100, 100, 500]));

		const ds = must(barData(must(built[0], 'one chart built')).datasets[0], 'one dataset');
		expect(ds.data[0]).toBe(3);
		expect(ds.data[9]).toBe(1);
		expect(ds.data.reduce((a, b) => a + b, 0)).toBe(4);
	});
});

describe("renderDistributionChart — theme via cssVar (item 3)", () => {
	it("paints the PR bucket in --acc-cyan and the rest in --bg-4, both read through cssVar", () => {
		mountCanvas();
		renderDistributionChart(fakeCtx(), own([100, 130, 160, 190, 220, 250, 280, 310, 340, 370]));

		const colors = must(barData(must(built[0], 'one chart built')).datasets[0], 'one dataset').backgroundColor;
		expect(colors[0]).toBe("VAR--acc-cyan"); // PR bucket highlighted
		expect(colors[1]).toBe("VAR--bg-4");     // every other bucket
		// Grid / tick colours are cssVar lookups too — not literals.
		expect(barOptions(must(built[0], 'one chart built')).scales.x.grid.color).toBe("VAR--line");
		expect(barOptions(must(built[0], 'one chart built')).scales.x.ticks.color).toBe("VAR--fg-3");
	});
});

describe("renderDistributionChart — empty + rebuild contract", () => {
	it("shows the shared empty state and builds NO chart with fewer than two own efforts", () => {
		const host = mountCanvas();
		renderDistributionChart(fakeCtx(), own([100]));

		expect(built).toHaveLength(0); // no blank canvas Chart
		const overlay = host.querySelector<HTMLElement>(".chart-empty-state");
		expect(overlay?.textContent).toBe("Need more efforts to bucket — go ride!");
		expect(overlay?.style.display).toBe("flex");
		expect(host.querySelector("canvas")?.style.visibility).toBe("hidden");
	});

	it("clears the empty overlay and restores the canvas once there is data", () => {
		const host = mountCanvas();
		renderDistributionChart(fakeCtx(), own([100]));          // empty → overlay shown
		renderDistributionChart(fakeCtx(), own([100, 200, 300])); // data → overlay hidden

		expect(built).toHaveLength(1);
		expect(host.querySelector<HTMLElement>(".chart-empty-state")?.style.display).toBe("none");
		expect(host.querySelector("canvas")?.style.visibility).toBe("");
	});

	it("destroys the previous chart before building the next (no leaked instance)", () => {
		mountCanvas();
		const ctx = fakeCtx();
		ctx.distributionChart = { destroy: () => { FakeChart.destroyed++; } } as never;
		renderDistributionChart(ctx, own([100, 200, 300]));
		expect(FakeChart.destroyed).toBe(1);
	});

	it("no-ops (no throw, no chart) when the canvas is absent", () => {
		expect(() => renderDistributionChart(fakeCtx(), own([100, 200, 300]))).not.toThrow();
		expect(built).toHaveLength(0);
	});
});
