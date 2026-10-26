import { beforeEach, describe, expect, it, vi } from 'vitest';
import { must } from '../core/invariant';
import { OUT_OF_RACE_COLOR, createRaceProfile, outOfRaceSpans, speedTrace } from './race-profile';
import { PROFILE_TRACE_COLOR } from '../core/profile-chart';
import type { RaceCheckpointDto, RaceCourseDto } from './race-api';
import type { FieldSpeedBin, FieldSpeedProfile } from './race-field-speed';

/**
 * The profile is the SECOND view of the field — the map is the first — and the results table's filter
 * drives both through one call. So the two must agree on what the filtered set MEANS, and until now they
 * did not: `RaceMarkers.setDimmed(exceptIndices)` treats its argument as the riders to keep BRIGHT
 * (`dimmed = !bright.has(i)`), while the profile treated the same array as the riders to DIM. The page
 * handed the identical array to both, so filtering the table dimmed exactly the wrong half of the
 * elevation strip.
 *
 * There was no test file here at all, which is how it survived. The contract below is the markers'
 * contract, and the profile's `visible` already followed it — only `dimmed` disagreed.
 */

/** Records what was actually painted: one entry per fill(), with the style in force at the time. */
function recordingContext() {
	const fills: Array<{ fillStyle: string; globalAlpha: number; radii: number[] }> = [];
	let radii: number[] = [];

	const ctx = {
		fillStyle: '#000000',
		strokeStyle: '#000000',
		globalAlpha: 1,
		lineWidth: 1,
		font: '',
		textAlign: 'left',
		textBaseline: 'top',
		beginPath() {
			radii = [];
		},
		arc(_x: number, _y: number, r: number) {
			radii.push(r);
		},
		fill() {
			fills.push({ fillStyle: ctx.fillStyle, globalAlpha: ctx.globalAlpha, radii: [...radii] });
			radii = [];
		},
		clearRect() {},
		drawImage() {},
		moveTo() {},
		lineTo() {},
		closePath() {},
		stroke() {},
		fillRect() {},
		fillText() {},
		measureText: () => ({ width: 10 }),
		save() {},
		restore() {},
		scale() {},
		setTransform() {},
		rect() {},
		clip() {},
		setLineDash() {},
		createLinearGradient: () => ({ addColorStop() {} }),
	};
	return { ctx, fills };
}

/** A flat 1 km course, 11 nodes at 100 m. */
function course(): RaceCourseDto {
	const n = 11;
	return {
		lat: Array.from({ length: n }, () => 47.9),
		lng: Array.from({ length: n }, (_, i) => 8.1 + i * 0.001),
		elevation: Array.from({ length: n }, () => 500),
		gradePercent: Array.from({ length: n }, () => 0),
		stepMeters: 100,
		totalMeters: 1000,
	};
}

/** One colour per rider, so each lands in its own colour-grouped fill and can be read back by name. */
const COLORS = ['#aa0000', '#bb0000', '#cc0000', '#dd0000'];

/** jsdom has no ResizeObserver, and the profile observes its canvas to re-fit the terrain. */
class FakeResizeObserver {
	observe(): void {}
	unobserve(): void {}
	disconnect(): void {}
}

function mount() {
	vi.stubGlobal('ResizeObserver', FakeResizeObserver);

	const { ctx, fills } = recordingContext();
	// The profile takes the main canvas's context first, then an offscreen one for the terrain. Only the
	// first is the one that paints riders.
	let handedOut = 0;
	HTMLCanvasElement.prototype.getContext = vi.fn(() =>
		(handedOut++ === 0 ? ctx : recordingContext().ctx)) as never;
	HTMLCanvasElement.prototype.getBoundingClientRect = vi.fn(
		() => ({ left: 0, top: 0, width: 800, height: 200, right: 800, bottom: 200 })) as never;

	const canvas = document.createElement('canvas');
	document.body.appendChild(canvas);

	const profile = createRaceProfile({ canvas, course: course(), editable: false });
	profile.setField({
		chainages: [100, 300, 500, 700],
		finished: [0, 0, 0, 0],
		colors: COLORS,
	});
	return { profile, fills };
}

/** The alpha a rider was painted with, by his colour. Dim = 0.25, bright = 0.9 (race-profile drawFrame). */
function alphaByColor(fills: Array<{ fillStyle: string; globalAlpha: number; radii: number[] }>) {
	const byColor = new Map<string, number>();
	for (const f of fills) {
		if (f.radii.length === 0) continue;
		if (COLORS.includes(f.fillStyle)) byColor.set(f.fillStyle, f.globalAlpha);
	}
	return byColor;
}

