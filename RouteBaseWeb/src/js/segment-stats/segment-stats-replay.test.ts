import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

/**
 * The multi-effort replay orchestrator (init/start/pause/resume/seek/destroy over an rAF loop) was
 * 466 lines, browser-only. `replayApi` is exported; this drives it end-to-end against the real efforts/
 * tracks/markers modules + the real playback shell, on core/fake-map-harness, with:
 *
 *  - core/raf-test-harness.installFakeRaf, NOT `stubGlobal('requestAnimationFrame', () => 1)` — the inert
 *    stub swallows every frame, so a loop test would pass while asserting nothing. The "anti-inert" case
 *    below proves the playhead actually moves when a frame fires.
 *  - a hand-driven performance.now clock so the per-frame dt is deterministic (the shell reads
 *    performance.now itself; the harness's own frame time is ignored).
 *
 * These pin control flow + TEARDOWN, the RS2-class leak this file's own comments warn about: destroy()
 * must leave no rAF centring a nulled map. ctx supplies effortColors (E.build) + segmentCoords (fitBounds/
 * background); it is only otherwise touched by the import-time guard.
 */

vi.mock("./segment-statistics", () => ({
	ctx: { config: { segmentCoords: [[47, 7], [47.1, 7.1]] }, effortColors: ["#aa0000", "#00aa00"] },
}));

import { replayApi } from "./segment-stats-replay";
import { getSegmentReplayActive } from "./seg-stats-shared";
import type { EffortPerformanceData, EffortDataPoint } from "./segment-stats-effort-types";
import { createFakeMap } from "../core/fake-map-harness";
import { installFakeRaf, type FakeRaf } from "../core/raf-test-harness";

function pt(o: Partial<EffortDataPoint> & { time: number }): EffortDataPoint {
	return { distance: 0, speed: 0, altitude: 0, lat: 0, lng: 0, ...o };
}
function ramp(id: number, elapsed: number): EffortPerformanceData {
	return {
		effortId: id, activityId: id, elapsedTime: elapsed,
		points: [pt({ time: 0, lng: 0, distance: 0 }), pt({ time: elapsed, lng: 100, distance: 100 })],
	};
}
// fast finishes at 10 s, slow at 20 s → maxTime 20.
const PERF = [ramp(1, 10), ramp(2, 20)];

let fake: ReturnType<typeof createFakeMap>;
let raf: FakeRaf;
let clock: number;

beforeEach(() => {
	fake = createFakeMap();
	clock = 1000; // positive so the shell's `lastFrameTime ? …` guard reads a real value
	vi.spyOn(performance, "now").mockImplementation(() => clock);
	raf = installFakeRaf();
});

afterEach(() => {
	replayApi.destroy(); // safe to call repeatedly; also proves teardown is idempotent between tests
	raf.restore();
	vi.restoreAllMocks();
});

describe("replayApi.init", () => {
	it("builds the efforts and reports the timeline span", () => {
		expect(replayApi.init(fake.map, PERF, [0, 1])).toBe(true);
		expect(replayApi.getEfforts()).toHaveLength(2);
		expect(replayApi.getMaxTime()).toBe(20);
		expect(replayApi.isPlaying()).toBe(false);
	});

	it("returns false when no effort is renderable (nothing to replay)", () => {
		expect(replayApi.init(fake.map, [], [])).toBe(false);
		// a lone single-point effort is not renderable either
		const onePt: EffortPerformanceData = { effortId: 9, activityId: 9, elapsedTime: 5, points: [pt({ time: 0 })] };
		expect(replayApi.init(fake.map, [onePt], [0])).toBe(false);
	});
});

describe("replayApi — play / pause / resume", () => {
	beforeEach(() => { replayApi.init(fake.map, PERF, [0, 1]); });

	it("start flips the shared active flag and schedules exactly one frame", () => {
		replayApi.start();
		expect(replayApi.isPlaying()).toBe(true);
		expect(getSegmentReplayActive()).toBe(true); // map-hover reads this to suppress hit-testing
		expect(raf.pending()).toBe(1);
	});

	it("anti-inert: a fired frame actually advances the playhead (a swallowed frame would not)", () => {
		replayApi.start();
		expect(replayApi.getCurrentTime()).toBe(0);

		clock = 1100; // +100 ms
		raf.flushFrames(1);
		const t1 = replayApi.getCurrentTime();
		expect(t1).toBeGreaterThan(0); // the callback RAN — an inert `() => 1` stub leaves this at 0

		clock = 1200; // +100 ms more
		raf.flushFrames(1);
		expect(replayApi.getCurrentTime()).toBeGreaterThan(t1); // and keeps advancing
		expect(raf.pending()).toBe(1); // re-queues itself while playing
	});

	it("pause stops the frame with nothing left pending (no idle loop when not following)", () => {
		replayApi.start();
		clock = 1100; raf.flushFrames(1);
		replayApi.pause();
		expect(replayApi.isPlaying()).toBe(false);
		expect(raf.pending()).toBe(0);
	});

	it("resume re-schedules the loop; togglePlayPause returns the new playing state", () => {
		replayApi.start();
		replayApi.pause();
		replayApi.resume();
		expect(replayApi.isPlaying()).toBe(true);
		expect(raf.pending()).toBe(1);

		expect(replayApi.togglePlayPause()).toBe(false); // → paused
		expect(replayApi.togglePlayPause()).toBe(true);  // → playing
	});

	it("resume after completion restarts from 0 instead of re-completing (RS2-04)", () => {
		replayApi.seek(100);            // playhead at the end; seekReset marks every effort finished
		expect(replayApi.getEffortStates().every((s) => s.finished)).toBe(true);

		replayApi.resume();             // isAtEnd → restart() rewinds to 0
		expect(replayApi.isPlaying()).toBe(true);
		expect(replayApi.getCurrentTime()).toBe(0);
	});
});

describe("replayApi — seek + speed", () => {
	beforeEach(() => { replayApi.init(fake.map, PERF, [0, 1]); });

	it("seek maps a percentage onto the [0, maxTime] playhead and progress", () => {
		replayApi.seek(50);
		expect(replayApi.getCurrentTime()).toBeCloseTo(10); // 50% of 20 s
		expect(replayApi.getProgressPct()).toBeCloseTo(50);
	});

	it("setSpeed clamps to the shell's [0.1, 1000] band", () => {
		replayApi.setSpeed(5000);
		expect(replayApi.getSpeed()).toBe(1000);
		replayApi.setSpeed(0.0001);
		expect(replayApi.getSpeed()).toBe(0.1);
	});
});

describe("replayApi.destroy — teardown leaves no running rAF", () => {
	it("cancels the loop and clears the active flag (RS2 leak guard)", () => {
		replayApi.init(fake.map, PERF, [0, 1]);
		replayApi.start();
		clock = 1100; raf.flushFrames(1);
		expect(getSegmentReplayActive()).toBe(true);
		expect(raf.pending()).toBe(1);

		replayApi.destroy();
		expect(replayApi.isPlaying()).toBe(false);
		expect(raf.pending()).toBe(0); // no frame left centring a nulled map
		expect(getSegmentReplayActive()).toBe(false);
	});
});
