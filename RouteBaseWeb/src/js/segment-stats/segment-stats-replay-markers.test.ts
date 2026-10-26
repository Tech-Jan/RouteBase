import { describe, it, expect, beforeEach, vi } from "vitest";

/**
 * The replay marker layer (create/position/highlight/reset/teardown) was 301 lines, browser-only. Its
 * api (`replayMarkersApi`) is exported, so these tests bind it to core/fake-map-harness and assert on
 * what it hands MapLibre — the FeatureCollection coordinates and per-feature properties — rather than on
 * MapLibre internals. ctx is only touched by the import-time guard, so the mock is minimal.
 */

vi.mock("./segment-statistics", () => ({ ctx: { config: {} } }));

import { replayMarkersApi as M } from "./segment-stats-replay-markers";
import { MARKER_HALO_LAYER_ID } from "./segment-stats-replay-types";
import type { ReplayEffort } from "./segment-stats-replay-types";
import { createFakeMap } from "../core/fake-map-harness";
import { must } from '../core/invariant';

const MARKER_LAYERS = [MARKER_HALO_LAYER_ID, "replay-markers-border", "replay-markers-fill"];

/** A ReplayEffort whose only meaningful fields for markers are color + the points' first coord. */
function eff(color: string, coords: Array<[number, number]>): ReplayEffort {
	return {
		sourceIndex: 0, name: "", date: "", color, elapsedTime: 0, totalDistance: 0, progress: [],
		points: coords.map(([lng, lat]) => ({ distance: 0, time: 0, speed: 0, altitude: 0, lat, lng })),
	};
}

interface MarkerFeature { properties: { color: string; finished: boolean; highlighted: boolean }; geometry: { coordinates: [number, number] }; }
type FC = { features: MarkerFeature[] };

/** Read the FeatureCollection from the most recent setData() the module pushed to its source. */
function lastPushed(fake: ReturnType<typeof createFakeMap>): FC {
	const src = fake.map.getSource("replay-markers") as unknown as { setData: ReturnType<typeof vi.fn> };
	const calls = src.setData.mock.calls;
	return must(calls[calls.length - 1], 'at least one setData call')[0] as FC;
}

let fake: ReturnType<typeof createFakeMap>;
beforeEach(() => { fake = createFakeMap(); });

describe("replayMarkersApi.create", () => {
	it("adds the marker source and all three circle layers", () => {
		M.bind(fake.map, [eff("#f00", [[7, 47]])]);
		M.create();
		expect(fake.sources.has("replay-markers")).toBe(true);
		for (const id of MARKER_LAYERS) expect(fake.layers.has(id)).toBe(true);
	});

	it("builds one feature per effort at its start coordinate, coloured, unfinished", () => {
		M.bind(fake.map, [eff("#f00", [[7, 47], [8, 48]]), eff("#0f0", [[1, 2], [3, 4]])]);
		M.create();
		M.flushMarkerGeoSource();

		const fc = lastPushed(fake);
		expect(fc.features).toHaveLength(2);
		expect(must(fc.features[0], 'feature 0').geometry.coordinates).toEqual([7, 47]); // [lng, lat] of the first point
		expect(must(fc.features[0], 'feature 0').properties.color).toBe("#f00");
		expect(must(fc.features[1], 'feature 1').properties.color).toBe("#0f0");
		expect(fc.features.every((f) => !f.properties.finished)).toBe(true);
	});

	it("guards writes until create has run (no source ⇒ setMarkerPosition is a no-op)", () => {
		M.bind(fake.map, [eff("#f00", [[7, 47], [8, 48]])]);
		// no create()
		expect(() => M.setMarkerPosition(0, 10, 10)).not.toThrow();
		M.create();
		M.flushMarkerGeoSource();
		expect(must(lastPushed(fake).features[0], 'feature 0').geometry.coordinates).toEqual([7, 47]); // untouched
	});
});

describe("replayMarkersApi — position + highlight + reset push [lng, lat]", () => {
	beforeEach(() => {
		M.bind(fake.map, [eff("#f00", [[7, 47], [8, 48]]), eff("#0f0", [[1, 2], [3, 4]])]);
		M.create();
	});

	it("setMarkerPosition moves a marker (stored as [lng, lat]) and flush pushes it", () => {
		M.setMarkerPosition(1, 40.5, 12.25); // (lat, lng)
		M.flushMarkerGeoSource();
		expect(must(lastPushed(fake).features[1], 'feature 1').geometry.coordinates).toEqual([12.25, 40.5]);
	});

	it("setMarkerFinished flips the flag the paint expressions read", () => {
		M.setMarkerFinished(0, true);
		M.flushMarkerGeoSource();
		expect(must(lastPushed(fake).features[0], 'feature 0').properties.finished).toBe(true);
	});

	it("setMarkerHighlight flushes immediately and marks the feature highlighted", () => {
		M.setMarkerHighlight(1, true); // auto-flushes
		expect(must(lastPushed(fake).features[1], 'feature 1').properties.highlighted).toBe(true);
		M.setMarkerHighlight(1, false);
		expect(must(lastPushed(fake).features[1], 'feature 1').properties.highlighted).toBe(false);
	});

	it("resetMarkerFeatures returns markers to start, clears finished, re-applies highlights", () => {
		M.setMarkerPosition(0, 99, 99);
		M.setMarkerFinished(0, true);
		M.resetMarkerFeatures({ 1: true }); // effort 1 stays highlighted
		M.flushMarkerGeoSource();

		const fc = lastPushed(fake);
		expect(must(fc.features[0], 'feature 0').geometry.coordinates).toEqual([7, 47]); // back to start
		expect(must(fc.features[0], 'feature 0').properties.finished).toBe(false);
		expect(must(fc.features[1], 'feature 1').properties.highlighted).toBe(true);
	});
});

describe("replayMarkersApi — diagnostics + teardown", () => {
	it("getDiagnosticSnapshot counts the live source, three layers and feature total", () => {
		M.bind(fake.map, [eff("#f00", [[7, 47]]), eff("#0f0", [[1, 2]])]);
		M.create();
		const snap = M.getDiagnosticSnapshot();
		expect(snap.mlMarkerSources).toBe(1);
		expect(snap.mlMarkerLayers).toBe(3);
		expect(snap.markerFeatureCount).toBe(2);
	});

	it("destroyAll removes every marker layer and the source", () => {
		M.bind(fake.map, [eff("#f00", [[7, 47]])]);
		M.create();
		M.destroyAll();
		expect(fake.sources.has("replay-markers")).toBe(false);
		for (const id of MARKER_LAYERS) expect(fake.layers.has(id)).toBe(false);
	});
});
