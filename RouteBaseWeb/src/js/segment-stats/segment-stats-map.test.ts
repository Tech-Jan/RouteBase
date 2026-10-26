import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import type { SegmentStatisticsContext } from "./segment-stats-page-types";

/**
 * The segment map orchestrator (segment-stats-map.ts) was a top-level module body with no export — the
 * view→layer state machine could only be verified by eye in a browser. It is now
 * initSegmentStatsMap(ctx); this drives it with a synthetic ctx and a RECORDING maplibre engine, then
 * clicks the "Show" menu buttons and asserts which layers each view asks the engine to draw.
 *
 * The invariant the module's own docstring pins — "S/E markers are always drawn in every mode" — is the
 * headline assertion: every view first clears, then draws seg-start + seg-end, then its view-specific
 * layers. The heavy collaborators (MapShell lifecycle, the real MapLibre engine, terrain, heatmap) are
 * all mocked; seg-stats-shared is REAL so the registerMapShared seam runs for real.
 */

// Recording engine — the union of maplibre calls the view path makes; shared with the test via hoisted.
const M = vi.hoisted(() => ({
	api: {
		clearAll: vi.fn(),
		addCircleMarker: vi.fn(),
		addPolyline: vi.fn(),
		showSegmentRoute: vi.fn(),
		showShapes: vi.fn(),
		showEfforts: vi.fn(() => Promise.resolve()),
		refreshVisibleEfforts: vi.fn(() => Promise.resolve()),
		fitBounds: vi.fn(),
		showSelectedEfforts: vi.fn(() => Promise.resolve()),
		removeDefaultTrack: vi.fn(),
		hideStaticLayers: vi.fn(),
		showStaticLayers: vi.fn(),
	},
}));
const S = vi.hoisted(() => ({
	shell: {
		captureCamera: vi.fn(() => null),
		getStyleUrl: vi.fn(() => null),
		switchToBasemap: vi.fn(),
		setActiveBasemap: vi.fn(),
	},
}));

// ctx from the singleton fails the init guard at import (hasSegment:false) → no init runs on load.
vi.mock("./segment-statistics", () => ({ ctx: { config: { hasSegment: false } } }));
vi.mock("./segment-stats-map-maplibre", () => ({ maplibreApi: M.api, bindShared: vi.fn() }));
vi.mock("./segment-stats-map-hover", () => ({ mapHoverApi: { bind: vi.fn(), handleMouseMove: vi.fn(), resetState: vi.fn() } }));
vi.mock("../core/map-shell", () => ({ createMapShell: () => S.shell }));
vi.mock("../core/basemap-registry", () => ({ defaultBasemapId: () => "liberty", demProviderFor: () => "terrarium" }));
vi.mock("./heatmap-controls", () => ({
	createHeatmapControls: () => ({ status: () => "none", tilesReady: () => false, applyOverlay: vi.fn() }),
}));
vi.mock("../maplibre/maplibre-terrain", () => ({ initMapLibreTerrain: vi.fn(), destroyMapLibre: vi.fn() }));
vi.mock("../core/map-state", () => ({ getMaplibreMap: () => null }));
vi.mock("../maplibre/maplibre-helpers", () => ({
	helpers: { buildInlineTerrainStyle: () => null, addHillshadeAndLight: vi.fn(), fetchStyleJson: () => Promise.resolve({}) },
}));
vi.mock("../maplibre/maplibre-style-swap", () => ({ swapStyleWithTerrain: vi.fn(), cancelPendingSwap: vi.fn() }));
vi.mock("../core/map-toolbar", () => ({ setupMapToolbar: vi.fn() }));

import { initSegmentStatsMap } from "./segment-stats-map";

const SEG_COORDS = [[47, 7], [47.001, 7.001], [47.002, 7.002]];

function mkCtx(over: Partial<SegmentStatisticsContext> = {}): SegmentStatisticsContext {
	return {
		config: {
			hasSegment: true, hasGpsData: true, hasEfforts: true,
			segmentCoords: SEG_COORDS,
			efforts: [{ colorIndex: 0 }, { colorIndex: 1 }],
			shapes: [],
		},
		effortColors: ["#aa0000", "#00aa00"],
		effortVisibility: new Set<number>(),
		getVisibleEffortIndices: () => [0, 1],
		hideMapHighlight: vi.fn(),
		...over,
	} as unknown as SegmentStatisticsContext;
}

