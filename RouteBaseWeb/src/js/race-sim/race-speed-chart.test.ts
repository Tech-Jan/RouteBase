/**
 * What is pinned here is the CONFIG the chart assembles, not pixels — the same shape
 * `segment-stats/segment-stats-charts.test.ts` and `core/chart-factory.test.ts` use. The drawing is
 * Chart.js's job and is not this module's contract; the axis extent, the per-rider datasets and the
 * measured-vs-modelled marks are.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { lttbDecimate } from '../core/chart-decimation';
import { PROFILE_TRACE_COLOR } from '../core/profile-chart';
import { installFakeRaf, type FakeRaf } from '../core/raf-test-harness';
import type { RaceCheckpointDto, RaceCourseDto } from './race-api';
import type { FieldSpeedProfile } from './race-field-speed';
import type { RiderSpeedSeries } from './race-rider-speed';
import { SERIES_MODES, type SeriesViews } from '../core/chart-series-modes';

interface Built { type: string; data: unknown; options: unknown; plugins: Array<{ id?: string }> }
let built: Built[] = [];
/** The instance under test. `built` records the configs; this is the live object the module holds. */
let chartInstance: FakeChart | null = null;

class FakeChart {
	canvas: HTMLCanvasElement;
	data: { datasets: unknown[] };
	options: Record<string, never> & Record<string, unknown>;
	scales: Record<string, unknown>;
	chartArea = { top: 0, bottom: 100, left: 0, right: 200 };
	updates: string[] = [];
	draws = 0;
	destroyed = false;

	constructor(
		ctx2d: { canvas: HTMLCanvasElement },
		config: { type: string; data: { datasets: unknown[] }; options: unknown; plugins?: Array<{ id?: string }> },
	) {
		this.canvas = ctx2d.canvas;
		this.data = config.data;
		this.options = config.options as typeof this.options;
		built.push({ type: config.type, data: config.data, options: config.options, plugins: config.plugins ?? [] });
		chartInstance = this;
		this.scales = {
			x: { min: 0, max: 100, axis: 'x', options: { ticks: {} }, getValueForPixel: (p: number) => p, getPixelForValue: (v: number) => v },
			y: { min: 0, max: 50, axis: 'y', options: { ticks: {} }, getPixelForValue: (v: number) => v },
		};
	}
	update(mode?: string): void { this.updates.push(mode ?? 'default'); }
	draw(): void { this.draws++; }
	destroy(): void { this.destroyed = true; }
	resetZoom(): void {}
	isZoomedOrPanned(): boolean { return false; }
	getElementsAtEventForMode(): unknown[] { return []; }
	zoomScale(): void {}
}

interface LineDataset {
	label: string;
	data: Array<{ x: number; y: number }>;
	borderColor: string;
	borderDash?: number[];
	pointRadius: number;
	parsing: boolean;
	normalized: boolean;
}
interface LineOptions {
	scales: { x: { min: number; max: number; type: string }; y: { beginAtZero: boolean; min?: number; max?: number } };
	plugins: { legend: { display: boolean }; zoom: unknown; tooltip: { enabled: boolean } };
	interaction: { mode: string; axis: string };
	events: string[];
}

let raf: FakeRaf;
let canvas: HTMLCanvasElement;
let host: HTMLDivElement;

/** Move the pointer to a canvas x. `getValueForPixel` on the fake is identity, so x === the value. */
function scrubTo(clientX: number): void {
	canvas.dispatchEvent(new MouseEvent('mousemove', { clientX, clientY: 10, bubbles: true }));
}

function readoutText(): string {
	return host.querySelector('.chart-readout')?.textContent ?? '';
}

beforeEach(() => {
	built = [];
	chartInstance = null;
	raf = installFakeRaf();
	vi.stubGlobal('Chart', FakeChart);
	// Wrapped, like production: the readout box is appended into the canvas's parent.
	host = document.createElement('div');
	canvas = document.createElement('canvas');
	host.append(canvas);
	document.body.append(host);
	// jsdom canvases have no 2d context; the chart only needs the object's `canvas` back-reference.
	vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(function (this: HTMLCanvasElement) {
		return { canvas: this } as unknown as CanvasRenderingContext2D;
	});
});

