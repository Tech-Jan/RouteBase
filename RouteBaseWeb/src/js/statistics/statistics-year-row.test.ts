import { describe, it, expect } from "vitest";
import { must } from '../core/invariant';
import { yearRowPlugin } from "./statistics-year-row";

// The year tier draws a boundary tick at each Jan 1 inside the view and centres the year label
// within its visible span. These pin tick placement + label set (the untested :47-73 block).

interface Rec { strokes: number; labels: Array<{ text: string; x: number }>; }

function recordingChart(min: number, max: number) {
	const rec: Rec = { strokes: 0, labels: [] };
	const left = 0, right = 1000;
	const getPixelForValue = (v: number) => ((v - min) / (max - min)) * (right - left) + left;
	const ctx = {
		save() {}, restore() {}, beginPath() {}, moveTo() {}, lineTo() {},
		stroke() { rec.strokes++; },
		fillText(text: string, x: number) { rec.labels.push({ text, x }); },
		strokeStyle: "", fillStyle: "", font: "", textBaseline: "", textAlign: "", lineWidth: 1,
	};
	const chart = {
		ctx,
		chartArea: { left, right, top: 0, bottom: 200 },
		scales: { x: { min, max, bottom: 180, getPixelForValue } },
	};
	return { chart, rec };
}

// The plugin hook only reads `chart`; call it through a loose type so the test doesn't have to
// satisfy Chart.js' full generic-Chart signature.
const drawYearRow = yearRowPlugin.afterDraw as unknown as (c: unknown, a: unknown, o: unknown) => void;

describe("yearRowPlugin", () => {
	it("no-ops unless enabled", () => {
		const { chart, rec } = recordingChart(Date.UTC(2020, 0, 1), Date.UTC(2022, 0, 1));
		drawYearRow(chart, {}, { show: false });
		expect(rec.strokes).toBe(0);
		expect(rec.labels).toHaveLength(0);
	});

	it("ticks each interior Jan 1 and labels every visible year", () => {
		// View: 2020-07-01 .. 2022-07-01. Interior boundaries: 2021-01-01, 2022-01-01 (2020-01-01 is
		// before the view). Visible years: 2020, 2021, 2022.
		const { chart, rec } = recordingChart(Date.UTC(2020, 6, 1), Date.UTC(2022, 6, 1));
		drawYearRow(chart, {}, { show: true });

		expect(rec.strokes).toBe(2); // 2021 + 2022 boundary ticks (not 2020 — off the left edge)
		expect(rec.labels.map((l) => l.text)).toEqual(["2020", "2021", "2022"]);
		// 2021 label is centred in the plot; 2020 sits left of it, 2022 right of it.
		const x2020 = must(rec.labels[0], 'label 0').x, x2021 = must(rec.labels[1], 'label 1').x, x2022 = must(rec.labels[2], 'label 2').x;
		expect(x2020).toBeLessThan(x2021);
		expect(x2021).toBeLessThan(x2022);
	});
});
