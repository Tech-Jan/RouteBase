/**
 * What is pinned here is the GEOMETRY and the COLOURS — where a section starts on a percent axis, which
 * tint it wears, where a checkpoint's rule lands — not pixels. The 2d context is a recorder, so a fill in
 * the wrong colour or a band clipped to the wrong span fails a test; a two-pixel difference in the
 * silhouette does not, because that is Chart.js's plot box, not this module's contract.
 *
 * Same boundary `race-profile.test.ts` draws against the strip.
 */
import { describe, expect, it } from 'vitest';
import type { RaceCheckpointDto, RaceCourseDto } from './race-api';
import {
	backdropBands, checkpointAtPixel, createRaceSpeedBackdrop, legAt, metresOf, OUT_OF_RACE_COLOR, percentOf,
} from './race-speed-backdrop';

const TOTAL = 70_000;
const NODES = 71;

function course(): RaceCourseDto {
	return {
		lat: Array.from({ length: NODES }, () => 47.9),
		lng: Array.from({ length: NODES }, (_, i) => 8.1 + i * 0.001),
		// A ramp, so the silhouette has a shape and min/max are not equal.
		elevation: Array.from({ length: NODES }, (_, i) => 500 + i * 4),
		gradePercent: Array.from({ length: NODES }, () => 0),
		stepMeters: TOTAL / (NODES - 1),
		totalMeters: TOTAL,
	};
}

function cp(over: Partial<RaceCheckpointDto> & { checkpointId: number; chainageMeters: number }): RaceCheckpointDto {
	return {
		seq: 0, name: '', node: 0, legName: null, legColor: null, ...over,
	};
}

/** Start · Raimartihof · Finish, the middle two legs coloured. */
function checkpoints(): RaceCheckpointDto[] {
	return [
		cp({ checkpointId: 1, chainageMeters: 0, seq: 0, name: 'Start', legName: 'xcski', legColor: '#ff6b6b' }),
		cp({ checkpointId: 2, chainageMeters: 40_000, seq: 1, name: 'Raimartihof', legName: 'run', legColor: '#4dabf7' }),
		cp({ checkpointId: 3, chainageMeters: TOTAL, seq: 2, name: 'Finish' }),
	];
}

interface FillRecord { color: string; alpha: number; clip: { x: number; w: number } }

/**
 * The two inks a checkpoint rule is drawn in — `cssVar` falls back to these under jsdom, where no
 * stylesheet defines the custom properties. Used to tell a RULE stroke from a terrain EDGE stroke,
 * since both go through the same recorder.
 */
const RULE_INK = 'rgba(226, 232, 240, 0.5)';
const RULE_INK_SELECTED = '#ffffff';
const isRule = (color: string): boolean => color === RULE_INK || color === RULE_INK_SELECTED;

/** A 2d context that records what it was asked to fill, inside which clip, and at which y. */
function recorder(): {
	ctx: CanvasRenderingContext2D; fills: FillRecord[]; strokes: string[]; ys: number[];
} {
	const fills: FillRecord[] = [];
	const strokes: string[] = [];
	/** Every y the terrain path visited — how the silhouette's vertical extent is asserted. */
	const ys: number[] = [];
	let clip = { x: 0, w: 0 };
	let pendingRect = { x: 0, w: 0 };
	const ctx = {
		fillStyle: '', strokeStyle: '', globalAlpha: 1, lineWidth: 1,
		save() {}, restore() {},
		beginPath() {}, moveTo() {}, closePath() {},
		lineTo(_x: number, y: number) { ys.push(y); },
		setLineDash() {},
		rect(x: number, _y: number, w: number) { pendingRect = { x, w }; },
		clip() { clip = pendingRect; },
		fill() { fills.push({ color: String(ctx.fillStyle), alpha: ctx.globalAlpha, clip }); },
		stroke() { strokes.push(String(ctx.strokeStyle)); },
		fillRect() {},
	} as unknown as CanvasRenderingContext2D & { fillStyle: string; strokeStyle: string; globalAlpha: number };
	return { ctx: ctx as CanvasRenderingContext2D, fills, strokes, ys };
}

/** A chart whose x-scale maps 0–100 % onto 0–1000 px, so a percentage reads as ten times itself. */
function chart(ctx: CanvasRenderingContext2D) {
	return {
		ctx,
		chartArea: { left: 0, right: 1000, top: 0, bottom: 200, width: 1000, height: 200 },
		scales: { x: { getPixelForValue: (v: number) => v * 10 } },
	} as never;
}

