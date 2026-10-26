import { describe, it, expect, beforeEach, vi } from "vitest";

/**
 * The replay track/polyline layer (segment background, effort tracks, the followed-effort track) was
 * 253 lines, browser-only. `replayTracksApi` is exported, so these tests bind it to core/fake-map-harness
 * and assert on the GeoJSON it builds. Tracks reads ctx.config.segmentCoords for the background, so the
 * mock carries a real coordinate list. Note the coord flip: segmentCoords are [lat, lng] and every layer
 * this module builds emits [lng, lat] — pinned below.
 */

// [lat, lng] pairs — inlined in the factory because vi.mock is hoisted above any top-level const.
vi.mock("./segment-statistics", () => ({ ctx: { config: { segmentCoords: [[47, 7], [47.1, 7.1], [47.2, 7.2]] } } }));

import { replayTracksApi as T } from "./segment-stats-replay-tracks";
import type { ReplayEffort } from "./segment-stats-replay-types";
import { createFakeMap } from "../core/fake-map-harness";
import { must } from '../core/invariant';

function eff(color: string, coords: Array<[number, number]>): ReplayEffort {
	return {
		sourceIndex: 0, name: "", date: "", color, elapsedTime: 0, totalDistance: 0, progress: [],
		points: coords.map(([lng, lat]) => ({ distance: 0, time: 0, speed: 0, altitude: 0, lat, lng })),
	};
}

interface LineFeature { properties: Record<string, unknown>; geometry: { coordinates: Array<[number, number]> }; }
type FC = { features: LineFeature[] };

function lastPushed(fake: ReturnType<typeof createFakeMap>, sourceId: string): FC {
	const src = fake.map.getSource(sourceId) as unknown as { setData: ReturnType<typeof vi.fn> };
	const calls = src.setData.mock.calls;
	return must(calls[calls.length - 1], 'at least one setData call')[0] as FC;
}

let fake: ReturnType<typeof createFakeMap>;
beforeEach(() => { fake = createFakeMap(); });

describe("replayTracksApi.draw", () => {
	it("adds the segment-background and effort-track sources + line layers", () => {
		T.bind(fake.map, [eff("#f00", [[7, 47], [8, 48]])]);
		T.draw();
		expect(fake.sources.has("replay-seg-bg")).toBe(true);
		expect(fake.sources.has("replay-efforts")).toBe(true);
		expect(fake.layers.has("replay-seg-bg-line")).toBe(true);
		expect(fake.layers.has("replay-efforts-line")).toBe(true);
	});

	it("emits effort tracks as [lng, lat] LineStrings carrying the effort colour + index", () => {
		T.bind(fake.map, [eff("#f00", [[7, 47], [8, 48]]), eff("#0f0", [[1, 2], [3, 4]])]);
		T.draw();
		T.updateEffortTracks(); // setData path — inspectable via the fake source

		const fc = lastPushed(fake, "replay-efforts");
		expect(fc.features).toHaveLength(2);
		expect(must(fc.features[0], 'feature 0').geometry.coordinates).toEqual([[7, 47], [8, 48]]);
		expect(must(fc.features[0], 'feature 0').properties).toMatchObject({ color: "#f00", _idx: 0 });
		expect(must(fc.features[1], 'feature 1').properties).toMatchObject({ color: "#0f0", _idx: 1 });
	});
});

describe("replayTracksApi — followed-effort track (heatmap-view replay)", () => {
	beforeEach(() => {
		T.bind(fake.map, [eff("#f00", [[7, 47], [8, 48], [9, 49]])]);
	});

	it("showEffortTrack adds the follow source with a casing + line layer; hide removes them", () => {
		T.showEffortTrack(0);
		expect(fake.sources.has("replay-follow-track")).toBe(true);
		expect(fake.layers.has("replay-follow-track-casing")).toBe(true);
		expect(fake.layers.has("replay-follow-track-line")).toBe(true);

		T.hideEffortTrack();
		expect(fake.sources.has("replay-follow-track")).toBe(false);
		expect(fake.layers.has("replay-follow-track-line")).toBe(false);
	});

	it("showEffortTrack replaces a previously followed track rather than stacking", () => {
		T.showEffortTrack(0);
		T.showEffortTrack(0); // hideEffortTrack() runs first internally → still exactly one
		expect(fake.sources.has("replay-follow-track")).toBe(true);
		T.hideEffortTrack();
		expect(fake.sources.has("replay-follow-track")).toBe(false);
	});
});

describe("replayTracksApi — background visibility + diagnostics + teardown", () => {
	it("setSegmentBackgroundVisible toggles the background line's opacity", () => {
		const setPaint = vi.fn();
		(fake.map as unknown as { setPaintProperty: unknown }).setPaintProperty = setPaint;
		T.bind(fake.map, [eff("#f00", [[7, 47], [8, 48]])]);
		T.draw();

		T.setSegmentBackgroundVisible(false);
		expect(setPaint).toHaveBeenCalledWith("replay-seg-bg-line", "line-opacity", 0);
		T.setSegmentBackgroundVisible(true);
		expect(setPaint).toHaveBeenCalledWith("replay-seg-bg-line", "line-opacity", 0.3);
	});

	it("getDiagnosticSnapshot counts the two live sources and two line layers", () => {
		T.bind(fake.map, [eff("#f00", [[7, 47], [8, 48]])]);
		T.draw();
		const snap = T.getDiagnosticSnapshot();
		expect(snap.mlTrackSources).toBe(2);
		expect(snap.mlTrackLayers).toBe(2);
	});

	it("destroyAll removes every track + follow layer and source", () => {
		T.bind(fake.map, [eff("#f00", [[7, 47], [8, 48]])]);
		T.draw();
		T.showEffortTrack(0);
		T.destroyAll();
		for (const s of ["replay-seg-bg", "replay-efforts", "replay-follow-track"]) {
			expect(fake.sources.has(s)).toBe(false);
		}
		expect(fake.layers.has("replay-efforts-line")).toBe(false);
	});
});
