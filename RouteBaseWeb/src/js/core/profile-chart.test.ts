import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
	createProfileChart, PROFILE_TRACE_COLOR, type ProfileBackdrop, type ProfileTrace,
} from './profile-chart';
import { groupByStyle } from './profile-style-groups';
import { must } from './invariant';

// The real implementation still runs — this only makes the calls countable, which is the whole point of
// hoisting it out of the frame path. Every other test in this file is unaffected.
vi.mock('./profile-style-groups', async (importOriginal) => {
	const actual = await importOriginal<typeof import('./profile-style-groups')>();
	return { ...actual, groupByStyle: vi.fn(actual.groupByStyle) };
});
const groupCalls = vi.mocked(groupByStyle);

/**
 * The strip has two backdrops, and the difference between them is a claim about the world:
 *
 *   terrain — ONE piece of ground everyone is on, so a dot's height is READ OFF it. Two dots at the
 *             same x are at the same place, and the picture says so.
 *   series  — N independent lines, so a dot carries its OWN height. A shapes segment has no reference
 *             GPS at all, so "50%" is 50% of each effort's own distance; drawing one shared backdrop
 *             there would assert a shared position that does not exist.
 *
 * These pin that difference, plus the decorations seam that keeps race vocabulary out of core.
 */

interface Op {
	op: string;
	fillStyle: string;
	strokeStyle: string;
	globalAlpha: number;
	lineWidth: number;
	points: Array<[number, number]>;
	text?: string | undefined;
}

/** Records each fill/stroke/text with the path and the style in force at the time. */
function recordingContext() {
	const ops: Op[] = [];
	let points: Array<[number, number]> = [];

	const ctx = {
		fillStyle: '', strokeStyle: '', globalAlpha: 1, lineWidth: 1, lineJoin: '', font: '',
		textAlign: 'left', textBaseline: 'top',
		beginPath() { points = []; },
		moveTo(x: number, y: number) { points.push([x, y]); },
		lineTo(x: number, y: number) { points.push([x, y]); },
		arc(x: number, y: number) { points.push([x, y]); },
		closePath() {},
		record(op: string, text?: string) {
			ops.push({
				op, text,
				fillStyle: String(ctx.fillStyle), strokeStyle: String(ctx.strokeStyle),
				globalAlpha: ctx.globalAlpha, lineWidth: ctx.lineWidth, points: [...points],
			});
			points = [];
		},
		fill() { ctx.record('fill'); },
		stroke() { ctx.record('stroke'); },
		fillText(text: string) { ctx.record('fillText', text); },
		fillRect() {},
		clearRect() {}, drawImage() {}, save() {}, restore() {}, rect() {}, clip() {},
		setTransform() {}, scale() {}, setLineDash() {},
		measureText: () => ({ width: 10 }),
	};
	return { ctx, ops };
}

class FakeResizeObserver {
	observe(): void {}
	unobserve(): void {}
	disconnect(): void {}
}

/** A 1 km ramp: 0 m up to 100 m. Sloped on purpose — a flat course cannot tell y-from-terrain apart. */
const RAMP: ProfileBackdrop = {
	kind: 'terrain', elevation: [0, 50, 100], step: 500, axisMax: 1000,
};

/** 800×200 canvas, padding 8/8/10/0 → plot x ∈ [8, 792], y ∈ [10, 200]. The plot runs to the canvas's
 *  bottom edge: STRIP_PADDING.bottom is 0, so the terrain's baseline IS that edge. */
const X_OF = (value: number) => 8 + (value / 1000) * 784;
const Y_OF = (height: number) => 200 - (height / 100) * 190;