afterEach(() => {
	raf.restore();
	vi.unstubAllGlobals();
	vi.restoreAllMocks();
	document.body.replaceChildren();
});

/**
 * Every mode backed by the same points — these tests are about the chart, not the estimators.
 *
 * `dec` is decimated to the draw target exactly as `buildSeriesViews` does, so the zoom tests below start
 * from the same coarse whole-course line production does. A short series passes through untouched
 * (lttbDecimate returns the input when target >= length), so the other tests are unaffected.
 */
function viewsOf(points: { x: number; y: number }[]): SeriesViews {
	const one = { full: points, dec: lttbDecimate(points, 600) };
	return {
		views: Object.fromEntries(SERIES_MODES.map((m) => [m, one])) as SeriesViews['views'],
		// null = "this mode has no range dependence", which is what `recorded` returns in production;
		// the chart then falls back to slicing `full`, which is the path these tests exercise.
		rebuildRange: () => null,
	};
}

/** n points across the full 0–100 % axis, with y === x so a misread is visible in the readout. */
function ramp(n: number): { x: number; y: number }[] {
	return Array.from({ length: n }, (_, i) => ({ x: (i / (n - 1)) * 100, y: (i / (n - 1)) * 100 }));
}

/** Move the x-window and fire the hook the zoom plugin fires. */
function zoomTo(min: number, max: number): void {
	const scale = chartInstance?.scales.x as { min: number; max: number } | undefined;
	if (scale) { scale.min = min; scale.max = max; }
	const zoom = (chartInstance?.options as unknown as {
		plugins?: { zoom?: { zoom?: { onZoom?: (a: { chart: unknown }) => void } } };
	})?.plugins?.zoom?.zoom;
	zoom?.onZoom?.({ chart: chartInstance });
}

function rider(over: Partial<RiderSpeedSeries> = {}): RiderSpeedSeries {
	const points = [{ x: 0, y: 20 }, { x: 50, y: 30 }, { x: 100, y: 25 }];
	return {
		participantId: 1,
		fieldIndex: 0,
		name: 'Me',
		color: '#ffd43b',
		isMeasured: true,
		views: viewsOf(points),
		...over,
	};
}

/** A 70 km course, so a percentage in these tests has an obvious km beside it. */
const TOTAL_M = 70_000;

function course(): RaceCourseDto {
	const nodes = 71;
	return {
		lat: Array.from({ length: nodes }, () => 47.9),
		lng: Array.from({ length: nodes }, (_, i) => 8.1 + i * 0.001),
		elevation: Array.from({ length: nodes }, (_, i) => 500 + i * 4),
		gradePercent: Array.from({ length: nodes }, () => 0),
		stepMeters: TOTAL_M / (nodes - 1),
		totalMeters: TOTAL_M,
	};
}

function checkpoints(): RaceCheckpointDto[] {
	return [
		{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: 'Hinterzarten', legColor: '#ff6b6b' },
		{ checkpointId: 2, seq: 1, name: 'Raimartihof', chainageMeters: 40_000, node: 40, legName: 'Hennenbach', legColor: '#4dabf7' },
		{ checkpointId: 3, seq: 2, name: 'Finish', chainageMeters: TOTAL_M, node: 70, legName: null, legColor: null },
	];
}

function fieldProfile(): FieldSpeedProfile {
	const bins = Array.from({ length: 20 }, (_, i) => ({
		distanceMeters: (i / 19) * TOTAL_M, median: 24, p10: 16, p90: 34, count: 40,
	}));
	return { bins, totalMeters: TOTAL_M, maxSpeedKmh: 34 };
}

/** The datasets Chart.js was handed on the most recent build. */
function datasets(): LineDataset[] {
	return (chartInstance?.data.datasets ?? []) as LineDataset[];
}

async function subject() {
	return await import('./race-speed-chart');
}

