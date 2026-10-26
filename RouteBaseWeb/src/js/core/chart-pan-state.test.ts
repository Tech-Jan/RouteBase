// @vitest-environment node
import { describe, it, expect, afterEach } from "vitest";
import { isChartPanning, setChartPanning } from "./chart-pan-state";

afterEach(() => setChartPanning(false));

describe("chart pan state", () => {
	it("setChartPanning toggles isChartPanning", () => {
		expect(isChartPanning()).toBe(false);
		setChartPanning(true);
		expect(isChartPanning()).toBe(true);
		setChartPanning(false);
		expect(isChartPanning()).toBe(false);
	});
});
