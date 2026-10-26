// @vitest-environment node
import { describe, it, expect, vi } from "vitest";
import { chartAreaGradient, setLineSelection } from "./chart-line-select";
import { must } from './invariant';

// Capture the colour stops a gradient receives so we can assert the parsed rgba.
function fakeGradientCtx() {
	const stops: [number, string][] = [];
	const gradient = { addColorStop: (o: number, c: string) => { stops.push([o, c]); } };
	const chart = {
		chartArea: { top: 0, bottom: 100, left: 0, right: 200 },
		ctx: { createLinearGradient: () => gradient },
	};
	return { chart, stops };
}

describe("chartAreaGradient", () => {
	it("parses a hex colour into rgba stops", () => {
		const { chart, stops } = fakeGradientCtx();
		chartAreaGradient("#ff8000", 0.5)({ chart } as never);
		expect(must(stops[0], 'top stop')[1]).toBe("rgba(255,128,0,0.5)"); // top stop at topAlpha
		expect(must(stops[1], 'bottom stop')[1]).toBe("rgba(255,128,0,0)");   // bottom stop transparent
	});

	it("parses an rgb()/rgba() colour into rgba stops", () => {
		const { chart, stops } = fakeGradientCtx();
		chartAreaGradient("rgba(10, 20, 30, 0.9)", 0.4)({ chart } as never);
		expect(must(stops[0], 'top stop')[1]).toBe("rgba(10,20,30,0.4)");
		expect(must(stops[1], 'bottom stop')[1]).toBe("rgba(10,20,30,0)");
	});
});

describe("setLineSelection", () => {
	function fakeChart() {
		const update = vi.fn();
		const datasets = [
			{ borderColor: "#ff0000", borderWidth: 2, backgroundColor: "x", fill: false },
			{ borderColor: "#00ff00", borderWidth: 2, backgroundColor: "y", fill: false },
		];
		return { chart: { data: { datasets }, update } as never, datasets, update };
	}

	it("fills the selected dataset and thickens it, then restores base width on clear", () => {
		const { chart, datasets, update } = fakeChart();
		const d0 = must(datasets[0], 'dataset 0'), d1 = must(datasets[1], 'dataset 1');
		setLineSelection(chart, 0);
		expect(d0.fill).toBe("origin");
		expect(d0.borderWidth).toBe(3.5); // base 2 + 1.5
		expect(d1.fill).toBe(false);
		expect(d1.borderWidth).toBe(2);

		setLineSelection(chart, null);
		expect(d0.fill).toBe(false);
		expect(d0.borderWidth).toBe(2); // restored from _baseWidth
		expect(update).toHaveBeenCalledWith("none");
	});
});