function mount() {
	vi.stubGlobal('ResizeObserver', FakeResizeObserver);
	const main = recordingContext();
	const back = recordingContext();
	// The chart takes the visible canvas's context first, then the offscreen backdrop one.
	let handedOut = 0;
	HTMLCanvasElement.prototype.getContext = vi.fn(() => {
		const which = handedOut++;
		return which === 0 ? main.ctx : which === 1 ? back.ctx : recordingContext().ctx;
	}) as never;
	HTMLCanvasElement.prototype.getBoundingClientRect = vi.fn(
		() => ({ left: 0, top: 0, width: 800, height: 200, right: 800, bottom: 200 })) as never;

	const canvas = document.createElement('canvas');
	document.body.appendChild(canvas);
	return { canvas, frame: main.ops, backdrop: back.ops };
}

/**
 * The dots of one painted colour in the LATEST frame, as [x, y] pairs. Each dot contributes a moveTo
 * (to break the sub-path) and then the arc itself, so only every second point is a dot centre.
 */
function dotsOf(ops: Op[], color: string): Array<[number, number]> {
	const last = ops.filter((o) => o.op === 'fill' && o.fillStyle === color).at(-1);
	return (last?.points ?? []).filter((_, i) => i % 2 === 1);
}

describe('createProfileChart — terrain backdrop', () => {
	beforeEach(() => { document.body.innerHTML = ''; });

	it('reads each dot height OFF the shared ground', () => {
		const { canvas, frame } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });
		chart.setField({ values: [0, 500, 1000], finished: [0, 0, 0], colors: ['#aa0000', '#aa0000', '#aa0000'] });

		chart.tick(true);
		const dots = dotsOf(frame, '#aa0000');

		expect(dots).toHaveLength(3);
		// Bottom of the ramp, middle, top — the dots climb it.
		expect(must(dots[0], 'dot 0')[1]).toBeCloseTo(Y_OF(0), 1);
		expect(must(dots[1], 'dot 1')[1]).toBeCloseTo(Y_OF(50), 1);
		expect(must(dots[2], 'dot 2')[1]).toBeCloseTo(Y_OF(100), 1);
		expect(must(dots[0], 'dot 0')[0]).toBeCloseTo(X_OF(0), 1);
		expect(must(dots[2], 'dot 2')[0]).toBeCloseTo(X_OF(1000), 1);
	});

	it('with no bands the backdrop is ONE untinted fill — the cheap path a plain strip wants', () => {
		const { canvas, backdrop } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });

		backdrop.length = 0;   // ignore the construction paint
		chart.setDecorations({ bands: [], markers: [] });

		const fills = backdrop.filter((o) => o.op === 'fill');
		expect(fills).toHaveLength(1);
		expect(must(fills[0], 'the one fill').fillStyle).toBe('rgba(148, 163, 184, 0.30)');
	});

	it('bands tint stretches of the SAME curve, one fill each', () => {
		const { canvas, backdrop } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });

		backdrop.length = 0;
		chart.setDecorations({
			bands: [
				{ fromValue: 0, toValue: 400, color: '#ff0000' },
				{ fromValue: 400, toValue: 1000, color: '#00ff00' },
			],
			markers: [],
		});

		const fills = backdrop.filter((o) => o.op === 'fill');
		expect(fills.map((f) => f.fillStyle)).toEqual(['#ff0000', '#00ff00']);
		// Same curve both times: every fill walks all 3 nodes (plus the two baseline corners).
		expect(must(fills[0], 'fill 0').points).toHaveLength(5);
		expect(must(fills[1], 'fill 1').points).toHaveLength(5);
	});

	it('markers are ruled lines, and the emphasised one is drawn heavier', () => {
		const { canvas, backdrop } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });

		backdrop.length = 0;
		chart.setDecorations({
			bands: [],
			markers: [
				{ id: 1, atValue: 250, emphasised: false },
				{ id: 2, atValue: 750, emphasised: true },
			],
		});

		const rules = backdrop.filter((o) => o.op === 'stroke');
		expect(rules).toHaveLength(2);
		const plain = must(rules[0], 'rule 0'), heavy = must(rules[1], 'rule 1');
		expect(plain.lineWidth).toBe(1);
		expect(must(plain.points[0], 'first point')[0]).toBeCloseTo(X_OF(250), 1);
		expect(heavy.lineWidth).toBe(2);
		expect(must(heavy.points[0], 'first point')[0]).toBeCloseTo(X_OF(750), 1);
	});

	it('draws the leader under the playhead, so the followed one is what survives an overlap', () => {
		const { canvas, frame } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });

		chart.setPlayhead(300);    // the followed participant
		chart.setLeadLine(800);    // the leader, ahead of him
		chart.tick(true);

		const rules = frame.filter((o) => o.op === 'stroke');
		expect(rules).toHaveLength(2);
		// Leader first, playhead second — the later stroke is the one drawn on top.
		const lead = must(rules[0], 'rule 0'), followed = must(rules[1], 'rule 1');
		expect(must(lead.points[0], 'first point')[0]).toBeCloseTo(X_OF(800), 1);
		expect(lead.strokeStyle).toBe('rgba(250, 204, 21, 0.6)');
		expect(must(followed.points[0], 'first point')[0]).toBeCloseTo(X_OF(300), 1);
		expect(followed.strokeStyle).toBe('rgba(249, 115, 22, 0.75)');
	});

	it('with nobody followed there is ONE rule — a second line would mark nothing', () => {
		const { canvas, frame } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });

		chart.setPlayhead(300);
		chart.setLeadLine(800);
		chart.tick(true);
		frame.length = 0;

		chart.setLeadLine(null);
		chart.tick(true);

		const rules = frame.filter((o) => o.op === 'stroke');
		expect(rules).toHaveLength(1);
		expect(must(rules[0], 'the one rule').strokeStyle).toBe('rgba(249, 115, 22, 0.75)');
	});

	it('the hover readout names the axis value AND the ground under it', () => {
		const { canvas, frame } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });

		chart.setHover(500);

		expect(frame.find((o) => o.op === 'fillText')?.text).toBe('0.50 km · 50 m');
	});
});