describe('RaceProfile filter', () => {
	beforeEach(() => {
		document.body.innerHTML = '';
	});

	it('dims everyone OUTSIDE the filtered set — the same contract the markers use', () => {
		const { profile, fills } = mount();

		// Riders 0 and 1 match the results-table filter. This is the array the page hands to
		// markers.setDimmed(), which keeps exactly these two bright.
		profile.setFilter({ bright: new Set([0, 1]) });
		profile.tick(true);

		const alpha = alphaByColor(fills);

		expect(alpha.get(must(COLORS[0], 'palette color 0'))).toBeCloseTo(0.9);   // matched → bright
		expect(alpha.get(must(COLORS[1], 'palette color 1'))).toBeCloseTo(0.9);
		expect(alpha.get(must(COLORS[2], 'palette color 2'))).toBeCloseTo(0.25);  // not matched → dim
		expect(alpha.get(must(COLORS[3], 'palette color 3'))).toBeCloseTo(0.25);
	});

	it('null clears the filter — the whole field is drawn full strength', () => {
		const { profile, fills } = mount();

		profile.setFilter({ bright: new Set([0]) });
		profile.setFilter({ bright: null });
		profile.tick(true);

		for (const color of COLORS) {
			expect(alphaByColor(fills).get(color)).toBeCloseTo(0.9);
		}
	});
});

/**
 * The place/drag path is now a `ProfileInteraction` the race hands to core/profile-chart: the chart says
 * "a press landed at 8.5 km, over checkpoint 2", and THIS decides whether that is a selection, a drag, a
 * placement or nothing. These pin that decision — the rules the server would otherwise have to reject.
 */
