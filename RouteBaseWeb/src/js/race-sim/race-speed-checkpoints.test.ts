/**
 * The gesture's contract: what a press, a drag and a release DO to the checkpoint editor — and what they
 * refuse to do. The rules themselves belong to `race-clock.ts` and are tested there; what is pinned here
 * is that this surface obeys them, that a placement needs arming, and that a drag over a boundary takes
 * the gesture away from the chart's own pan instead of fighting it.
 *
 * `chart-zoom-pan.setPanYield` is spied rather than mocked away: the predicate it registers has to give
 * the SAME answer as the drag's own hit-test, and a press that stops the pan but then misses the drag is
 * a dead gesture. Testing the two against each other is the point.
 */
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import type { RaceCheckpointDto } from './race-api';

const H = vi.hoisted(() => ({
	yields: [] as Array<((pixelX: number) => boolean) | null>,
	cursors: [] as Array<string | null>,
}));

vi.mock('../core/chart-zoom-pan', () => ({
	setPanYield: (_canvas: HTMLCanvasElement, predicate: ((pixelX: number) => boolean) | null) => {
		H.yields.push(predicate);
	},
	setPanIdleCursor: (_canvas: HTMLCanvasElement, cursor: string | null) => {
		H.cursors.push(cursor);
	},
}));

import { createRaceSpeedCheckpoints } from './race-speed-checkpoints';

const TOTAL = 70_000;
/** The x-scale maps 0–100 % onto 0–1000 px, so a percentage reads as ten times itself. */
const PX_PER_PERCENT = 10;

/** Metres → the canvas x they sit at, under the fake scale below. */
function xOf(metres: number): number {
	return (metres / TOTAL) * 100 * PX_PER_PERCENT;
}

function cp(over: Partial<RaceCheckpointDto> & { checkpointId: number; chainageMeters: number }): RaceCheckpointDto {
	return { seq: 0, name: '', node: 0, legName: null, legColor: null, ...over };
}

let canvas: HTMLCanvasElement;
let checkpoints: RaceCheckpointDto[];
let onPlace: Mock<(chainageMeters: number) => void>;
let onMove: Mock<(checkpointId: number, chainageMeters: number) => void>;
let onSelect: Mock<(checkpointId: number) => void>;
let onLocalMove: Mock<(checkpointId: number, chainageMeters: number) => void>;

const chart = () => ({
	chartArea: { left: 0, right: 1000, top: 0, bottom: 200 },
	scales: {
		x: {
			getPixelForValue: (v: number) => v * PX_PER_PERCENT,
			getValueForPixel: (p: number) => p / PX_PER_PERCENT,
		},
	},
} as never);

function press(x: number): void {
	canvas.dispatchEvent(new PointerEvent('pointerdown', { clientX: x, button: 0, pointerId: 1, bubbles: true }));
}
function moveTo(x: number): void {
	canvas.dispatchEvent(new PointerEvent('pointermove', { clientX: x, pointerId: 1, bubbles: true }));
}
function release(x: number): void {
	canvas.dispatchEvent(new PointerEvent('pointerup', { clientX: x, pointerId: 1, bubbles: true }));
}
/** Press, drag and release in one go. */
function drag(from: number, to: number): void {
	press(from);
	moveTo(to);
	release(to);
}

function subject(over: { editable?: boolean } = {}) {
	return createRaceSpeedCheckpoints({
		canvas,
		chart,
		totalMeters: TOTAL,
		checkpoints: () => checkpoints,
		editable: over.editable ?? true,
		onPlace,
		onMove,
		onSelect,
		onLocalMove,
	});
}

beforeEach(() => {
	H.yields = [];
	H.cursors = [];
	onPlace = vi.fn();
	onMove = vi.fn();
	onSelect = vi.fn();
	// The editor's draft is what a drag writes into; mirroring it here is what makes a release read the
	// LIVE position rather than the one the press captured.
	onLocalMove = vi.fn((id: number, metres: number) => {
		checkpoints = checkpoints.map((c) => (c.checkpointId === id ? { ...c, chainageMeters: metres } : c));
	});
	checkpoints = [
		cp({ checkpointId: 1, chainageMeters: 0, name: 'Start' }),
		cp({ checkpointId: 2, chainageMeters: 35_000, name: 'Raimartihof' }),
		cp({ checkpointId: 3, chainageMeters: TOTAL, name: 'Finish' }),
	];
	canvas = document.createElement('canvas');
	// jsdom gives every element a zero rect, so a clientX IS the canvas-relative x.
	document.body.append(canvas);
});

afterEach(() => {
	document.body.replaceChildren();
	vi.restoreAllMocks();
});

describe('placing', () => {
	it('does nothing until the Sections editor arms it', () => {
		subject();

		press(xOf(20_000));
		release(xOf(20_000));

		expect(onPlace).not.toHaveBeenCalled();
	});

	it('places at the clicked chainage once armed', () => {
		const layer = subject();
		layer.setPlaceable(true);

		press(xOf(20_000));
		release(xOf(20_000));

		expect(onPlace).toHaveBeenCalledTimes(1);
		expect(onPlace.mock.calls[0]?.[0]).toBeCloseTo(20_000, 6);
	});

	it('refuses a spot on top of an existing boundary — that is a zero-length leg for the pacing model', () => {
		const layer = subject();
		layer.setPlaceable(true);

		// 20 m from the Raimartihof line, well inside race-clock's MIN_GAP_METRES.
		press(xOf(35_020));
		release(xOf(35_020));

		expect(onPlace).not.toHaveBeenCalled();
	});

	it('does not place when the press travelled — that is the chart being panned', () => {
		const layer = subject();
		layer.setPlaceable(true);

		drag(xOf(20_000), xOf(24_000));

		expect(onPlace).not.toHaveBeenCalled();
	});

	it('never places for a viewer, however armed the layer is', () => {
		const layer = subject({ editable: false });
		layer.setPlaceable(true);

		press(xOf(20_000));
		release(xOf(20_000));

		expect(onPlace).not.toHaveBeenCalled();
	});
});