describe('createProfileChart — series backdrop', () => {
	beforeEach(() => { document.body.innerHTML = ''; });

	const TWO_EFFORTS: ProfileBackdrop = {
		kind: 'series',
		axisMax: 1000,
		series: [
			{ x: [0, 1000], y: [0, 100], color: '#111111' },
			{ x: [0, 1000], y: [100, 0], color: '#222222' },
		],
	};

	it('strokes one line per effort, in its own colour — no shared ground is claimed', () => {
		const { canvas, backdrop } = mount();
		const chart = createProfileChart({ canvas, backdrop: TWO_EFFORTS });

		backdrop.length = 0;
		chart.setDecorations({ bands: [], markers: [] });

		const lines = backdrop.filter((o) => o.op === 'stroke');
		expect(lines.map((l) => l.strokeStyle)).toEqual(['#111111', '#222222']);
		// Nothing is filled: filled areas would occlude each other, and these are separate claims.
		expect(backdrop.filter((o) => o.op === 'fill')).toHaveLength(0);
		expect(must(lines[0], 'line 0').points[0]).toEqual([X_OF(0), Y_OF(0)]);
		expect(must(lines[1], 'line 1').points[0]).toEqual([X_OF(0), Y_OF(100)]);
	});

	it('each dot rides its OWN line — two dots at the same x sit at different heights', () => {
		const { canvas, frame } = mount();
		const chart = createProfileChart({ canvas, backdrop: TWO_EFFORTS });

		chart.setField({
			values: [500, 500],
			heights: [25, 75],   // same moment, two different efforts, two different places
			finished: [0, 0],
			colors: ['#aa0000', '#bb0000'],
		});
		chart.tick(true);

		expect(must(dotsOf(frame, '#aa0000')[0], 'one red dot')[1]).toBeCloseTo(Y_OF(25), 1);
		expect(must(dotsOf(frame, '#bb0000')[0], 'one dark-red dot')[1]).toBeCloseTo(Y_OF(75), 1);
	});

	it('the y-range spans every line, so the efforts stay comparable', () => {
		const { canvas, backdrop } = mount();
		createProfileChart({
			canvas,
			backdrop: {
				kind: 'series', axisMax: 1000,
				series: [{ x: [0, 1000], y: [200, 300], color: '#111111' }],
			},
		});

		const line = backdrop.find((o) => o.op === 'stroke')!;
		// 200 is the floor and 300 the ceiling of this backdrop — bottom and top of the plot.
		expect(must(line.points[0], 'first point')[1]).toBeCloseTo(200, 1);
		expect(must(line.points[1], 'second point')[1]).toBeCloseTo(10, 1);
	});

	it('the hover readout drops the height — the pointer is over no one line', () => {
		const { canvas, frame } = mount();
		createProfileChart({ canvas, backdrop: TWO_EFFORTS }).setHover(500);

		expect(frame.find((o) => o.op === 'fillText')?.text).toBe('0.50 km');
	});

	it('a caller-supplied formatter owns the vocabulary — the axis is not always metres', () => {
		const { canvas, frame } = mount();
		createProfileChart({
			canvas, backdrop: TWO_EFFORTS,
			formatHover: (v) => `${Math.round(v / 10)}%`,
		}).setHover(500);

		expect(frame.find((o) => o.op === 'fillText')?.text).toBe('50%');
	});
});

