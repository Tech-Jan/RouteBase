import type { ChartJsInstance } from './core-types';
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { ChartPanelManager } from "./chart-panel-manager";
import { must } from "./invariant";

// Run rAF callbacks synchronously so _apply/_resizeAfterLayout resolve within the test.
beforeEach(() => {
	vi.stubGlobal("requestAnimationFrame", (cb: FrameRequestCallback): number => {
		cb(0);
		return 0;
	});
	vi.stubGlobal("cancelAnimationFrame", () => {});
});

afterEach(() => {
	vi.unstubAllGlobals();
	document.body.innerHTML = "";
});

describe("ChartPanelManager tab state", () => {
	it("toggle removes an active tab", () => {
		const mgr = new ChartPanelManager({ defaultTabs: ["speed", "elevation"] });
		mgr.toggle("speed");
		expect(mgr.activePanels()).toEqual(["elevation"]);
	});

	it("toggle refuses to remove the last active tab", () => {
		const mgr = new ChartPanelManager({ defaultTabs: ["speed"] });
		mgr.toggle("speed");
		expect(mgr.activePanels()).toEqual(["speed"]);
	});

	it("setActive ignores empty array", () => {
		const mgr = new ChartPanelManager({ defaultTabs: ["speed"] });
		mgr.setActive([]);
		expect(mgr.activePanels()).toEqual(["speed"]);
	});

	// CF-03: the accessor returns a snapshot, so a caller mutating the result can't corrupt state.
	it("activePanels returns a snapshot, not the live array", () => {
		const mgr = new ChartPanelManager({ defaultTabs: ["speed"] });
		const snap = mgr.activePanels();
		snap.push("junk");
		// Internal state is intact: "speed" is still the only real tab, so toggle keeps it (last one).
		mgr.toggle("speed");
		expect(mgr.activePanels()).toEqual(["speed"]);
	});
});

describe("ChartPanelManager _apply DOM", () => {
	it("_apply toggles .active on panels and buttons", () => {
		document.body.innerHTML = `
			<div id="chartPanelWrap">
				<div class="chart-panel" id="panel-speed"></div>
				<div class="chart-panel" id="panel-elevation"></div>
			</div>
			<button data-chart-tab="speed"></button>
			<button data-chart-tab="elevation"></button>`;
		const mgr = new ChartPanelManager({ wrapperId: "chartPanelWrap", defaultTabs: ["speed"] });
		mgr.initButtons();

		expect(document.getElementById("panel-speed")!.classList.contains("active")).toBe(true);
		expect(document.getElementById("panel-elevation")!.classList.contains("active")).toBe(false);

		const buttons = document.querySelectorAll<HTMLButtonElement>("[data-chart-tab]");
		expect(must(buttons[0], "speed tab").classList.contains("active")).toBe(true); // speed
		expect(must(buttons[1], "elevation tab").classList.contains("active")).toBe(false); // elevation
	});
});

describe("ChartPanelManager resetAllZoom", () => {
	// CF-01: resetAllZoom must re-decimate + render every chart (not just resetZoom), else the
	// dataset keeps the old window's decimation stretched across the full axis.
	it("resetZoom + update run once per chart in chartMap", () => {
		const makeChart = () =>
			({
				resetZoom: vi.fn(),
				update: vi.fn(),
				scales: { x: { min: 0, max: 100 } },
			} as unknown as ChartJsInstance & { resetZoom: ReturnType<typeof vi.fn>; update: ReturnType<typeof vi.fn> });
		const a = makeChart();
		const b = makeChart();
		const mgr = new ChartPanelManager({ chartMap: { a: () => a, b: () => b } });

		expect(() => mgr.resetAllZoom()).not.toThrow();

		for (const c of [a, b]) {
			expect(c.resetZoom).toHaveBeenCalledTimes(1);
			// redecimateChartForRange is a no-op for these non-factory charts, but update("none")
			// still applies the reset render.
			expect(c.update).toHaveBeenCalledWith("none");
			expect(c.update).toHaveBeenCalledTimes(1);
		}
	});
});