describe('RaceProfile placement and dragging', () => {
	/** Plot maths: an 800 px canvas inset 8 px each side over a 1 km course → x = 8 + (metres/1000)*784. */
	const xOf = (metres: number) => 8 + (metres / 1000) * 784;

	function checkpoints(): RaceCheckpointDto[] {
		return [
			{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: null, legColor: null },
			{ checkpointId: 2, seq: 1, name: 'Mid', chainageMeters: 500, node: 5, legName: 'Leg 1', legColor: '#ff0000' },
			{ checkpointId: 3, seq: 2, name: 'Finish', chainageMeters: 1000, node: 10, legName: null, legColor: null },
		];
	}

	function mountEditable(editable = true) {
		vi.stubGlobal('ResizeObserver', FakeResizeObserver);
		let handedOut = 0;
		HTMLCanvasElement.prototype.getContext = vi.fn(() =>
			(handedOut++ === 0 ? recordingContext().ctx : recordingContext().ctx)) as never;
		HTMLCanvasElement.prototype.getBoundingClientRect = vi.fn(
			() => ({ left: 0, top: 0, width: 800, height: 200, right: 800, bottom: 200 })) as never;

		const canvas = document.createElement('canvas');
		// jsdom does not implement pointer capture; the drag takes it to survive leaving the 10 px strip.
		canvas.setPointerCapture = vi.fn();
		canvas.hasPointerCapture = vi.fn(() => true);
		canvas.releasePointerCapture = vi.fn();
		document.body.appendChild(canvas);

		const onPlace = vi.fn();
		const onMove = vi.fn();
		const onSelect = vi.fn();
		const profile = createRaceProfile({ canvas, course: course(), editable, onPlace, onMove, onSelect });
		profile.setCheckpoints(checkpoints());
		return { profile, canvas, onPlace, onMove, onSelect };
	}

	const press = (canvas: HTMLCanvasElement, x: number) =>
		canvas.dispatchEvent(new MouseEvent('pointerdown', { clientX: x, clientY: 100, bubbles: true }));
	const move = (canvas: HTMLCanvasElement, x: number) =>
		canvas.dispatchEvent(new MouseEvent('pointermove', { clientX: x, clientY: 100, bubbles: true }));
	const release = (canvas: HTMLCanvasElement, x: number) =>
		canvas.dispatchEvent(new MouseEvent('pointerup', { clientX: x, clientY: 100, bubbles: true }));

	beforeEach(() => {
		document.body.innerHTML = '';
	});

	it('a click on an empty stretch places nothing until the editor arms it', () => {
		const { profile, canvas, onPlace } = mountEditable();

		press(canvas, xOf(250));
		expect(onPlace).not.toHaveBeenCalled();

		profile.setPlaceable(true);
		press(canvas, xOf(250));
		expect(onPlace).toHaveBeenCalledTimes(1);
		expect(must(onPlace.mock.calls[0], 'one place call')[0]).toBeCloseTo(250, 0);
	});

	it('refuses to place on top of an existing checkpoint — a zero-length leg is a divide by zero', () => {
		const { profile, canvas, onPlace } = mountEditable();
		profile.setPlaceable(true);

		press(canvas, xOf(510));   // 10 m from Mid, inside the 50 m gap
		expect(onPlace).not.toHaveBeenCalled();
	});

	it('a click on a checkpoint selects it', () => {
		const { canvas, onSelect, onPlace } = mountEditable();

		press(canvas, xOf(500));
		expect(onSelect).toHaveBeenCalledWith(2);
		expect(onPlace).not.toHaveBeenCalled();   // a hit is never also a placement
	});

	it('dragging a checkpoint writes ONCE, on release', () => {
		const { canvas, onMove } = mountEditable();

		press(canvas, xOf(500));
		move(canvas, xOf(400));
		move(canvas, xOf(300));
		expect(onMove).not.toHaveBeenCalled();   // the server hears nothing mid-drag

		release(canvas, xOf(300));
		expect(onMove).toHaveBeenCalledTimes(1);
		const moveCall = must(onMove.mock.calls[0], 'one move call');
		expect(moveCall[0]).toBe(2);
		expect(moveCall[1]).toBeCloseTo(300, 0);
	});

	// The endpoints USED to be undraggable, on the theory that they ARE the ride's ends. They are not: the
	// grid is the whole recorded ride and the race is a window on it, and pinning the Finish to a length
	// taken from the course ACTIVITY (a different metric) once left it 337 m inside a 70 km course with no
	// way to drag it back. Both lines move now, bounded by their neighbours and the grid.
	it('the Start drags into the ride, up to the first checkpoint', () => {
		const { canvas, onSelect, onMove } = mountEditable();

		press(canvas, xOf(0));
		expect(onSelect).toHaveBeenCalledWith(1);
		move(canvas, xOf(400));
		release(canvas, xOf(400));

		expect(onMove).toHaveBeenCalledTimes(1);
		const moved = must(onMove.mock.calls[0], 'one move call');
		expect(moved[0]).toBe(1);
		expect(moved[1]).toBeCloseTo(400, 0);
	});

	it('the Finish drags back off the end of the ride', () => {
		const { canvas, onSelect, onMove } = mountEditable();

		press(canvas, xOf(1000));
		expect(onSelect).toHaveBeenLastCalledWith(3);
		move(canvas, xOf(600));
		release(canvas, xOf(600));

		expect(onMove).toHaveBeenCalledTimes(1);
		const moved = must(onMove.mock.calls[0], 'one move call');
		expect(moved[0]).toBe(3);
		expect(moved[1]).toBeCloseTo(600, 0);
	});

	it('an endpoint still cannot cross its neighbour — it would stop being the endpoint', () => {
		const { canvas, onMove } = mountEditable();

		press(canvas, xOf(1000));
		move(canvas, xOf(300));    // past Mid at 500 — every position refused
		release(canvas, xOf(300));

		expect(onMove).not.toHaveBeenCalled();
	});

	it('a drag that never found a legal spot writes nothing', () => {
		const { canvas, onMove } = mountEditable();

		press(canvas, xOf(500));
		move(canvas, xOf(10));    // inside the start's min-gap — every position refused
		release(canvas, xOf(10));

		expect(onMove).not.toHaveBeenCalled();
	});

	it('a read-only strip has no write path at all — the share fork and the viewer', () => {
		const { profile, canvas, onPlace, onMove, onSelect } = mountEditable(false);
		profile.setPlaceable(true);

		press(canvas, xOf(500));
		move(canvas, xOf(300));
		release(canvas, xOf(300));
		press(canvas, xOf(250));

		expect(onSelect).not.toHaveBeenCalled();
		expect(onMove).not.toHaveBeenCalled();
		expect(onPlace).not.toHaveBeenCalled();
	});
});