describe('createProfileChart — swapping the backdrop', () => {
	beforeEach(() => { document.body.innerHTML = ''; });

	it('setBackdrop re-measures the axis and the range', () => {
		const { canvas, backdrop } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });
		chart.setDecorations({ bands: [], markers: [{ id: 1, atValue: 500, emphasised: false }] });

		// Halfway along a 1 km axis.
		expect(must(backdrop.filter((o) => o.op === 'stroke').at(-1)!.points[0], 'first point')[0]).toBeCloseTo(X_OF(500), 1);

		chart.setBackdrop({ kind: 'series', axisMax: 100, series: [{ x: [0, 100], y: [0, 10], color: '#111' }] });

		// The same marker is now at 500 of a 100-unit axis — off the right edge, not halfway.
		const rule = backdrop.filter((o) => o.op === 'stroke').at(-1)!;
		expect(must(rule.points[0], 'first point')[0]).toBeGreaterThan(792);
	});
});

/**
 * PERF P3, as a counted proxy rather than a benchmark. The dots MOVE every frame but never change
 * STYLE, so the bucketing is hoisted out of the 30 Hz path and memoised. Counting the calls states that
 * directly and survives in CI; a wall-clock number would measure the machine, and a heap delta cannot
 * prove an allocation claim at all.
 *
 * The second half matters more than the first: a memo whose invalidation is wrong paints stale colours,
 * which is a real bug traded for a perf win. Each setter that can change a style is pinned below.
 */
