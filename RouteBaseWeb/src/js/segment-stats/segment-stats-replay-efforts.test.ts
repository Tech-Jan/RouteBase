// @vitest-environment node
import { describe, it, expect, beforeEach, vi } from "vitest";

/**
 * The multi-effort replay state machine (build → advance/seek → interpolate → progress) was 374 lines
 * with zero tests: `replayEffortsApi` is exported, but every effort of position, sync, boundary and
 * interpolation logic here had only ever been verified by watching markers race in a browser. These
 * tests drive the exported api directly with a fake markers sink, pinning the feature's whole premise —
 * TIME-synchronised advance (faster efforts pull ahead), PERCENT-synchronised advance (same %, gaps read
 * as time), seek == incremental advance, and the boundary/interpolation corners.
 *
 * ctx is only read by the module's import-time guard (`if (!ctx) throw`); build() takes effortColors as a
 * parameter, so the mock just has to be truthy. Mirrors segment-stats-charts.test.ts's ctx mock.
 */

vi.mock("./segment-statistics", () => ({ ctx: { config: {}, effortColors: ["#aa0000", "#00aa00", "#0000aa"] } }));

import { replayEffortsApi as E } from "./segment-stats-replay-efforts";
import type { EffortPerformanceData, EffortDataPoint } from "./segment-stats-effort-types";
import type { SegmentStatsReplayMarkersApi } from "./segment-stats-replay-types";
import { must } from '../core/invariant';

const COLORS = ["#aa0000", "#00aa00", "#0000aa"];

/** A markers sink that records the last position + finished flag the state machine pushed per index. */
function fakeMarkers() {
	const positions: Record<number, { lat: number; lng: number }> = {};
	const finished: Record<number, boolean> = {};
	let flushes = 0;
	const M = {
		bind() {}, create() {}, rebind() {},
		setMarkerPosition(idx: number, lat: number, lng: number) { positions[idx] = { lat, lng }; },
		setMarkerFinished(idx: number, f: boolean) { finished[idx] = f; },
		setMarkerHighlight() {},
		flushMarkerGeoSource() { flushes++; },
		resetMarkerFeatures() {},
		destroyAll() {},
		getDiagnosticSnapshot() { return { markerFeatureCount: 0, markerDirty: false, mlMarkerSources: 0, mlMarkerLayers: 0 }; },
	} as SegmentStatsReplayMarkersApi;
	return { M, positions, finished, flushes: () => flushes };
}

/** A GPS point; lng is used as the "position along the track" the assertions read. */
function pt(o: Partial<EffortDataPoint> & { time: number }): EffortDataPoint {
	return { distance: 0, speed: 0, altitude: 0, lat: 0, lng: 0, ...o };
}

function effort(id: number, elapsed: number, points: EffortDataPoint[]): EffortPerformanceData {
	return { effortId: id, activityId: id, elapsedTime: elapsed, points };
}

// A two-point effort that travels lng 0→100 over 0→`elapsed` seconds (distance mirrors lng).
function ramp(id: number, elapsed: number): EffortPerformanceData {
	return effort(id, elapsed, [pt({ time: 0, lng: 0, distance: 0 }), pt({ time: elapsed, lng: 100, distance: 100 })]);
}

beforeEach(() => { E.destroy(); });

describe("replayEffortsApi.build", () => {
	it("skips efforts with <2 points but still counts their elapsedTime toward maxTime", () => {
		const twoPt = ramp(1, 10);
		const onePt = effort(2, 50, [pt({ time: 0, lng: 0 })]); // single point → not renderable
		E.build([twoPt, onePt], [0, 1], COLORS);

		expect(E.getEfforts()).toHaveLength(1); // one-point effort excluded from the render list
		expect(must(E.getEfforts()[0], 'effort 0').sourceIndex).toBe(0);
		expect(E.getMaxTime()).toBe(50); // …but its 50 s still spans the timeline (PS-03 lazy replay)
	});

	it("colours each effort by sourceIndex % palette length and seeds progress from distance", () => {
		E.build([ramp(1, 10), ramp(2, 10)], [0, 1], COLORS);
		const efforts = E.getEfforts();
		expect(must(efforts[0], 'effort 0').color).toBe(COLORS[0]);
		expect(must(efforts[1], 'effort 1').color).toBe(COLORS[1]);
		// distance 0→100 with no segmentProgress → distance-fraction progress 0→100.
		expect(must(efforts[0], 'effort 0').progress).toEqual([0, 100]);
	});

	it("RR-01: a zero-net-distance effort falls back to a monotonic index ramp (never all-zero)", () => {
		const degen = effort(1, 10, [
			pt({ time: 0, distance: 0 }), pt({ time: 5, distance: 0 }), pt({ time: 10, distance: 0 }),
		]);
		E.build([degen], [0], COLORS);
		// totalDist === 0 would pin every progress to 0 (→ "finished at frame 0" in percent mode); the
		// index-ramp fallback keeps it advancing.
		expect(must(E.getEfforts()[0], 'effort 0').progress).toEqual([0, 50, 100]);
	});

	it("an empty visible set builds nothing and advance is a vacuous all-finished no-op", () => {
		E.build([], [], COLORS);
		expect(E.getEfforts()).toHaveLength(0);
		expect(E.getMaxTime()).toBe(0);
		const { M } = fakeMarkers();
		expect(E.advance(5, M, null)).toEqual({ allFinished: true, activeCount: 0 });
	});
});

