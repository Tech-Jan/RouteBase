import { afterEach, describe, expect, it, vi } from 'vitest';
import {
	buildEffortSeries, createSegmentReplayProfile, planSegmentProfile, type SegmentProfileEffort,
} from './segment-replay-profile';
import type { EffortDataPoint } from './segment-stats-effort-types';
import { must } from '../core/invariant';

// The chart itself is stubbed: its drawing has its own tests (core/profile-chart.test.ts), and what
// matters here is WHICH value each rule is given.
const chartStub = {
	setBackdrop: vi.fn(), setDecorations: vi.fn(), setTrace: vi.fn(), setField: vi.fn(),
	setColors: vi.fn(), setFilter: vi.fn(), setPlayhead: vi.fn(), setLeadLine: vi.fn(),
	setHover: vi.fn(), setCursor: vi.fn(), tick: vi.fn(), destroy: vi.fn(),
};
/** The options the strip built the chart with — that is where the hover formatter lands. */
interface ChartStubOptions {
	formatHover?: (value: number, y: number | null, trace: number | null) => string;
}
let chartOptions: ChartStubOptions = {};
vi.mock('../core/profile-chart', () => ({
	PROFILE_TRACE_COLOR: '#3b82f6',
	createProfileChart: (o: ChartStubOptions) => { chartOptions = o; return chartStub; },
}));
vi.mock('../core/replay-chart-drawer', () => ({
	createReplayChartDrawer: () => ({
		open: vi.fn(), close: vi.fn(), toggle: vi.fn(), isOpen: () => true, height: () => 120,
		destroy: vi.fn(),
	}),
}));

/**
 * The mode decision is the load-bearing part of this module, because it is a claim about the data:
 * a shared terrain says "every dot at the same x is in the same place", and for a shapes segment that
 * is simply false (zero Segment_GPS rows → segmentProgress null on every point → each effort falls
 * back to its own distance fraction). These pin that the picture follows the data, not a flag.
 */

function pt(time: number, altitude: number | null, distance = time * 5): EffortDataPoint {
	return { distance, time, speed: 18, altitude, lat: null, lng: null };
}

function effort(color: string, points: EffortDataPoint[]): SegmentProfileEffort {
	return { color, points, elapsedTime: points[points.length - 1]?.time ?? 0 };
}

/** ~100 m apart along a meridian, climbing 10 m each hop. */
function coordsWithAltitude(n: number): number[][] {
	return Array.from({ length: n }, (_, i) => [47.9 + i * 0.0009, 8.1, 500 + i * 10]);
}

describe('buildEffortSeries', () => {
	it('gives every effort its own line, in its own colour, against elapsed time', () => {
		const series = buildEffortSeries([
			effort('#aa0000', [pt(0, 500), pt(60, 540), pt(120, 520)]),
			effort('#bb0000', [pt(0, 500), pt(90, 560)]),
		])!;

		expect(series.kind).toBe('series');
		expect(series.series.map((s) => s.color)).toEqual(['#aa0000', '#bb0000']);
		expect(Array.from(must(series.series[0], 'first line').y)).toEqual([500, 540, 520]);
		// The axis spans the SLOWEST effort, so nothing is drawn off the right edge.
		expect(series.axisMax).toBe(120);
	});

	it('drops efforts with no usable altitude instead of flattening them onto zero', () => {
		const series = buildEffortSeries([
			effort('#aa0000', [pt(0, null), pt(60, null)]),
			effort('#bb0000', [pt(0, 500), pt(90, 560)]),
		])!;

		expect(series.series).toHaveLength(1);
		expect(must(series.series[0], 'surviving line').color).toBe('#bb0000');
	});

	it('is null when NOTHING has an altitude — there is no strip to draw', () => {
		expect(buildEffortSeries([effort('#aa0000', [pt(0, null), pt(60, null)])])).toBeNull();
		expect(buildEffortSeries([])).toBeNull();
	});

	it('decimates a long effort but keeps its last point, so the line ends where the ride does', () => {
		const points = Array.from({ length: 5000 }, (_, i) => pt(i, 500 + (i % 50)));
		const series = buildEffortSeries([effort('#aa0000', points)])!;
		const line = must(series.series[0], 'the one line');

		expect(line.x.length).toBeLessThanOrEqual(601);
		expect(line.x[line.x.length - 1]).toBe(4999);
		expect(line.y[line.y.length - 1]).toBe(must(points[4999], 'last point').altitude);
	});
});

describe('planSegmentProfile — the picture follows the data', () => {
	const efforts = [effort('#aa0000', [pt(0, 500), pt(60, 540)])];

	it('a polyline segment with reference altitudes gets the SHARED terrain', () => {
		const plan = planSegmentProfile(
			{ kind: 'polyline', hasGpsData: true, segmentCoords: coordsWithAltitude(6) }, efforts)!;

		expect(plan.mode).toBe('progress');
		expect(plan.backdrop.kind).toBe('terrain');
	});

	it('a shapes segment gets PER-EFFORT lines — "50%" is a different place for each ride', () => {
		const plan = planSegmentProfile({ kind: 'shapes', hasGpsData: false }, efforts)!;

		expect(plan.mode).toBe('time');
		expect(plan.backdrop.kind).toBe('series');
	});

	it('a polyline whose altitudes never reached the client falls back to per-effort lines', () => {
		const flat = coordsWithAltitude(6).map((c) => [must(c[0], 'lat'), must(c[1], 'lng')]);   // lat/lng only
		const plan = planSegmentProfile({ kind: 'polyline', hasGpsData: true, segmentCoords: flat }, efforts)!;

		expect(plan.mode).toBe('time');
		expect(plan.backdrop.kind).toBe('series');
	});

	it('nothing to draw at all is null, not an empty strip', () => {
		expect(planSegmentProfile({ kind: 'shapes' }, [effort('#aa0000', [pt(0, null)])])).toBeNull();
	});
});