describe('createProfileChart — style grouping is hoisted out of the frame path', () => {
	const FIELD = {
		values: [100, 200, 300, 400],
		finished: [0, 0, 0, 0],
		colors: ['#aaa', '#bbb', '#aaa', '#bbb'],
	};

	function mountWithField() {
		const { canvas, frame } = mount();
		let nowMs = 1000;
		const chart = createProfileChart({ canvas, backdrop: RAMP, now: () => nowMs });
		chart.setField(FIELD);
		groupCalls.mockClear();
		return { chart, frame, advance: (ms: number) => { nowMs += ms; } };
	}

	it('groups ONCE for the whole replay — the dots move, their styling does not', () => {
		// Cleared BEFORE the window opens (the mock is module-scoped, so earlier describes have already
		// used it) and not again afterwards: the grouping setField's repaint performs is the one legitimate
		// call, and clearing after it would hide whether the 30 frames added more.
		groupCalls.mockClear();
		const { canvas, frame } = mount();
		let nowMs = 1000;
		const chart = createProfileChart({ canvas, backdrop: RAMP, now: () => nowMs });
		chart.setField(FIELD);

		const FRAMES = 30;
		for (let i = 0; i < FRAMES; i++) {
			nowMs += 34;                       // a full 30 Hz interval, so no tick is throttled away
			chart.setPlayhead(i * 10);
			chart.tick();
		}

		// The frames really ran — without this the call count below would pass vacuously if the throttle
		// swallowed every tick, which is exactly how a perf test quietly stops measuring anything.
		expect(dotsOf(frame, '#aaa')).toHaveLength(2);
		// ...and 30 frames of movement added no re-grouping at all. Before the memo this was 31.
		expect(groupCalls).toHaveBeenCalledTimes(1);
	});

	it('setColors re-groups — it mutates the colours array IN PLACE, so identity cannot detect it', () => {
		const { chart } = mountWithField();

		chart.setColors(new Map([[0, '#ff0000']]));

		expect(groupCalls).toHaveBeenCalledTimes(1);
	});

	it('setFilter re-groups — dimming and hiding both change which bucket a dot belongs to', () => {
		const { chart } = mountWithField();

		chart.setFilter({ bright: new Set([0, 1]) });
		expect(groupCalls).toHaveBeenCalledTimes(1);

		chart.setFilter({ visible: new Set([0]) });
		expect(groupCalls).toHaveBeenCalledTimes(2);
	});

	it('setField re-groups — a new field brings new colours and new finished flags', () => {
		const { chart } = mountWithField();

		chart.setField({ ...FIELD, colors: ['#111', '#111', '#111', '#111'] });

		expect(groupCalls).toHaveBeenCalledTimes(1);
	});

	it('the memo paints the NEW colour, not a stale one — invalidation proven through the canvas', () => {
		const { chart, frame, advance } = mountWithField();
		advance(34);
		chart.tick();
		expect(dotsOf(frame, '#ff0000')).toHaveLength(0);

		chart.setColors(new Map([[0, '#ff0000']]));
		advance(34);
		chart.tick();

		expect(dotsOf(frame, '#ff0000')).toHaveLength(1);
	});
});

/**
 * The trace — a SECOND measured series over the backdrop, on its own y-scale.
 *
 * That is the one place the strip carries two units at once, so what these pin is the thing that keeps it
 * from being a dual-axis chart: the trace scale is anchored at 0 and topped at a round number ABOVE the
 * data (never fitted to it), the terrain gets no numbers, and the gridlines belong to the trace.
 */