describe('selecting', () => {
	it('reports the boundary a press landed on', () => {
		subject();

		press(xOf(35_000));

		expect(onSelect).toHaveBeenCalledWith(2);
	});

	it('reports it for a viewer too — a selection is a read, not a write', () => {
		subject({ editable: false });

		press(xOf(35_000));

		expect(onSelect).toHaveBeenCalledWith(2);
		expect(onMove).not.toHaveBeenCalled();
	});

	it('says nothing when the press is on open course', () => {
		subject();

		press(xOf(20_000));

		expect(onSelect).not.toHaveBeenCalled();
	});
});

describe('dragging', () => {
	it('previews locally on the way and commits once on release', () => {
		subject();

		press(xOf(35_000));
		moveTo(xOf(38_000));
		moveTo(xOf(41_000));
		release(xOf(41_000));

		expect(onLocalMove).toHaveBeenCalledTimes(2);
		expect(onMove).toHaveBeenCalledTimes(1);
		expect(onMove.mock.calls[0]?.[0]).toBe(2);
		expect(onMove.mock.calls[0]?.[1]).toBeCloseTo(41_000, 6);
	});

	it('commits where the boundary actually ENDED, not where the pointer was released', () => {
		subject();

		press(xOf(35_000));
		moveTo(xOf(41_000));
		// Past the Finish: race-clock refuses it, so the boundary stays at 41 km and the release must
		// report that — reporting the pointer would write a chainage the server would reject.
		moveTo(xOf(TOTAL + 5_000));
		release(xOf(TOTAL + 5_000));

		expect(onMove.mock.calls[0]?.[1]).toBeCloseTo(41_000, 6);
	});

	it('writes nothing when every position on the way was refused', () => {
		// Boxed in: two neighbours a hair either side, so no legal spot exists at all.
		checkpoints = [
			cp({ checkpointId: 1, chainageMeters: 0 }),
			cp({ checkpointId: 9, chainageMeters: 34_960 }),
			cp({ checkpointId: 2, chainageMeters: 35_000 }),
			cp({ checkpointId: 10, chainageMeters: 35_040 }),
			cp({ checkpointId: 3, chainageMeters: TOTAL }),
		];
		subject();

		drag(xOf(35_000), xOf(35_010));

		expect(onLocalMove).not.toHaveBeenCalled();
		expect(onMove).not.toHaveBeenCalled();
	});

	it('writes nothing for a press that never moved — that is a selection', () => {
		subject();

		press(xOf(35_000));
		release(xOf(35_000));

		expect(onMove).not.toHaveBeenCalled();
		expect(onSelect).toHaveBeenCalledWith(2);
	});

	it('drops a drag the system cancelled instead of leaving it half-open', () => {
		subject();

		press(xOf(35_000));
		canvas.dispatchEvent(new PointerEvent('pointercancel', { pointerId: 1, bubbles: true }));
		moveTo(xOf(50_000));

		expect(onLocalMove).not.toHaveBeenCalled();
	});

	it('ignores a press outside the plot — that is axis furniture, not the course', () => {
		subject().setPlaceable(true);

		press(1_400);
		release(1_400);

		expect(onPlace).not.toHaveBeenCalled();
		expect(onSelect).not.toHaveBeenCalled();
	});
});

describe('living with the chart\'s own pan', () => {
	it('registers a yield predicate that claims exactly the presses the drag will take', () => {
		subject();
		const predicate = H.yields.at(-1);

		expect(predicate?.(xOf(35_000))).toBe(true);    // on the Raimartihof rule
		expect(predicate?.(xOf(20_000))).toBe(false);   // open course — the chart may pan
	});

	it('claims nothing for a viewer, who has no drag to protect', () => {
		subject({ editable: false });

		expect(H.yields.at(-1)?.(xOf(35_000))).toBe(false);
	});

	it('hands the canvas back on destroy, so a dead closure cannot keep claiming presses', () => {
		const layer = subject();

		layer.destroy();

		expect(H.yields.at(-1)).toBeNull();
		expect(H.cursors.at(-1)).toBeNull();
	});

	it('claims the crosshair through the pan seam, which is what makes it survive a mousemove', () => {
		const layer = subject();

		layer.setPlaceable(true);
		layer.setPlaceable(false);

		// Pan rewrites canvas.style.cursor on every mousemove; an inline one would last a twitch.
		expect(H.cursors).toEqual(['crosshair', null]);
	});

	it('stops listening on destroy', () => {
		const layer = subject();
		layer.setPlaceable(true);

		layer.destroy();
		press(xOf(20_000));
		release(xOf(20_000));

		expect(onPlace).not.toHaveBeenCalled();
	});
});
