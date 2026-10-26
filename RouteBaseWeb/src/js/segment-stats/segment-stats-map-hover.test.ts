import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { installFakeRaf, type FakeRaf } from "../core/raf-test-harness";
import type { SegStatsHoverMapAdapter } from "./segment-stats-map-types";
import type { SegmentStatisticsContext } from "./segment-stats-page-types";

/**
 * segment-stats-map-hover drives the map→chart proximity hover through the exported `mapHoverApi`
 * (bind / handleMouseMove / resetState). It was already reachable, but never tested. These pin the
 * three gates that keep the O(N×M) hit-test off the hot path and the one cross-cluster contract:
 *
 *  - replay-active suppression: `if (getSegmentReplayActive()) return;` — set the flag DIRECTLY via the
 *    shared slot (no replay engine imported), the way the sibling replay lane sets it.
 *  - drag suppression: the bound adapter's onDragStart pauses the loop; onDragEnd resumes it.
 *  - touch skip: on a (hover:none) device the loop never runs.
 *  - the happy path: an idle mouse over an effort GPS point marks the matching on-segment position.
 *
 * A real rAF harness (not the frame-swallowing stub) runs the queued frame so a suppressed call can be
 * distinguished from a scheduled-but-empty one. Module state is per-test (resetModules + dynamic import).
 */

// The module auto-inits against this throwaway singleton at import; each test then rebinds the api
// onto its own fake context via initSegmentStatsMapHover(ctx) — the "receive, don't import" seam.
vi.mock("./segment-statistics", () => ({ ctx: {} }));

let raf: FakeRaf;

beforeEach(() => { raf = installFakeRaf(); });
afterEach(() => { raf.restore(); vi.unstubAllGlobals(); vi.resetModules(); });

// A linear projection: x = lng·1000, y = -lat·1000. A GPS point AT the mouse projects to distance 0,
// so it always wins the MAX_PX=30 gate. The three probe calls linearize exactly on a linear map.
function makeAdapter(): { adapter: SegStatsHoverMapAdapter; fireDragStart: () => void; fireDragEnd: () => void } {
	let dragStart = (): void => {};
	let dragEnd = (): void => {};
	return {
		adapter: {
			project: (lat: number, lng: number) => ({ x: lng * 1000, y: -lat * 1000 }),
			onDragStart: (cb: () => void) => { dragStart = cb; },
			onDragEnd: (cb: () => void) => { dragEnd = cb; },
		},
		fireDragStart: () => dragStart(),
		fireDragEnd: () => dragEnd(),
	};
}

interface Loaded {
	sharedMod: typeof import("./seg-stats-shared");
	api: import("./segment-stats-map-types").SegmentStatsMapHoverApi;
	ctx: SegmentStatisticsContext;
	marker: ReturnType<typeof vi.fn>;
}

async function load(): Promise<Loaded> {
	const sharedMod = await import("./seg-stats-shared");
	const { mapHoverApi, initSegmentStatsMapHover } = await import("./segment-stats-map-hover");

	const marker = vi.fn();
	const ctx = {
		performanceData: [{ points: [{ lat: 47, lng: 7 }] }],
		effortVisibility: new Set<number>([0]),
		showSegmentPositionMarker: marker,
		_trySetChartActive: vi.fn(),
		performanceChart: null, elevationChart: null, effortChart: null, progressChart: null,
		onChartLeave: vi.fn(),
	} as unknown as SegmentStatisticsContext;
	// Rebind every api method + the hit-test onto this fake context — a plain object passed in, no
	// vi.mock of the singleton's fields. This is the payoff of the receive-don't-import conversion.
	initSegmentStatsMapHover(ctx);

	// One effort, one GPS point at the mouse. efforts:[] → the eff branch is skipped (whole-track
	// fraction fallback), so the test needs no mapDataApi split geometry.
	sharedMod.registerMapShared({
		segmentCoords: [[47, 7]],
		efforts: [],
		effortGpsArrays: [[{ lat: 47, lng: 7 } as never]],
		shapes: [],
		getActiveEngine: () => ({ engine: "", styleUrl: null }),
	});
	return { sharedMod, api: mapHoverApi, ctx, marker };
}

describe("segment-stats-map-hover — hit-test gating", () => {
	it("marks the on-segment position for an idle mouse over an effort point", async () => {
		const { api, marker, sharedMod } = await load();
		sharedMod.setSegmentReplayActive(false);
		const a = makeAdapter();
		api.bind(a.adapter);

		api.handleMouseMove({ lng: 7, lat: 47 });
		expect(raf.pending()).toBe(1);   // a frame was scheduled
		raf.flushFrames();

		expect(marker).toHaveBeenCalledTimes(1);
		expect(marker).toHaveBeenCalledWith(47, 7);
	});

	it("suppresses the hit-test entirely while the replay overlay is up (no frame even scheduled)", async () => {
		const { api, marker, sharedMod } = await load();
		sharedMod.setSegmentReplayActive(true);
		api.bind(makeAdapter().adapter);

		api.handleMouseMove({ lng: 7, lat: 47 });
		expect(raf.pending()).toBe(0);   // returned before scheduling
		raf.flushFrames();
		expect(marker).not.toHaveBeenCalled();
	});

	it("pauses on drag start and resumes on drag end", async () => {
		const { api, marker, sharedMod } = await load();
		sharedMod.setSegmentReplayActive(false);
		const a = makeAdapter();
		api.bind(a.adapter);

		a.fireDragStart();
		api.handleMouseMove({ lng: 7, lat: 47 });
		raf.flushFrames();
		expect(marker).not.toHaveBeenCalled();

		a.fireDragEnd();
		api.handleMouseMove({ lng: 7, lat: 47 });
		raf.flushFrames();
		expect(marker).toHaveBeenCalledWith(47, 7);
	});

	it("skips the heavy hit-test on a touch (hover:none) device", async () => {
		vi.stubGlobal("matchMedia", () => ({ matches: true }));
		const { api, marker, sharedMod } = await load();
		sharedMod.setSegmentReplayActive(false);
		api.bind(makeAdapter().adapter);

		api.handleMouseMove({ lng: 7, lat: 47 });
		expect(raf.pending()).toBe(0);
		raf.flushFrames();
		expect(marker).not.toHaveBeenCalled();
	});

	it("does nothing before an adapter is bound", async () => {
		const { api, marker, sharedMod } = await load();
		sharedMod.setSegmentReplayActive(false);
		api.handleMouseMove({ lng: 7, lat: 47 });
		expect(raf.pending()).toBe(0);
		raf.flushFrames();
		expect(marker).not.toHaveBeenCalled();
	});
});
