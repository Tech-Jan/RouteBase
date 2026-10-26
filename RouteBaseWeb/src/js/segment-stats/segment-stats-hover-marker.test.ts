import type { EffortPerformanceData, EffortDataPoint } from './segment-stats-effort-types';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';

// PS-08 characterization pins for the hover POSITION marker (segment-stats-hover.ts).
// Two locked behaviours the payload-shaping downsample must not disturb:
//   1. findClosestPointByX — the point-resolution search (time / distance / percent
//      via segmentProgress), incl. the midpoint tie → LOWER index rule.
//   2. onChartHover → one map marker per visible effort at the resolved point's
//      [lng,lat], with the highlighted flag following selectedEffortIndex.
// Exact index + coordinate assertions so a resampled point stream is caught.
//
// Harness: seed #segmentStatisticsConfig, then import segment-statistics (ctx) +
// segment-stats-hover under a fresh registry. A recording maplibre fake captures
// the coalesced setData FeatureCollection.

interface Pt {
	distance: number; time: number; speed: number;
	altitude: number | null; lat: number | null; lng: number | null;
	segmentProgress?: number | null;
}

// 5 points; time [0,10,20,30,40], distance [0,100,200,300,400], progress [0,.25,.5,.75,1].
function pts(latBase: number, lngBase: number): Pt[] {
	return [0, 1, 2, 3, 4].map((k) => ({
		distance: k * 100, time: k * 10, speed: 5,
		altitude: 0, lat: latBase + k, lng: lngBase + k, segmentProgress: k / 4,
	}));
}

function perfDatum(idx: number, p: Pt[]): EffortPerformanceData {
	return {
		effortId: idx, activityId: 100 + idx, activityName: 'Effort ' + idx,
		activityDate: '2026-01-0' + (idx + 1), elapsedTime: 40,
		points: p as unknown as EffortDataPoint[],
	};
}

function seedConfig(): void {
	const cfg = {
		hasSegment: true, hasGpsData: true, hasEfforts: true, mapTilerApiKey: '',
		segmentId: 5, kind: 'polyline',
		segmentCoords: [[1, 10], [2, 11]],
		effortRows: [
			{ effortIndex: 0, colorIndex: 0, gpsInSegmentId: 1, activityId: 100, activityName: 'A0', activityType: 'Run', isOwn: true, userDisplayName: null, dateIso: '2026-01-01', matchPct: 100, elapsedSec: 40, distanceM: 400, avgSpeedMs: 5, avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0 },
			{ effortIndex: 1, colorIndex: 1, gpsInSegmentId: 2, activityId: 101, activityName: 'A1', activityType: 'Run', isOwn: true, userDisplayName: null, dateIso: '2026-01-02', matchPct: 100, elapsedSec: 40, distanceM: 400, avgSpeedMs: 5, avgHr: 150, avgPower: 200, elevationM: 10, entryId: 0, exitId: 0 },
		],
	};
	document.body.innerHTML = '<script type="application/json" id="segmentStatisticsConfig">' + JSON.stringify(cfg) + '</script>';
}

// Recording maplibre instance: getSource returns a source whose setData is a spy.
interface RecSource { setData: ReturnType<typeof vi.fn>; }
function makeRecordingMap(): { ml: unknown; source: () => RecSource | undefined } {
	const sources = new Map<string, RecSource>();
	const ml = {
		addSource(id: string): void { sources.set(id, { setData: vi.fn() }); },
		addLayer(): void {},
		getSource(id: string): RecSource | undefined { return sources.get(id); },
	};
	return { ml, source: () => sources.get('seg-hover-markers') };
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
async function loadHover(perf: EffortPerformanceData[], visible: number[]): Promise<{ ctx: any }> {
	const { ctx } = await import('./segment-statistics');
	await import('./segment-stats-hover'); // assigns ctx.findClosestPointByX / onChartHover
	ctx.performanceData = perf;
	ctx.effortVisibility.clear();
	for (const i of visible) ctx.effortVisibility.add(i);
	ctx.currentXAxis = 'time';
	ctx.aggregateMode = false;
	ctx.selectedEffortIndex = null;
	ctx.hoveredEffortIndex = null;
	return { ctx };
}

// Stub Chart.js chart: nearest-x hit-test result + a scale that resolves the cursor x.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function stubChart(hoveredX: number, hit: { datasetIndex: number; index: number }): any {
	return {
		canvas: document.createElement('canvas'),
		data: { datasets: [{ data: [{ x: 0 }], hidden: false }, { data: [{ x: 0 }], hidden: false }] },
		scales: { x: { getValueForPixel: (_px: number) => hoveredX } },
		getElementsAtEventForMode: () => [hit],
	};
}

const microtask = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