describe("replayEffortsApi — TIME sync (the feature's premise)", () => {
	it("at one wall-clock instant a faster effort sits further along than a slower one", () => {
		// Both cover the same 100 m, fast in 10 s, slow in 20 s.
		E.build([ramp(1, 10), ramp(2, 20)], [0, 1], COLORS);
		const { M, positions } = fakeMarkers();

		E.updateAllMarkers(5, M); // t = 5 s
		expect(must(positions[0], 'marker 0').lng).toBeCloseTo(50); // fast: half its distance
		expect(must(positions[1], 'marker 1').lng).toBeCloseTo(25); // slow: a quarter
		expect(must(positions[0], 'marker 0').lng).toBeGreaterThan(must(positions[1], 'marker 1').lng); // faster pulls ahead
	});

	it("interpolates between samples and lands EXACTLY on a sample when the clock hits it", () => {
		const e = effort(1, 10, [
			pt({ time: 0, lng: 0 }), pt({ time: 5, lng: 50 }), pt({ time: 10, lng: 150 }),
		]);
		E.build([e], [0], COLORS);
		const { M, positions } = fakeMarkers();

		E.updateAllMarkers(2.5, M);
		expect(must(positions[0], 'marker 0').lng).toBeCloseTo(25); // half-way p0→p1

		E.updateAllMarkers(5, M);
		expect(must(positions[0], 'marker 0').lng).toBe(50); // exact sample hit → no interpolation drift

		E.updateAllMarkers(7.5, M);
		expect(must(positions[0], 'marker 0').lng).toBeCloseTo(100); // half-way p1→p2 (50→150)
	});
});

describe("replayEffortsApi — PERCENT sync", () => {
	it("holds every effort at the same completion % while their real times diverge (sidebar gap)", () => {
		const withProg = (id: number, midTime: number, endTime: number): EffortPerformanceData =>
			effort(id, endTime, [
				pt({ time: 0, lng: 0, segmentProgress: 0 }),
				pt({ time: midTime, lng: 50, segmentProgress: 0.5 }),
				pt({ time: endTime, lng: 100, segmentProgress: 1 }),
			]);
		E.build([withProg(1, 5, 10), withProg(2, 10, 20)], [0, 1], COLORS);
		E.setMode("percent");

		const { M, positions } = fakeMarkers();
		E.updateAllMarkers(10, M); // maxTime 20 → target 50 %

		expect(must(positions[0], 'marker 0').lng).toBeCloseTo(50); // both at their own 50 % point
		expect(must(positions[1], 'marker 1').lng).toBeCloseTo(50);
		// …but the effort that reached 50 % faster reports the smaller elapsed time.
		expect(E.getDataAtTime(0, 10)!.time).toBeCloseTo(5);
		expect(E.getDataAtTime(1, 10)!.time).toBeCloseTo(10);
	});
});

describe("replayEffortsApi — seek == incremental advance", () => {
	it("a direct seek lands the marker where stepwise advance would (mid-effort)", () => {
		const e = effort(1, 10, [
			pt({ time: 0, lng: 0 }), pt({ time: 5, lng: 50 }), pt({ time: 10, lng: 150 }),
		]);

		E.build([e], [0], COLORS);
		const inc = fakeMarkers();
		E.advance(2, inc.M, null); E.advance(5, inc.M, null); E.advance(8, inc.M, null);

		E.build([e], [0], COLORS);
		const sk = fakeMarkers();
		E.seekReset(8, sk.M); E.updateAllMarkers(8, sk.M);

		expect(must(sk.positions[0], 'marker 0').lng).toBeCloseTo(must(inc.positions[0], 'marker 0').lng);
		expect(sk.finished[0] ?? false).toBe(inc.finished[0] ?? false);
	});

	it("seeking past an effort's end matches advancing past it — finished, snapped to the last point", () => {
		E.build([ramp(1, 10), ramp(2, 20)], [0, 1], COLORS);
		const inc = fakeMarkers();
		E.advance(12, inc.M, null); // fast finished, slow still moving

		E.build([ramp(1, 10), ramp(2, 20)], [0, 1], COLORS);
		const sk = fakeMarkers();
		E.seekReset(12, sk.M); E.updateAllMarkers(12, sk.M);

		expect(sk.finished[0]).toBe(true);
		expect(inc.finished[0]).toBe(true);
		expect(must(sk.positions[0], 'marker 0').lng).toBe(100); // snapped to last point, no overrun
		expect(must(sk.positions[1], 'marker 1').lng).toBeCloseTo(must(inc.positions[1], 'marker 1').lng); // unfinished effort agrees
	});
});