function paint(deps: { checkpoints: RaceCheckpointDto[]; selected?: number | null }) {
	const rec = recorder();
	const backdrop = createRaceSpeedBackdrop({
		course: course(),
		checkpoints: () => deps.checkpoints,
		selected: () => deps.selected ?? null,
	});
	(backdrop.plugin as { beforeDatasetsDraw(c: unknown): void }).beforeDatasetsDraw(chart(rec.ctx));
	return { backdrop, ...rec };
}

describe('the percent axis', () => {
	it('is chainage over the grid length, which is what a rider is placed by', () => {
		expect(percentOf(0, TOTAL)).toBe(0);
		expect(percentOf(35_000, TOTAL)).toBe(50);
		expect(percentOf(TOTAL, TOTAL)).toBe(100);
	});

	it('round-trips, so a click and a rule land on the same metre', () => {
		for (const metres of [0, 1, 12_345, 40_000, TOTAL]) {
			expect(metresOf(percentOf(metres, TOTAL), TOTAL)).toBeCloseTo(metres, 6);
		}
	});

	it('clamps rather than running off the plot, and survives a course with no length', () => {
		expect(percentOf(-100, TOTAL)).toBe(0);
		expect(percentOf(TOTAL * 2, TOTAL)).toBe(100);
		expect(percentOf(500, 0)).toBe(0);
		expect(metresOf(50, 0)).toBe(0);
	});
});

describe('backdropBands', () => {
	it('is one untinted band when the course has no checkpoints at all', () => {
		expect(backdropBands([], TOTAL)).toEqual([
			{ fromMetres: 0, toMetres: TOTAL, color: null, label: null },
		]);
	});

	it('colours each section by the checkpoint it FOLLOWS', () => {
		const bands = backdropBands(checkpoints(), TOTAL);

		expect(bands).toEqual([
			{ fromMetres: 0, toMetres: 40_000, color: '#ff6b6b', label: 'xcski' },
			{ fromMetres: 40_000, toMetres: TOTAL, color: '#4dabf7', label: 'run' },
		]);
	});

	it('greys the ride outside the race, so moving a line inward dims that end rather than erasing it', () => {
		const inset = [
			cp({ checkpointId: 1, chainageMeters: 2_000, legColor: '#ff6b6b' }),
			cp({ checkpointId: 3, chainageMeters: 60_000 }),
		];

		const bands = backdropBands(inset, TOTAL);

		expect(bands).toContainEqual({ fromMetres: 0, toMetres: 2_000, color: OUT_OF_RACE_COLOR, label: null });
		expect(bands).toContainEqual({ fromMetres: 60_000, toMetres: TOTAL, color: OUT_OF_RACE_COLOR, label: null });
	});
});

describe('painting the course', () => {
	it('fills once per section, each clipped to its own span', () => {
		const { fills } = paint({ checkpoints: checkpoints() });

		expect(fills.map((f) => f.color)).toEqual(['#ff6b6b', '#4dabf7']);
		// 0–40 km and 40–70 km, on a 10 px-per-percent scale.
		const boundary = 40_000 / TOTAL * 1000;
		expect(fills[0]?.clip.x).toBe(0);
		expect(fills[0]?.clip.w).toBeCloseTo(boundary, 6);
		expect(fills[1]?.clip.x).toBeCloseTo(boundary, 6);
		expect(fills[1]?.clip.w).toBeCloseTo(1000 - boundary, 6);
	});

	it('washes a tinted section rather than blocking it — a rider line reads straight through', () => {
		const { fills } = paint({ checkpoints: checkpoints() });

		expect(fills.every((f) => f.alpha > 0 && f.alpha < 1)).toBe(true);
	});

	/**
	 * The geometry the activity (`chart-factory`'s yBg `grace: "8%"`) and segment
	 * (`segment-stats-charts-plugins`) elevation backdrops both use: the WHOLE plot height, graced 8 % at
	 * each end. Asserted against area.top/bottom rather than a fraction constant, so tuning the constant
	 * cannot quietly reintroduce the squashed band this replaced.
	 */
	it('uses the full plot height, graced at both ends — not a band at the bottom', () => {
		const { ys } = paint({ checkpoints: checkpoints() });
		const top = 0, bottom = 200, plotH = bottom - top;

		const highest = Math.min(...ys);
		const lowest = Math.max(...ys.filter((y) => y < bottom));   // `bottom` itself is the path's anchor

		// The summit sits within the 8 % grace of the top, and the valley within 8 % of the bottom.
		expect(highest).toBeGreaterThanOrEqual(top);
		expect(highest).toBeLessThan(top + plotH * 0.12);
		expect(lowest).toBeGreaterThan(bottom - plotH * 0.12);
		// And the silhouette spans most of the plot — the old bottom-42 % band could not.
		expect(lowest - highest).toBeGreaterThan(plotH * 0.8);
	});

	it('draws one rule per checkpoint', () => {
		const { strokes } = paint({ checkpoints: checkpoints() });

		expect(strokes.filter(isRule).length).toBe(3);
	});

	it('draws the focused boundary in a different ink from the rest', () => {
		const { strokes } = paint({ checkpoints: checkpoints(), selected: 2 });

		// Three rules, and the middle one is not the colour the other two share.
		const rules = strokes.filter(isRule);
		expect(rules[1]).not.toBe(rules[0]);
		expect(rules[0]).toBe(rules[2]);
	});

	// A full-height fill has to be faint or it buries the rider lines, and a faint fill only reads as a
	// PROFILE once its ridge is drawn — the pairing segment-stats uses (ELEV_BG_FILL + ELEV_BG_STROKE).
	it('strokes each section\'s top edge in that section\'s own colour', () => {
		const { strokes } = paint({ checkpoints: checkpoints() });

		expect(strokes.filter((c) => !isRule(c))).toEqual(['#ff6b6b', '#4dabf7']);
	});

	it('draws nothing at all when the chart has no laid-out x-scale yet', () => {
		const rec = recorder();
		const backdrop = createRaceSpeedBackdrop({
			course: course(), checkpoints: checkpoints, selected: () => null,
		});

		(backdrop.plugin as { beforeDatasetsDraw(c: unknown): void })
			.beforeDatasetsDraw({ ctx: rec.ctx, chartArea: null, scales: {} } as never);

		expect(rec.fills).toEqual([]);
	});
});

