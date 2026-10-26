import type { EffortPerformanceData, EffortDataPoint } from './segment-stats-effort-types';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { resetAccountSettings, setAccountSettings } from '../core/account-settings';

// PS-08 characterization pins for chart-dataset building (segment-stats-delta.ts).
// Locks how ctx.performanceData → Chart.js {x,y} datasets BEFORE the payload-shaping
// endpoint/DTO changes: exact point mapping (speed/elevation/effort channels), the
// _effortIndex tagging that hover/selection depend on, and placeholder-effort safety.
// The downsample + columnar-wire work must keep these values byte-identical after
// hydration, so the assertions are exact ({x,y} deep-equality), not tolerant.
//
// Harness: seed the #segmentStatisticsConfig JSON tag, then dynamically import
// segment-statistics (ctx) + segment-stats-delta under a fresh module registry.

interface Pt {
	distance: number; time: number; speed: number;
	altitude: number | null; lat: number | null; lng: number | null;
	cumulativeCostKj?: number | null; segmentProgress?: number | null;
}

// Two efforts, 5 points each; distinct channel values so a wrong-column bug is visible.
function points(base: number): Pt[] {
	return [0, 1, 2, 3, 4].map((k) => ({
		distance: base * 1000 + k * 100,
		time: k * 10,
		speed: base * 10 + k, // e.g. 10,11,12… / 20,21,22…
		altitude: base * 100 + k * 5,
		lat: base + k * 0.01,
		lng: 10 * base + k * 0.02,
		cumulativeCostKj: base * 50 + k * 2,
		segmentProgress: k / 4,
	}));
}

function perfDatum(idx: number, pts: Pt[]): EffortPerformanceData {
	return {
		effortId: idx, activityId: 100 + idx, activityName: 'Effort ' + idx,
		activityDate: '2026-01-0' + (idx + 1), elapsedTime: 40,
		points: pts as unknown as EffortDataPoint[],
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
	document.body.innerHTML =
		'<script type="application/json" id="segmentStatisticsConfig">' + JSON.stringify(cfg) + '</script>';

	// Smoothing OFF keeps the plotted speed equal to the raw p.speed — this suite pins exact values, not
	// the smoother's. Set through the SEAM rather than by seeding an #appSettings island: the island is
	// read once by core/core.ts (the layouts' boot entry), which a unit test does not load, so seeding it
	// here would silently arrange nothing and every pinned value would shift under the ON default.
	setAccountSettings({ charts: { speedSmoothingSeconds: 0 } }); // 0 s = no smoothing
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
async function loadDeltaSystem(perf: EffortPerformanceData[], visible: number[]): Promise<{ ctx: any; system: any }> {
	const { ctx } = await import('./segment-statistics');
	const { createDeltaSystem } = await import('./segment-stats-delta');
	ctx.performanceData = perf;
	ctx.effortVisibility.clear();
	for (const i of visible) ctx.effortVisibility.add(i);
	ctx.currentXAxis = 'time';
	ctx.deltaCharts.clear();          // total mode: raw metric, no reference subtraction
	ctx.selectedEffortIndex = null;
	// statsBorderColor is identity so borderColor === effort colour (not under test here);
	// effortPointX = p.time pins the x-axis in time mode. ctx is passed to the delta system as
	// a dep now (the real singleton this harness builds) rather than reached for via the module import.
	const system = createDeltaSystem({ ctx, statsBorderColor: (c: string) => c, effortPointX: (p: EffortDataPoint) => p.time });
	return { ctx, system };
}

beforeEach(() => {
	seedConfig();
});

afterEach(async () => {
	const { ctx } = await import('./segment-statistics');
	ctx.performanceData = null;
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	resetAccountSettings();
	document.body.innerHTML = '';
	const { vi } = await import('vitest');
	vi.resetModules();
});

describe('segment-stats chart datasets — PS-08 pins', () => {
	it('speed/elevation/effort datasets map points to {x:time,y:metric} exactly', async () => {
		const p0 = points(1);
		const p1 = points(2);
		const { system } = await loadDeltaSystem([perfDatum(0, p0), perfDatum(1, p1)], [0, 1]);

		const cases: Array<{ key: string; y: (p: Pt) => number }> = [
			{ key: 'speed', y: (p) => p.speed },
			{ key: 'elevation', y: (p) => p.altitude! },
			{ key: 'effort', y: (p) => p.cumulativeCostKj! },
		];

		for (const c of cases) {
			// The speed metric is processed by default (smoothed then binned); this suite pins the
			// {x,y} MAPPING, so take it unprocessed and let the estimator have its own tests.
			system.setSpeedMode('recorded');
			// eslint-disable-next-line @typescript-eslint/no-explicit-any
			const datasets = system.buildMetricDatasets(c.key, 2, 0) as any[];
			expect(datasets).toHaveLength(2);

			[p0, p1].forEach((pts, di) => {
				expect(datasets[di]._effortIndex).toBe(di);
				expect(datasets[di].data).toEqual(pts.map((p) => ({ x: p.time, y: c.y(p) })));
			});
		}
	});

	it('placeholder efforts (points: []) yield empty datasets without throwing', async () => {
		const p0 = points(1);
		const { system } = await loadDeltaSystem([perfDatum(0, p0), perfDatum(1, [])], [0, 1]);

		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const datasets = system.buildMetricDatasets('speed', 2, 0) as any[];
		expect(datasets).toHaveLength(2);
		expect(datasets[1].data).toEqual([]);          // placeholder → empty, position preserved
		expect(datasets[1]._effortIndex).toBe(1);
		expect(datasets[0].data).toHaveLength(5);
	});
});
