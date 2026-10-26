// @vitest-environment node
import type { SegmentStatisticsContext } from "./segment-stats-page-types";
import { describe, it, expect, beforeEach } from "vitest";
import { makeElevBackgroundPlugin, makeDensityPlugin } from "./segment-stats-charts-plugins";

// the backdrop plugins now RECEIVE ctx as a dep and read it live per draw. This drops the
// vi.mock("./segment-statistics") the suite used to install — a plain fake ctx is passed in and mutated
// directly. Pins the elevation backdrop (altY at :80-93) and the density blit.
const ctx = {
	selectedEffortIndex: null as number | null,
	performanceData: null as unknown,
	aggregateMode: false,
} as unknown as SegmentStatisticsContext;

interface Rec { fills: number; strokes: number; images: number; clips: number; }

function recordingChart(rec: Rec) {
	const ctx2d = {
		save() {}, restore() {}, beginPath() {}, rect() {}, closePath() {},
		moveTo() {}, lineTo() {},
		clip() { rec.clips++; },
		fill() { rec.fills++; },
		stroke() { rec.strokes++; },
		drawImage() { rec.images++; },
		fillStyle: "", strokeStyle: "", lineWidth: 1, imageSmoothingEnabled: false,
	};
	return {
		ctx: ctx2d,
		chartArea: { top: 0, bottom: 200, left: 0, right: 400 },
		scales: {
			x: { min: 0, max: 1000, getPixelForValue: (v: number) => v / 2.5 },
			y: { getPixelForValue: (v: number) => 200 - v },
		},
	} as unknown as InstanceType<typeof Chart>;
}

describe("makeElevBackgroundPlugin", () => {
	beforeEach(() => {
		ctx.selectedEffortIndex = null;
		ctx.performanceData = null as never;
	});

	const plugin = makeElevBackgroundPlugin({ ctx, effortPointX: (p) => p.distance });

	it("draws nothing when no effort is highlighted", () => {
		const rec: Rec = { fills: 0, strokes: 0, images: 0, clips: 0 };
		plugin.beforeDatasetsDraw(recordingChart(rec));
		expect(rec.fills).toBe(0);
		expect(rec.strokes).toBe(0);
	});

	it("clips to the plot and draws the filled profile + top edge of the selected effort", () => {
		ctx.selectedEffortIndex = 0;
		ctx.performanceData = [{
			points: [
				{ altitude: 0, distance: 0 },
				{ altitude: 50, distance: 500 },
				{ altitude: 100, distance: 1000 },
			],
		}] as never;
		const rec: Rec = { fills: 0, strokes: 0, images: 0, clips: 0 };
		plugin.beforeDatasetsDraw(recordingChart(rec));
		expect(rec.clips).toBe(1);
		expect(rec.fills).toBe(1);   // the filled area down to the baseline
		expect(rec.strokes).toBe(1); // the faint top edge
	});
});

describe("makeDensityPlugin", () => {
	beforeEach(() => { ctx.aggregateMode = false; });

	const images = [{ canvas: { width: 100, height: 240 }, xMin: 0, xMax: 1000, yMin: 0, yMax: 100 }];
	const aggregate = { getDensityImages: () => images } as never;

	it("blits nothing outside aggregate mode", () => {
		const rec: Rec = { fills: 0, strokes: 0, images: 0, clips: 0 };
		makeDensityPlugin("speed", { aggregate, ctx }).beforeDatasetsDraw(recordingChart(rec));
		expect(rec.images).toBe(0);
	});

	it("clips to the plot and blits each region bitmap in aggregate mode", () => {
		ctx.aggregateMode = true;
		const rec: Rec = { fills: 0, strokes: 0, images: 0, clips: 0 };
		makeDensityPlugin("speed", { aggregate, ctx }).beforeDatasetsDraw(recordingChart(rec));
		expect(rec.clips).toBe(1);
		expect(rec.images).toBe(1);
	});
});