// RF-03: a single hover pointermove used to redraw the profile TWICE — once via the coordinator routing
// onHover back to this chart's setHover, and once via onPointerMove's own drawFrame(). setHover is now
// idempotent for the firing chart, so exactly one redraw happens per hover move.
describe('RF-03 — hover redraws the profile once, not twice', () => {
	it('one hover pointermove clears the canvas once', () => {
		vi.stubGlobal('ResizeObserver', FakeResizeObserver);
		let clears = 0;
		const rec = recordingContext();
		(rec.ctx as { clearRect: () => void }).clearRect = () => { clears++; };
		let handedOut = 0;
		HTMLCanvasElement.prototype.getContext = vi.fn(() =>
			(handedOut++ === 0 ? rec.ctx : recordingContext().ctx)) as never;
		HTMLCanvasElement.prototype.getBoundingClientRect = vi.fn(
			() => ({ left: 0, top: 0, width: 800, height: 200, right: 800, bottom: 200 })) as never;

		const canvas = document.createElement('canvas');
		document.body.appendChild(canvas);

		// Mirror race-charts.syncHover: a chart's onHover routes back to its own setHover.
		const holder: { p?: ReturnType<typeof createRaceProfile> } = {};
		holder.p = createRaceProfile({
			canvas, course: course(), editable: false,
			onHover: (m) => holder.p!.setHover(m),
		});
		holder.p.setField({ chainages: [100, 300, 500, 700], finished: [0, 0, 0, 0], colors: COLORS });

		clears = 0; // ignore the setup draws
		canvas.dispatchEvent(new MouseEvent('pointermove', { clientX: 400, clientY: 100, bubbles: true }));

		expect(clears).toBe(1);
	});
});

/**
 * The ride outside Start..Finish. It is drawn — dimmed — on both the profile and the map, and both read
 * this one function, so they cannot disagree about what is out of play.
 *
 * On the profile these are not decoration: paintTerrain fills ONCE PER BAND, clipped to the band's span,
 * so a stretch with no band is not drawn at all. Without them, moving an endpoint inward would erase that
 * end of the terrain instead of dimming it.
 */
describe('outOfRaceSpans', () => {
	const cp = (id: number, chainageMeters: number): RaceCheckpointDto => ({
		checkpointId: id, seq: id, name: null, chainageMeters,
		node: Math.round(chainageMeters / 100), legName: null, legColor: null,
	});

	it('is empty when the race spans the whole ride', () => {
		expect(outOfRaceSpans([cp(1, 0), cp(2, 1000)], 1000)).toEqual([]);
	});

	it('returns the roll-out and the cooldown, uncoloured', () => {
		expect(outOfRaceSpans([cp(1, 120), cp(2, 900)], 1000)).toEqual([
			{ fromMetres: 0, toMetres: 120, color: OUT_OF_RACE_COLOR, label: null },
			{ fromMetres: 900, toMetres: 1000, color: OUT_OF_RACE_COLOR, label: null },
		]);
	});

	it('drops a span too thin to draw — the map wants a whole grid step', () => {
		expect(outOfRaceSpans([cp(1, 4), cp(2, 997)], 1000, 100)).toEqual([]);
	});
});

/**
 * The field-speed aggregate, as the profile draws it. It was a second stacked canvas until it became a
 * trace on this strip, and the conversion is where the risk moved: bins are SPARSE — a stretch nobody
 * rode files none — so the x positions have to travel with the values instead of being implied by a step.
 */
describe('speedTrace', () => {
	function bin(distanceMeters: number, median: number): FieldSpeedBin {
		return { distanceMeters, p10: median - 5, median, p90: median + 8, count: 3 };
	}

	function profile(bins: FieldSpeedBin[]): FieldSpeedProfile {
		return { bins, totalMeters: 1000, maxSpeedKmh: Math.max(0, ...bins.map((b) => b.p90)) };
	}

	it('carries each bin’s own distance, so a gap in the field does not shift the line', () => {
		// 250 and 750 are filed; the stretch between them was never ridden and files nothing.
		const trace = must(speedTrace(profile([bin(250, 30), bin(750, 18)])), 'a trace');

		expect(Array.from(trace.x)).toEqual([250, 750]);
		expect(Array.from(trace.y)).toEqual([30, 18]);
		expect(trace.color).toBe(PROFILE_TRACE_COLOR);
	});

	it('splits p10/p90 into the band, index-aligned with the median line', () => {
		const trace = must(speedTrace(profile([bin(250, 30), bin(750, 18)])), 'a trace');
		const band = must(trace.band, 'a spread band');

		expect(Array.from(band.low)).toEqual([25, 13]);
		expect(Array.from(band.high)).toEqual([38, 26]);
	});

	it('declines a profile it cannot draw a line from — one bin is a point, not a trace', () => {
		expect(speedTrace(null)).toBeNull();
		expect(speedTrace(profile([]))).toBeNull();
		expect(speedTrace(profile([bin(500, 25)]))).toBeNull();
	});
});