describe("replayEffortsApi — boundaries", () => {
	it("t = 0: every marker sits at its start point and nothing is finished", () => {
		E.build([ramp(1, 10), ramp(2, 20)], [0, 1], COLORS);
		const { M, positions, finished } = fakeMarkers();
		E.updateAllMarkers(0, M);
		expect(must(positions[0], 'marker 0').lng).toBe(0);
		expect(must(positions[1], 'marker 1').lng).toBe(0);
		expect(finished[0] ?? false).toBe(false);
		expect(E.getEffortStates().every((s) => !s.finished)).toBe(true);
	});

	it("t past the longest effort: all finished, snapped to their ends, no overrun", () => {
		E.build([ramp(1, 10), ramp(2, 20)], [0, 1], COLORS);
		const { M, positions } = fakeMarkers();
		const { allFinished } = E.advance(1000, M, null);
		expect(allFinished).toBe(true);
		expect(must(positions[0], 'marker 0').lng).toBe(100); // exactly the last point, not beyond
		expect(must(positions[1], 'marker 1').lng).toBe(100);
		expect(E.getEffortStates().every((s) => s.finished)).toBe(true);
	});
});

describe("replayEffortsApi — advance callbacks + queries", () => {
	it("fires onEffortFinish once per effort with a 1-based, chronological finish order", () => {
		E.build([ramp(1, 10), ramp(2, 20)], [0, 1], COLORS);
		const { M } = fakeMarkers();
		const fired: Array<{ idx: number; order: number; time: number }> = [];
		const cb = (idx: number, order: number, time: number) => fired.push({ idx, order, time });

		E.advance(10, M, cb); // fast crosses its finish line
		E.advance(20, M, cb); // slow crosses its finish line
		E.advance(30, M, cb); // both already finished → no repeat fire

		expect(fired).toEqual([
			{ idx: 0, order: 1, time: 10 },
			{ idx: 1, order: 2, time: 20 },
		]);
	});

	it("getDataAtTime interpolates distance/speed/altitude/progress, and pins finished to progress 100", () => {
		const e = effort(1, 10, [
			pt({ time: 0, distance: 0, speed: 10, altitude: 100 }),
			pt({ time: 10, distance: 100, speed: 20, altitude: 200 }),
		]);
		E.build([e], [0], COLORS);

		const mid = E.getDataAtTime(0, 5)!;
		expect(mid.distance).toBeCloseTo(50);
		expect(mid.speed).toBeCloseTo(15);
		expect(mid.altitude).toBeCloseTo(150);
		expect(mid.progress).toBeCloseTo(50);
		expect(mid.time).toBeCloseTo(5); // time mode → the clock itself

		const { M } = fakeMarkers();
		E.updateAllMarkers(1000, M); // drive it to finished
		const done = E.getDataAtTime(0, 1000)!;
		expect(done.progress).toBe(100);
		expect(done.time).toBe(10); // the effort's real elapsed time
	});

	it("getDataAtTime returns null for an out-of-range effort index", () => {
		E.build([ramp(1, 10)], [0], COLORS);
		expect(E.getDataAtTime(99, 5)).toBeNull();
	});
});

describe("replayEffortsApi — lifecycle", () => {
	it("reset rewinds every effort to the start; destroy clears the model", () => {
		E.build([ramp(1, 10), ramp(2, 20)], [0, 1], COLORS);
		const { M } = fakeMarkers();
		E.advance(1000, M, null); // finish everything
		expect(E.getEffortStates().every((s) => s.finished)).toBe(true);

		E.reset();
		expect(E.getEffortStates().every((s) => !s.finished && s.currentIndex === 0)).toBe(true);

		E.destroy();
		expect(E.getEfforts()).toHaveLength(0);
		expect(E.getMaxTime()).toBe(0);
	});
});