describe('pixelXOf', () => {
	it('places a checkpoint at its share of the plot', () => {
		const { backdrop, ctx } = paint({ checkpoints: checkpoints() });

		expect(backdrop.pixelXOf(chart(ctx), 35_000)).toBeCloseTo(500, 6);
	});

	it('is null for a checkpoint the zoom has scrolled off the plot', () => {
		const { backdrop, ctx } = paint({ checkpoints: checkpoints() });
		const zoomed = {
			ctx,
			chartArea: { left: 400, right: 600, top: 0, bottom: 200 },
			scales: { x: { getPixelForValue: (v: number) => v * 10 } },
		} as never;

		expect(backdrop.pixelXOf(zoomed, 35_000)).toBeCloseTo(500, 6);   // 50 % → 500 px, inside 400–600
		expect(backdrop.pixelXOf(zoomed, 0)).toBeNull();                 // 0 % → 0 px, outside it
	});
});

describe('checkpointAtPixel', () => {
	const live = () => chart(recorder().ctx);

	it('finds the boundary a press landed on', () => {
		// 40 km of 70 is 57.14 %, so 571.4 px.
		expect(checkpointAtPixel(live(), checkpoints(), 574, TOTAL, 12)?.checkpointId).toBe(2);
	});

	it('misses when the press is outside the grab radius', () => {
		expect(checkpointAtPixel(live(), checkpoints(), 600, TOTAL, 12)).toBeNull();
	});

	it('picks the NEAREST when two are within reach, so a press is never ambiguous', () => {
		const crowded = [
			cp({ checkpointId: 1, chainageMeters: 0 }),
			cp({ checkpointId: 7, chainageMeters: 35_000 }),
			cp({ checkpointId: 8, chainageMeters: 35_700 }),   // 10 px further along
			cp({ checkpointId: 3, chainageMeters: TOTAL }),
		];

		expect(checkpointAtPixel(live(), crowded, 502, TOTAL, 12)?.checkpointId).toBe(7);
		expect(checkpointAtPixel(live(), crowded, 508, TOTAL, 12)?.checkpointId).toBe(8);
	});

	it('is null before the chart has an x-scale, rather than claiming a press it cannot place', () => {
		expect(checkpointAtPixel(null, checkpoints(), 500, TOTAL, 12)).toBeNull();
	});
});

describe('legAt', () => {
	it('names the section a distance falls in', () => {
		expect(legAt(checkpoints(), 10_000)?.label).toBe('xcski');
		expect(legAt(checkpoints(), 50_000)?.label).toBe('run');
	});

	it('is null off the ends of the race, where no section is being ridden', () => {
		const inset = [
			cp({ checkpointId: 1, chainageMeters: 2_000, legName: 'xcski' }),
			cp({ checkpointId: 3, chainageMeters: 60_000 }),
		];

		expect(legAt(inset, 500)).toBeNull();
		expect(legAt(inset, 65_000)).toBeNull();
	});
});
