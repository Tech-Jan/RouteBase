import { describe, it, expect } from "vitest";
import { crosshairPlugin } from "./statistics-hover";

// The cumulative crosshair now draws through the shared strokeCrosshairLine kernel (item 2), styled
// as the dashed grey guide, plus a ring dot on each visible series. These pin that render.

interface Rec { strokes: number; arcs: number; fills: number; dashes: number[][]; }

function recordingChart(active: Array<{ datasetIndex: number; element: { x: number; y: number } }>) {
	const rec: Rec = { strokes: 0, arcs: 0, fills: 0, dashes: [] };
	const ctx = {
		save() {}, restore() {}, beginPath() {}, moveTo() {}, lineTo() {},
		stroke() { rec.strokes++; },
		arc() { rec.arcs++; },
		fill() { rec.fills++; },
		setLineDash(d: number[]) { rec.dashes.push(d); },
		strokeStyle: "", fillStyle: "", lineWidth: 1,
	};
	const chart = {
		ctx,
		chartArea: { top: 0, bottom: 200, left: 0, right: 400 },
		tooltip: { getActiveElements: () => active },
		data: { datasets: [{ borderColor: "#ff0000" }, { borderColor: "#00ff00" }] },
	};
	return { chart, rec };
}

// The hook only reads `chart`; call it through a loose type so the test needn't satisfy Chart.js'
// full generic-Chart signature or its exact hook arity.
const drawCrosshair = crosshairPlugin.afterDatasetsDraw as unknown as (c: unknown) => void;

describe("crosshairPlugin", () => {
	it("draws nothing when no series is active", () => {
		const { chart, rec } = recordingChart([]);
		drawCrosshair(chart);
		expect(rec.strokes).toBe(0);
	});

	it("draws a dashed guide plus one ring dot per finite series, skipping null gaps", () => {
		const { chart, rec } = recordingChart([
			{ datasetIndex: 0, element: { x: 100, y: 50 } },   // finite → dot
			{ datasetIndex: 1, element: { x: 100, y: NaN } },  // null gap → no dot
		]);
		drawCrosshair(chart);

		expect(rec.dashes).toContainEqual([3, 3]); // the guide is dashed (shared-kernel option)
		expect(rec.arcs).toBe(1);                  // exactly one dot (the null-gap series is skipped)
		expect(rec.fills).toBe(1);
		expect(rec.strokes).toBe(2);               // the guide + the one dot's ring
	});
});