describe('createRaceSpeedChart', () => {
	it('draws one dataset per rider, painted in that rider\'s colour', async () => {
		const { createRaceSpeedChart } = await subject();

		createRaceSpeedChart({
			canvas,
			series: [rider(), rider({ participantId: 2, fieldIndex: 1, name: 'Ana', color: '#4dabf7' })],
		});

		const datasets = (built[0]?.data as { datasets: LineDataset[] }).datasets;
		expect(datasets.length).toBe(2);
		expect(datasets.map((d) => d.label)).toEqual(['Me', 'Ana']);
		expect(datasets.map((d) => d.borderColor)).toEqual(['#ffd43b', '#4dabf7']);
	});

	it('fixes the x-axis to the definition of a percentage, not to the data', async () => {
		const { createRaceSpeedChart } = await subject();

		// A rider whose projection stops at 60 % must not shrink everyone else's axis.
		const stops = [{ x: 0, y: 10 }, { x: 60, y: 10 }];
		const short = rider({ views: viewsOf(stops) });
		createRaceSpeedChart({ canvas, series: [short] });

		const { x } = (built[0]?.options as LineOptions).scales;
		expect(x.type).toBe('linear');
		expect(x.min).toBe(0);
		expect(x.max).toBe(100);
	});

	it('marks a modelled rider as simulated, in the line AND in words', async () => {
		const { createRaceSpeedChart } = await subject();

		createRaceSpeedChart({ canvas, series: [rider({ isMeasured: false, name: 'Rider 42' })] });

		const [dataset] = (built[0]?.data as { datasets: LineDataset[] }).datasets;
		expect(dataset?.label).toBe('Rider 42 (simulated)');
		expect(dataset?.borderDash).toBeDefined();
	});

	it('leaves a measured rider\'s line solid', async () => {
		const { createRaceSpeedChart } = await subject();

		createRaceSpeedChart({ canvas, series: [rider()] });

		const [dataset] = (built[0]?.data as { datasets: LineDataset[] }).datasets;
		expect(dataset?.label).toBe('Me');
		expect(dataset?.borderDash).toBeUndefined();
	});

	it('shows a legend only when there is more than one rider to tell apart', async () => {
		const { createRaceSpeedChart } = await subject();

		createRaceSpeedChart({ canvas, series: [rider()] });
		expect((built[0]?.options as LineOptions).plugins.legend.display).toBe(false);

		createRaceSpeedChart({ canvas, series: [rider(), rider({ participantId: 2, fieldIndex: 1 })] });
		expect((built[1]?.options as LineOptions).plugins.legend.display).toBe(true);
	});

	it('anchors speed at zero and reads every rider at one x position', async () => {
		const { createRaceSpeedChart } = await subject();

		createRaceSpeedChart({ canvas, series: [rider()] });

		const options = built[0]?.options as LineOptions;
		expect(options.scales.y.beginAtZero).toBe(true);
		expect(options.interaction.mode).toBe('index');
		expect(options.interaction.axis).toBe('x');
	});

	it('drives its own hover instead of Chart.js\'s — that cycle is what flickers', async () => {
		const { createRaceSpeedChart } = await subject();

		createRaceSpeedChart({ canvas, series: [rider()] });

		const options = built[0]?.options as LineOptions;
		// Chart.js repaints every line on each native hover event. Both halves are the fix.
		expect(options.plugins.tooltip.enabled).toBe(false);
		expect(options.events).toEqual(['mouseout']);
	});

	it('shows a floating readout naming the rider, the value and the unit', async () => {
		const { createRaceSpeedChart } = await subject();
		createRaceSpeedChart({ canvas, series: [rider()] });

		scrubTo(50);

		const text = readoutText();
		expect(text).toContain('Me');
		expect(text).toContain('km/h');
		expect(text).toContain('50.0 %');
	});

	// The readout's title gained the two terms a percentage cannot be navigated by. Nothing else on this
	// page is in percent: the Sections editor, the results splits and the course itself are km and names.
	it('names the km and the section under the pointer once it has a course', async () => {
		const { createRaceSpeedChart } = await subject();
		createRaceSpeedChart({ canvas, series: [rider()], course: course(), checkpoints: checkpoints() });

		scrubTo(50);

		expect(readoutText()).toContain('35.00 km');
		expect(readoutText()).toContain('Hinterzarten');
	});

	it('reads every drawn rider at the hovered x, not just the nearest line', async () => {
		const { createRaceSpeedChart } = await subject();
		createRaceSpeedChart({
			canvas,
			series: [rider(), rider({ participantId: 2, fieldIndex: 1, name: 'Ana', color: '#4dabf7' })],
		});

		scrubTo(50);

		expect(readoutText()).toContain('Me');
		expect(readoutText()).toContain('Ana');
	});

	it('follows a finger, so the readout is not frozen on mobile', async () => {
		const { createRaceSpeedChart } = await subject();
		createRaceSpeedChart({ canvas, series: [rider()] });

		canvas.dispatchEvent(new TouchEvent('touchstart', {
			touches: [{ clientX: 30, clientY: 10 } as unknown as Touch], bubbles: true,
		}));

		expect(readoutText()).toContain('30.0 %');
	});

	it('takes the readout away when the pointer leaves', async () => {
		const { createRaceSpeedChart } = await subject();
		createRaceSpeedChart({ canvas, series: [rider()] });
		scrubTo(50);

		canvas.dispatchEvent(new MouseEvent('mouseleave', { bubbles: true }));

		expect(host.querySelector<HTMLElement>('.chart-readout')?.style.display).toBe('none');
	});

	it('keeps the zoom window when the drawn set changes', async () => {
		const { createRaceSpeedChart } = await subject();

		const chart = createRaceSpeedChart({ canvas, series: [rider()] });
		// finalizeChart already ran one update('none') to freeze the axes — count from there.
		const before = chartInstance?.updates.length ?? 0;
		chart?.setSeries([rider(), rider({ participantId: 2, fieldIndex: 1, name: 'Ana' })]);

		expect(chartInstance?.data.datasets.length).toBe(2);
		expect(chartInstance?.updates.length).toBe(before + 1);
		// 'none' throughout is what preserves the zoom: a full update would re-fit the axes.
		expect(chartInstance?.updates.every((m) => m === 'none')).toBe(true);
	});

	it('re-hides the legend when the set falls back to a single rider', async () => {
		const { createRaceSpeedChart } = await subject();

		const chart = createRaceSpeedChart({ canvas, series: [rider(), rider({ participantId: 2, fieldIndex: 1 })] });
		chart?.setSeries([rider()]);

		const options = chartInstance?.options as unknown as LineOptions;
		expect(options.plugins.legend.display).toBe(false);
	});

	it('destroys the underlying chart and does nothing on a later setSeries', async () => {
		const { createRaceSpeedChart } = await subject();

		const chart = createRaceSpeedChart({ canvas, series: [rider()] });
		const instance = chartInstance;
		chart?.destroy();
		chart?.setSeries([rider(), rider({ participantId: 2, fieldIndex: 1 })]);

		expect(instance?.destroyed).toBe(true);
		expect(instance?.data.datasets.length).toBe(1);
	});

	it('returns null when the canvas cannot give a 2d context', async () => {
		vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
		const { createRaceSpeedChart } = await subject();

		expect(createRaceSpeedChart({ canvas, series: [rider()] })).toBeNull();
	});

	// ── The course behind the riders ────────────────────────────────────────────────────────────────
	// The terrain is a PLUGIN, not a dataset: it is metres against a km/h axis and it needs a fill
	// clipped per section. What is pinned here is that split, not the pixels it draws.

	it('registers the course backdrop only when it is given a course', async () => {
		const { createRaceSpeedChart } = await subject();

		createRaceSpeedChart({ canvas, series: [rider()] });
		expect(built[0]?.plugins.map((p) => p.id)).not.toContain('raceSpeedBackdrop');

		createRaceSpeedChart({ canvas, series: [rider()], course: course(), checkpoints: checkpoints() });
		expect(built[1]?.plugins.map((p) => p.id)).toContain('raceSpeedBackdrop');
	});

	it('keeps the terrain out of the datasets, so it cannot reach the km/h axis', async () => {
		const { createRaceSpeedChart } = await subject();

		createRaceSpeedChart({ canvas, series: [rider()], course: course(), checkpoints: checkpoints() });

		expect(datasets().length).toBe(1);
	});

	it('repaints rather than re-updates when only a boundary moved', async () => {
		const { createRaceSpeedChart } = await subject();
		const chart = createRaceSpeedChart({ canvas, series: [rider()], course: course(), checkpoints: checkpoints() });
		const updates = chartInstance?.updates.length ?? 0;

		// What a drag does, once per pointermove. No dataset changed, so re-running the update pipeline
		// over every rider would be pure waste — and this is the frame budget a drag lives in.
		chart?.setCheckpoints(checkpoints().map((c) => ({ ...c, chainageMeters: c.chainageMeters + 500 })));
		chart?.setSelected(2);

		expect(chartInstance?.draws).toBe(2);
		expect(chartInstance?.updates.length).toBe(updates);
	});

	it('ignores a selection that did not change, so a re-render is never asked for twice', async () => {
		const { createRaceSpeedChart } = await subject();
		const chart = createRaceSpeedChart({ canvas, series: [rider()], course: course(), checkpoints: checkpoints() });

		chart?.setSelected(2);
		chart?.setSelected(2);

		expect(chartInstance?.draws).toBe(1);
	});

	// ── The field aggregate ─────────────────────────────────────────────────────────────────────────
	// It IS km/h, the same unit as a rider, so it is three honest datasets rather than a second backdrop.

	it('draws the field spread and its median under the riders', async () => {
		const { createRaceSpeedChart } = await subject();
		const chart = createRaceSpeedChart({ canvas, series: [rider()], course: course() });

		chart?.setFieldProfile(fieldProfile());

		const all = datasets();
		expect(all.length).toBe(4);
		// The field first, and with a higher `order`, so a rider is never painted over.
		expect(all.slice(0, 3).every((d) => (d as unknown as { order: number }).order > 0)).toBe(true);
		expect((all[3] as unknown as { order?: number }).order).toBeUndefined();
		expect(all[2]?.label).toBe('Field (median)');
		expect(all[3]?.label).toBe('Me');
	});

	it('keeps the spread out of the legend — it is one claim with the median, not three', async () => {
		const { createRaceSpeedChart } = await subject();
		const chart = createRaceSpeedChart({ canvas, series: [rider()], course: course() });

		chart?.setFieldProfile(fieldProfile());

		// Two unlabelled edges; the legend's filter keys on exactly that.
		expect(datasets().filter((d) => d.label === '').length).toBe(2);
		const options = chartInstance?.options as unknown as LineOptions;
		// One rider plus the field is two names — worth a key.
		expect(options.plugins.legend.display).toBe(true);
	});

	it('quotes the field median in the readout, which is what a rider is read against', async () => {
		const { createRaceSpeedChart } = await subject();
		const chart = createRaceSpeedChart({ canvas, series: [rider()], course: course() });
		chart?.setFieldProfile(fieldProfile());

		scrubTo(50);

		expect(readoutText()).toContain('Field');
		expect(readoutText()).toContain('Me');
	});

	it('puts the field on the same percent axis the riders are on', async () => {
		const { createRaceSpeedChart } = await subject();
		const chart = createRaceSpeedChart({ canvas, series: [rider()], course: course() });

		chart?.setFieldProfile(fieldProfile());

		const median = datasets()[2];
		expect(median?.data[0]?.x).toBeCloseTo(0, 6);
		expect(median?.data.at(-1)?.x).toBeCloseTo(100, 6);
	});

	it('scales the y axis to the field spread too, not just to the riders', async () => {
		const { createRaceSpeedChart } = await subject();
		// Every rider tops out at 30; the field's p90 reaches 34, and clipping it would be a lie.
		const chart = createRaceSpeedChart({ canvas, series: [rider()], course: course() });

		chart?.setFieldProfile(fieldProfile());

		const options = chartInstance?.options as unknown as LineOptions;
		expect(options.scales.y.max ?? 0).toBeGreaterThanOrEqual(34);
	});

	it('draws the field in the one shared trace colour, not a local copy of the hex', async () => {
		const { createRaceSpeedChart } = await subject();
		const chart = createRaceSpeedChart({ canvas, series: [rider()], course: course() });

		chart?.setFieldProfile(fieldProfile());

		// PROFILE_TRACE_COLOR: the strip traces the SAME aggregate in it, and its header says there is
		// one constant because colour follows the entity, not the chart it is drawn on.
		expect(datasets()[2]?.borderColor).toBe(PROFILE_TRACE_COLOR);
	});

	// ── The map link ────────────────────────────────────────────────────────────────────────────────

	it('reports the hovered position in METRES, which is what the map needs', async () => {
		const { createRaceSpeedChart } = await subject();
		const onHover = vi.fn();
		createRaceSpeedChart({ canvas, series: [rider()], course: course(), onHover });

		scrubTo(50);

		// 50 % of a 70 km course. The strip owns the dot; this is the door into it.
		expect(onHover).toHaveBeenCalledWith(35_000);
	});

	it('clears the map dot when the pointer leaves', async () => {
		const { createRaceSpeedChart } = await subject();
		const onHover = vi.fn();
		createRaceSpeedChart({ canvas, series: [rider()], course: course(), onHover });
		scrubTo(50);

		canvas.dispatchEvent(new MouseEvent('mouseleave', { bubbles: true }));

		expect(onHover).toHaveBeenLastCalledWith(null);
	});

	it('says nothing about position when it has no course — a pixel then has no chainage', async () => {
		const { createRaceSpeedChart } = await subject();
		const onHover = vi.fn();
		createRaceSpeedChart({ canvas, series: [rider()], onHover });

		scrubTo(50);

		expect(onHover).not.toHaveBeenCalled();
	});

	// ── Zoom re-decimation ──────────────────────────────────────────────────────────────────────────
	// The pattern chart-factory._redecimateForZoom established and this chart never used: zooming in
	// must buy DETAIL, not magnify the same points.

	it('re-slices each rider to the visible window on zoom', async () => {
		const { createRaceSpeedChart } = await subject();
		// 2000 source points; the whole-course view is decimated to the 600 draw target.
		createRaceSpeedChart({ canvas, series: [rider({ views: viewsOf(ramp(2000)) })], course: course() });
		const before = (datasets()[0]?.data ?? []).length;

		zoomTo(45, 55);   // the middle 10 % — ~200 source points, now all under the target

		const after = datasets()[0]?.data ?? [];
		expect(before).toBe(600);
		expect(after.every((p) => p.x >= 40 && p.x <= 60)).toBe(true);
		// Those points shared 600 across the whole course before; now the window has them all.
		expect(after.length).toBeGreaterThan(150);
	});

	it('keeps the readout reading the line that is actually drawn', async () => {
		const { createRaceSpeedChart } = await subject();
		createRaceSpeedChart({ canvas, series: [rider({ views: viewsOf(ramp(2000)) })], course: course() });

		zoomTo(45, 55);
		scrubTo(50);

		// The ramp's y equals its x, so a stale (coarser) lookup would quote a visibly different number.
		expect(readoutText()).toContain('50');
	});

	// ── The playhead ────────────────────────────────────────────────────────────────────────────────

	it('takes the playhead in metres and never repaints the chart for one', async () => {
		const { createRaceSpeedChart } = await subject();
		const chart = createRaceSpeedChart({ canvas, series: [rider()], course: course() });
		const before = chartInstance?.updates.length ?? 0;

		chart?.setPlayhead(TOTAL_M / 2, TOTAL_M * 0.75);

		// It goes to the overlay canvas. A chart.update() per replay frame, with a dozen rider lines
		// underneath, is the exact cost the crosshair was moved off the Chart.js event cycle to avoid.
		expect(chartInstance?.updates.length).toBe(before);
	});
});
