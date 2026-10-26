// @vitest-environment node
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { reportChartError, _resetChartErrorLog } from "./chart-error-log";

// The whole point of this module is that a swallowed chart failure stops being invisible
// WITHOUT the console filling up when a rebuilding chart fails every frame. These pin both.

describe("reportChartError", () => {
	let warn: ReturnType<typeof vi.spyOn>;

	beforeEach(() => {
		_resetChartErrorLog();
		warn = vi.spyOn(console, "warn").mockImplementation(() => {});
	});
	afterEach(() => {
		warn.mockRestore();
	});

	it("surfaces the failure with the label and the error", () => {
		const err = new Error("boom");
		reportChartError("chart-overlay:crosshair", err);
		expect(warn).toHaveBeenCalledTimes(1);
		expect(warn.mock.calls[0][0]).toContain("chart-overlay:crosshair");
		expect(warn.mock.calls[0][1]).toBe(err);
	});

	it("throttles repeats of the SAME label (a per-frame failure logs once, not 60x)", () => {
		for (let i = 0; i < 60; i++) reportChartError("chart-overlay:overlayDraw", new Error(String(i)));
		expect(warn).toHaveBeenCalledTimes(1);
	});

	it("does NOT throttle across DIFFERENT labels — distinct surfaces each surface once", () => {
		reportChartError("chart-overlay:highlight", new Error("a"));
		reportChartError("chart-panel-manager:resize:speed", new Error("b"));
		reportChartError("chart-zoom-pan:isZoomed:elevation", new Error("c"));
		expect(warn).toHaveBeenCalledTimes(3);
	});

	it("resets throttle state (test seam) so a label can log again", () => {
		reportChartError("x", new Error("1"));
		reportChartError("x", new Error("2")); // throttled
		_resetChartErrorLog();
		reportChartError("x", new Error("3")); // logs again
		expect(warn).toHaveBeenCalledTimes(2);
	});
});