describe('createProfileChart — the trace overlay', () => {
	beforeEach(() => { document.body.innerHTML = ''; });

	/** Speeds from 10 to 40 km/h across the 1 km ramp. niceTop(40) = ceil(44/10)*10 = 50. */
	const TRACE: ProfileTrace = {
		x: [0, 500, 1000], y: [10, 40, 20], color: PROFILE_TRACE_COLOR,
	};
	const TRACE_TOP = 50;
	/** The trace's own 0..top scale over the same plot box the terrain uses. */
	const TRACE_Y_OF = (kmh: number) => 200 - (kmh / TRACE_TOP) * 190;

	it('scales the line over 0..niceTop, NOT over its own min/max', () => {
		const { canvas, backdrop } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });

		backdrop.length = 0;
		chart.setTrace(TRACE);

		const line = must(
			backdrop.filter((o) => o.op === 'stroke' && o.strokeStyle === PROFILE_TRACE_COLOR).at(-1),
			'the trace line');
		expect(line.lineWidth).toBe(2);
		expect(line.points).toHaveLength(3);
		// 10 km/h sits a fifth up the strip, not on the floor — which is what "anchored at 0" buys: a low
		// line means genuinely slow, rather than merely "the slowest this ride happened to go".
		expect(must(line.points[0], 'sample 0')[1]).toBeCloseTo(TRACE_Y_OF(10), 1);
		expect(must(line.points[1], 'sample 1')[1]).toBeCloseTo(TRACE_Y_OF(40), 1);
		expect(must(line.points[2], 'sample 2')[1]).toBeCloseTo(TRACE_Y_OF(20), 1);
		// And the x positions are the backdrop's, so line and terrain cannot drift apart.
		expect(must(line.points[1], 'sample 1')[0]).toBeCloseTo(X_OF(500), 1);
	});

	/**
	 * The strip draws NO y-axis, for the trace or the terrain. Both are read as shapes; the number comes
	 * from the hover readout, which names its own units. The trace briefly had gridlines and left-edge
	 * labels — they were the only numbers on a 190px strip and cost a fifth of its height.
	 */
	it('draws no gridlines and no axis labels', () => {
		const { canvas, backdrop } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });

		backdrop.length = 0;
		chart.setTrace(TRACE);

		expect(backdrop.filter((o) => o.op === 'fillText')).toEqual([]);
		// The only strokes left belong to the trace line itself, never to a horizontal rule across the plot.
		const strokes = backdrop.filter((o) => o.op === 'stroke');
		expect(strokes.every((o) => o.strokeStyle === PROFILE_TRACE_COLOR)).toBe(true);
	});

	it('measures the top over the BAND, so a p90 excursion is not clipped off the strip', () => {
		const { canvas, backdrop } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });

		backdrop.length = 0;
		// The median tops out at 40, but the spread reaches 75 — the scale has to follow the band.
		chart.setTrace({ ...TRACE, band: { low: [5, 20, 10], high: [20, 75, 40] } });

		// niceTop(75) = ceil(82.5/20)*20 = 100. With no labels left to read the scale off, read it off the
		// LINE: the 40 km/h sample must sit 40/100 up the plot, not 40/50 — which is the clipping this
		// prevents, seen from below.
		const line = must(
			backdrop.filter((o) => o.op === 'stroke' && o.strokeStyle === PROFILE_TRACE_COLOR).at(-1),
			'the trace line');
		expect(must(line.points[1], 'sample 1')[1]).toBeCloseTo(200 - (40 / 100) * 190, 1);
	});

	it('draws the band as one closed area under the line, in the trace colour', () => {
		const { canvas, backdrop } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });

		backdrop.length = 0;
		chart.setTrace({ ...TRACE, band: { low: [5, 20, 10], high: [20, 45, 30] } });

		const band = must(
			backdrop.filter((o) => o.op === 'fill' && o.fillStyle === PROFILE_TRACE_COLOR).at(-1),
			'the spread band');
		expect(band.globalAlpha).toBeCloseTo(0.18, 6);
		// Up the highs, back down the lows: 3 + 3 points, and it returns to where it started in x.
		expect(band.points).toHaveLength(6);
		expect(must(band.points[0], 'first')[0]).toBeCloseTo(must(band.points[5], 'last')[0], 6);
	});

	it('the hover reads the NEAREST sample, and reports null past the trace', () => {
		const { canvas } = mount();
		const seen: Array<number | null> = [];
		const chart = createProfileChart({
			canvas,
			backdrop: RAMP,
			formatHover: (_value, _y, trace) => { seen.push(trace); return 'x'; },
		});
		chart.setTrace(TRACE);

		// 460 m is nearer the 500 m sample than the 0 m one.
		chart.setHover(460);
		expect(seen.at(-1)).toBe(40);
		chart.setHover(100);
		expect(seen.at(-1)).toBe(10);

		chart.setTrace(null);
		chart.setHover(460);
		expect(seen.at(-1)).toBeNull();
	});

	it('setTrace(null) removes the line and its gridlines — no orphan axis over a bare terrain', () => {
		const { canvas, backdrop } = mount();
		const chart = createProfileChart({ canvas, backdrop: RAMP });
		chart.setTrace(TRACE);

		backdrop.length = 0;
		chart.setTrace(null);

		// `strokeStyle` alone would match a terrain fill that merely inherited the last colour set —
		// the recorder carries style forward exactly as a canvas does. Only actual strokes count.
		expect(backdrop.filter((o) => o.op === 'stroke' && o.strokeStyle === PROFILE_TRACE_COLOR))
			.toHaveLength(0);
		expect(backdrop.filter((o) => o.op === 'fillText')).toHaveLength(0);
	});
});