beforeEach(() => {
	seedConfig();
});

afterEach(async () => {
	const { ctx } = await import('./segment-statistics');
	ctx.performanceData = null;
	// map-state is a module singleton; the resetModules() below re-inits it to null.
	document.body.innerHTML = '';
	const { vi: v } = await import('vitest');
	v.resetModules();
});

describe('segment-stats hover marker — PS-08 pins', () => {
	it('findClosestPointByX resolves by time, distance and percent(segmentProgress)', async () => {
		const { ctx } = await loadHover([perfDatum(0, pts(1, 10))], [0]);
		const p = ctx.performanceData[0].points;

		// time: 22 → index 2 (t=20) closer than index 3 (t=30).
		expect(ctx.findClosestPointByX(p, 22, 'time')).toBe(2);
		// time midpoint 15 (equidistant to t=10 idx1 and t=20 idx2) → LOWER index.
		expect(ctx.findClosestPointByX(p, 15, 'time')).toBe(1);
		// distance: 240 → index 2 (200) closer than index 3 (300).
		expect(ctx.findClosestPointByX(p, 240, 'distance')).toBe(2);
		// percent via segmentProgress: targetX 0–100, progress*100 = [0,25,50,75,100]; 60 → index 2 (50).
		expect(ctx.findClosestPointByX(p, 60, 'percent')).toBe(2);
		// percent midpoint 62.5 (between 50 idx2 and 75 idx3) → LOWER index.
		expect(ctx.findClosestPointByX(p, 62.5, 'percent')).toBe(2);
	});

	it('chart hover places one map marker per visible effort at the resolved point [lng,lat]', async () => {
		const { ctx } = await loadHover([perfDatum(0, pts(1, 10)), perfDatum(1, pts(50, 100))], [0, 1]);
		const { ml, source } = makeRecordingMap();
		// segment-stats-hover reads the active map from map-state (same reset registry).
		const { setMaplibreMap } = await import('../core/map-state');
		setMaplibreMap(ml as unknown as maplibregl.Map);
		ctx.selectedEffortIndex = 1; // effort 1 is the emphasised (highlighted) marker

		// Cursor x resolves to time 20 → each effort's index-2 point is nearest.
		ctx.onChartHover(stubChart(20, { datasetIndex: 0, index: 2 }), { x: 50, type: 'mousemove' });
		await microtask();

		const setData = source()!.setData;
		expect(setData).toHaveBeenCalledTimes(1);
		const fc = setData.mock.calls.at(-1)![0] as { features: Array<{ properties: { highlighted: boolean }; geometry: { coordinates: [number, number] } }> };
		expect(fc.features).toHaveLength(2);

		// Effort 0 index-2 point = lat 3, lng 12 → GeoJSON [lng,lat].
		expect(must(fc.features[0], 'feature 0').geometry.coordinates).toEqual([12, 3]);
		expect(must(fc.features[0], 'feature 0').properties.highlighted).toBe(false);
		// Effort 1 index-2 point = lat 52, lng 102; highlighted (selectedEffortIndex === 1).
		expect(must(fc.features[1], 'feature 1').geometry.coordinates).toEqual([102, 52]);
		expect(must(fc.features[1], 'feature 1').properties.highlighted).toBe(true);
	});
});

// SH-05: ID-16 regression pins — the setChartActive/clearChartActive guards (segment-stats-hover.ts)
// must swallow a stale-active-element throw from a chart that rebuilt since its active element was set.
// These pin the try/catch that a future refactor of the shared helpers could silently drop.
describe('segment-stats hover — ID-16 stale-element guard (SH-05)', () => {
	it('onChartHover swallows a setActiveElements throw on the progress chart', async () => {
		const { ctx } = await loadHover([perfDatum(0, pts(1, 10))], [0]);
		ctx.progressChart = {
			data: { datasets: [{ data: [] }] },
			getActiveElements: () => [],
			setActiveElements: () => { throw new TypeError('stale element'); },
			tooltip: undefined,
			draw: () => {},
		};
		expect(() => ctx.onChartHover(stubChart(20, { datasetIndex: 0, index: 2 }), { x: 50, type: 'mousemove' })).not.toThrow();
	});

	it('onChartLeave swallows a setActiveElements throw on the progress chart', async () => {
		const { ctx } = await loadHover([perfDatum(0, pts(1, 10))], [0]);
		ctx.progressChart = {
			data: { datasets: [{ data: [] }] },
			getActiveElements: () => [{ datasetIndex: 0, index: 0 }],
			setActiveElements: () => { throw new TypeError('stale element'); },
			tooltip: undefined,
			draw: () => {},
		};
		expect(() => ctx.onChartLeave()).not.toThrow();
	});
});