describe('the strip\'s two rules — progress mode', () => {
	afterEach(() => {
		document.body.innerHTML = '';
		vi.clearAllMocks();
	});

	/** The markup the strip is mount-gated on, plus a polyline segment so the mode is `progress`. */
	function mount(follow: () => number) {
		document.body.innerHTML = `
			<div id="segReplayDock">
				<div id="segReplayChartsHandle" hidden></div>
				<div id="segReplayCharts"><canvas id="segReplayProfile"></canvas></div>
			</div>`;
		return createSegmentReplayProfile({
			source: { kind: 'polyline', hasGpsData: true, segmentCoords: coordsWithAltitude(6) },
			efforts: [
				effort('#aa0000', [pt(0, 500), pt(60, 540)]),
				effort('#bb0000', [pt(0, 500), pt(60, 540)]),
			],
			// Effort 0 leads at 80%, effort 1 trails at 30%.
			sample: (idx) => ({ distance: 0, time: 30, speed: 20, altitude: 520, progress: idx === 0 ? 80 : 30 }),
			follow,
		})!;
	}

	it('nobody followed: the playhead IS the leader and there is no second rule', () => {
		const strip = mount(() => -1);

		strip.tick(30, true);

		expect(chartStub.setPlayhead).toHaveBeenLastCalledWith(80);
		expect(chartStub.setLeadLine).toHaveBeenLastCalledWith(null);
	});

	it('following the trailing effort puts the playhead on him and the leader on the golden rule', () => {
		const strip = mount(() => 1);

		strip.tick(30, true);

		expect(chartStub.setPlayhead).toHaveBeenLastCalledWith(30);
		expect(chartStub.setLeadLine).toHaveBeenLastCalledWith(80);
	});
});

/**
 * The efforts' aggregate speed, traced over the shared terrain. The rule worth pinning is the GATE:
 * it is a progress-mode picture only. In time mode each effort rides its own line and "50%" is a
 * different place per effort, so a quantile across efforts at one x aggregates positions that have
 * nothing to do with each other — a plausible-looking line that means nothing.
 */
describe('the speed trace', () => {
	afterEach(() => {
		document.body.innerHTML = '';
		vi.clearAllMocks();
	});

	function markup(): void {
		document.body.innerHTML = `
			<div id="segReplayDock">
				<div id="segReplayChartsHandle" hidden></div>
				<div id="segReplayCharts"><canvas id="segReplayProfile"></canvas></div>
			</div>`;
	}

	/** An effort with per-point completion — what a polyline segment's efforts carry. */
	function paced(color: string, speeds: number[]): SegmentProfileEffort {
		const points = speeds.map((speed, i) => ({ ...pt(i * 30, 500 + i * 10), speed }));
		return {
			color, points, elapsedTime: points[points.length - 1]?.time ?? 0,
			progress: speeds.map((_, i) => (i / (speeds.length - 1)) * 100),
		};
	}

	it('traces the aggregate over the shared terrain in progress mode', () => {
		markup();
		const strip = createSegmentReplayProfile({
			source: { kind: 'polyline', hasGpsData: true, segmentCoords: coordsWithAltitude(6) },
			efforts: [paced('#aa0000', [20, 24, 28, 32]), paced('#bb0000', [18, 22, 26, 30])],
			sample: () => null,
		})!;

		const trace = must(chartStub.setTrace.mock.calls[0]?.[0], 'a trace was set');
			expect(trace.color).toBe('#3b82f6');
		// x is completion, so it stays inside the terrain's own 0–100 axis.
		expect(Math.min(...trace.x)).toBeGreaterThanOrEqual(0);
		expect(Math.max(...trace.x)).toBeLessThanOrEqual(100);
		// Two efforts → a real spread, so the band is not the line.
		expect(trace.band).toBeDefined();

		strip.destroy();
	});

	it('draws NO trace in time mode — there is no shared position to aggregate across', () => {
		markup();
		// A shapes segment: no reference GPS, so the strip falls back to per-effort lines.
		const strip = createSegmentReplayProfile({
			source: { kind: 'shapes', hasGpsData: false },
			efforts: [paced('#aa0000', [20, 24, 28, 32]), paced('#bb0000', [18, 22, 26, 30])],
			sample: () => null,
		})!;

		expect(chartStub.setTrace).toHaveBeenCalledWith(null);

		strip.destroy();
	});

	it('draws no trace when the efforts carry no completion, even on a polyline segment', () => {
		markup();
		const strip = createSegmentReplayProfile({
			source: { kind: 'polyline', hasGpsData: true, segmentCoords: coordsWithAltitude(6) },
			// effort() builds without `progress` — the pre-linear-referencing shape.
			efforts: [effort('#aa0000', [pt(0, 500), pt(60, 540)])],
			sample: () => null,
		})!;

		expect(chartStub.setTrace).toHaveBeenCalledWith(null);

		strip.destroy();
	});

	it('the hover adds km/h only where there is a trace to read it from', () => {
		markup();
		const strip = createSegmentReplayProfile({
			source: { kind: 'polyline', hasGpsData: true, segmentCoords: coordsWithAltitude(6) },
			efforts: [paced('#aa0000', [20, 24, 28, 32])],
			sample: () => null,
		})!;

		const formatHover = must(chartOptions.formatHover, 'progress mode formats a hover');
		expect(formatHover(50, 540, 26.4)).toBe('50% · 540 m · 26 km/h');
		expect(formatHover(50, 540, null)).toBe('50% · 540 m');

		strip.destroy();
	});
});