/** Mount the "Show" menu buttons the orchestrator wires, plus the summary + filter-pill elements. */
function mountUi(): void {
	document.body.innerHTML =
		'<div id="mapModeValue"></div><span id="segMapFilterPill" hidden></span>' +
		'<button data-map-view="segeff">Seg. + Eff.</button>' +
		'<button data-map-view="segment">Segment</button>' +
		'<button data-map-view="efforts">Efforts</button>';
}

function clickView(view: string): void {
	document.querySelector<HTMLButtonElement>(`[data-map-view="${view}"]`)!.click();
}
function clearRec(): void {
	for (const fn of Object.values(M.api)) (fn as ReturnType<typeof vi.fn>).mockClear();
}
const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

/** ids passed to addCircleMarker this tick — the S/E marker invariant reads off this. */
function markerIds(): string[] {
	return M.api.addCircleMarker.mock.calls.map((c) => c[0] as string);
}
/** ids passed to addPolyline this tick (e.g. "seg-bg" underlay). */
function polylineIds(): string[] {
	return M.api.addPolyline.mock.calls.map((c) => c[0] as string);
}

beforeEach(() => { mountUi(); clearRec(); Object.values(S.shell).forEach((f) => (f as ReturnType<typeof vi.fn>).mockClear()); });
afterEach(() => { document.body.innerHTML = ""; });

describe("initSegmentStatsMap — view state machine", () => {
	it("wires the Show menu and switches the map onto the default basemap on init", () => {
		initSegmentStatsMap(mkCtx());
		expect(S.shell.switchToBasemap).toHaveBeenCalledWith("liberty");
		expect(S.shell.setActiveBasemap).toHaveBeenCalledWith("liberty");
	});

	it("Segment view: draws the segment route, no effort tracks — with S/E markers", async () => {
		initSegmentStatsMap(mkCtx());
		clearRec();
		clickView("segment");
		await flush();

		expect(M.api.clearAll).toHaveBeenCalled();
		expect(markerIds()).toEqual(["seg-start", "seg-end"]);   // S/E invariant
		expect(M.api.showSegmentRoute).toHaveBeenCalled();
		expect(M.api.showEfforts).not.toHaveBeenCalled();
	});

	it("Efforts view: draws the effort tracks + dashed underlay — with S/E markers", async () => {
		initSegmentStatsMap(mkCtx());
		clearRec();
		clickView("efforts");
		await flush();

		expect(markerIds()).toEqual(["seg-start", "seg-end"]);   // S/E invariant
		expect(M.api.showEfforts).toHaveBeenCalledWith([0, 1], false);
		expect(polylineIds()).toContain("seg-bg");
		expect(M.api.showSegmentRoute).not.toHaveBeenCalled();
	});

	it("Seg. + Eff. view: draws both effort tracks and the segment underlay — with S/E markers", async () => {
		initSegmentStatsMap(mkCtx());
		clearRec();
		clickView("segeff");
		await flush();

		expect(markerIds()).toEqual(["seg-start", "seg-end"]);   // S/E invariant
		expect(M.api.showEfforts).toHaveBeenCalledWith([0, 1], false);
		expect(polylineIds()).toContain("seg-bg");
	});

	it("S/E markers are drawn in EVERY mode across a full cycle (the module's docstring invariant)", async () => {
		initSegmentStatsMap(mkCtx());
		for (const view of ["segment", "efforts", "segeff"]) {
			clearRec();
			clickView(view);
			await flush();
			expect(markerIds(), `S/E markers must be present in the "${view}" view`).toEqual(["seg-start", "seg-end"]);
		}
	});

	it("draws the S start marker green and the E end marker red at the segment endpoints ([lng,lat])", async () => {
		initSegmentStatsMap(mkCtx());
		clearRec();
		clickView("segment");
		await flush();

		const start = M.api.addCircleMarker.mock.calls.find((c) => c[0] === "seg-start")!;
		const end = M.api.addCircleMarker.mock.calls.find((c) => c[0] === "seg-end")!;
		// addCircleMarker(id, [lng, lat], color, radius, label)
		expect(start[1]).toEqual([7, 47]);          // first coord [lng,lat]
		expect(start[2]).toBe("#4CAF50");
		expect(start[4]).toBe("S");
		expect(end[1]).toEqual([7.002, 47.002]);    // last coord [lng,lat]
		expect(end[2]).toBe("#f44336");
		expect(end[4]).toBe("E");
	});
});
