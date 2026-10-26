import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { GesturePreviewGroup, attachWheelZoom } from './chart-gesture-preview';
import { isChartPanning } from './chart-pan-state';

// PS-08 / PS-11 pin for the zoom pipeline. Fixed-resolution ("picture-like") zoom
// relies on the gesture applying exactly ONE real render per chart at gesture end
// (chart.zoomScale) and NEVER a chart.update during preview frames — the preview is
// a scaled bitmap. If the gesture ever re-rendered mid-preview, the decimation plugin
// would re-run per frame and re-introduce the lag PS-11 removes. This locks that
// single-apply invariant before the fixed-resolution decimation switch lands.
//
// ID-15 adds the mode axis (perFrame / overscan snapshot) — pinned in its own describe below.

type FakeChart = {
	chartArea: { left: number; top: number; right: number; bottom: number };
	canvas: HTMLCanvasElement;
	ctx: CanvasRenderingContext2D | null;
	scales: { x: { min: number; max: number } };
	zoomScale: ReturnType<typeof vi.fn>;
	update: ReturnType<typeof vi.fn>;
};

// Minimal fake Chart.js chart recording zoomScale/update; carries the fields the
// group's snapshot + apply read (chartArea, canvas, scales.x, ctx). zoomScale mutates
// scales.x so overscan capture reads back the "rendered" range (ID-15).
function fakeChart(): FakeChart {
	const c: FakeChart = {
		chartArea: { left: 0, top: 0, right: 100, bottom: 100 },
		canvas: document.createElement('canvas'),
		ctx: null, // jsdom getContext → null; _drawPreview no-ops, irrelevant to these pins
		scales: { x: { min: 0, max: 100 } },
		zoomScale: vi.fn(),
		update: vi.fn(),
	};
	c.zoomScale.mockImplementation((_axis: string, range: { min: number; max: number }) => {
		c.scales.x.min = range.min;
		c.scales.x.max = range.max;
	});
	return c;
}

// Queue-based rAF so tests can flush preview frames on demand. Existing sync end(0) tests
// never flush (cb stays queued), so their behavior is unchanged; the ID-15 tests flush.
let rafQueue: FrameRequestCallback[] = [];
function flushRaf(): void {
	const q = rafQueue;
	rafQueue = [];
	for (const cb of q) cb(0);
}

beforeEach(() => {
	rafQueue = [];
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(globalThis as any).requestAnimationFrame = (cb: FrameRequestCallback): number => { rafQueue.push(cb); return rafQueue.length; };
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(globalThis as any).cancelAnimationFrame = (): void => {};
});

afterEach(() => {
	vi.restoreAllMocks();
});

describe('GesturePreviewGroup — PS-08 zoom-pipeline pin', () => {
	it('gesture end applies exactly one zoomScale("x", range, "none") per chart; preview frames never call chart.update', () => {
		const charts = [fakeChart(), fakeChart()];
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const group = new GesturePreviewGroup(() => charts as any);

		group.begin();
		group.preview(10, 90);
		group.preview(20, 80);
		group.preview(30, 70); // last pending range wins
		group.end(0);          // synchronous apply

		for (const c of charts) {
			expect(c.zoomScale).toHaveBeenCalledTimes(1);
			expect(c.zoomScale).toHaveBeenCalledWith('x', { min: 30, max: 70 }, 'none');
			expect(c.update).not.toHaveBeenCalled();
		}
	});

	// ID-14 pin: beforeApply must run per chart BEFORE its zoomScale (chart-factory re-decimates
	// the dataset for the final range so the single apply render draws the right data — plugin
	// zoomScale fires no onZoom); afterApply exactly once, after every chart applied.
	it('apply hooks: beforeApply per chart before its zoomScale, afterApply once after all', () => {
		const charts = [fakeChart(), fakeChart()];
		const calls: string[] = [];
		charts.forEach((c, i) => c.zoomScale.mockImplementation(() => calls.push(`zoom${i}`)));
		const group = new GesturePreviewGroup(
			// eslint-disable-next-line @typescript-eslint/no-explicit-any
			() => charts as any,
			{
				// eslint-disable-next-line @typescript-eslint/no-explicit-any
				beforeApply: (chart: any, range) => {
					calls.push(`before${charts.indexOf(chart)}`);
					expect(range).toEqual({ min: 30, max: 70 });
				},
				afterApply: (range) => {
					calls.push('after');
					expect(range).toEqual({ min: 30, max: 70 });
				},
			},
		);

		group.begin();
		group.preview(30, 70);
		group.end(0);

		expect(calls).toEqual(['before0', 'zoom0', 'before1', 'zoom1', 'after']);
	});

	it('apply hooks do not fire when the gesture ends without a preview (plain click)', () => {
		const chart = fakeChart();
		const beforeApply = vi.fn();
		const afterApply = vi.fn();
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const group = new GesturePreviewGroup(() => [chart] as any, { beforeApply, afterApply });

		group.begin();
		group.end(0); // no preview() → no pending range → nothing to render

		expect(beforeApply).not.toHaveBeenCalled();
		expect(afterApply).not.toHaveBeenCalled();
		expect(chart.zoomScale).not.toHaveBeenCalled();
	});
});

describe('GesturePreviewGroup — ID-15 gesture modes', () => {
	// perFrame (aggregate): no snapshot; every preview frame is a real zoomScale render.
	it('perFrame mode renders live per frame (real zoomScale on each flushed preview)', () => {
		const charts = [fakeChart(), fakeChart()];
		const group = new GesturePreviewGroup(
			// eslint-disable-next-line @typescript-eslint/no-explicit-any
			() => charts as any, undefined, { getMode: () => ({ perFrame: true }) },
		);

		group.begin();
		group.preview(25, 75);
		flushRaf(); // the preview frame fires
		for (const c of charts) {
			expect(c.zoomScale).toHaveBeenCalledWith('x', { min: 25, max: 75 }, 'none');
		}
		group.end(0);
		// one live render + one apply render = 2 total; never a chart.update
		for (const c of charts) {
			expect(c.zoomScale).toHaveBeenCalledTimes(2);
			expect(c.update).not.toHaveBeenCalled();
		}
	});

	// Overscan (individual): begin renders NOTHING (lazy); the first preview frame renders the
	// chart once at overscan× the pending range, then subsequent frames inside the buffer draw the
	// bitmap only (no further zoomScale until the apply).
	it('overscan mode: lazy snapshot renders once at the wider range; in-buffer pans add no renders', () => {
		const chart = fakeChart(); // starts at [0,100]
		const group = new GesturePreviewGroup(
			// eslint-disable-next-line @typescript-eslint/no-explicit-any
			() => [chart] as any, undefined, { getMode: () => ({ perFrame: false, overscan: 3 }) },
		);
		// Simulate a zoomed-in view before panning.
		chart.scales.x = { min: 40, max: 60 };

		group.begin();
		expect(chart.zoomScale).not.toHaveBeenCalled(); // lazy — nothing at begin

		group.preview(41, 61); // first frame → overscan snapshot render
		flushRaf();
		// overscan 3× of a width-20 view around [41,61] = pad 20 → [21, 81]
		expect(chart.zoomScale).toHaveBeenCalledTimes(1);
		expect(chart.zoomScale).toHaveBeenLastCalledWith('x', { min: 21, max: 81 }, 'none');

		group.preview(45, 65); // still inside [21,81] → bitmap only, no new render
		flushRaf();
		expect(chart.zoomScale).toHaveBeenCalledTimes(1);
	});

	// Panning past the overscan buffer forces a re-snapshot (one more render).
	it('overscan mode: re-snapshots when the pan exceeds the buffer', () => {
		const chart = fakeChart();
		const group = new GesturePreviewGroup(
			// eslint-disable-next-line @typescript-eslint/no-explicit-any
			() => [chart] as any, undefined, { getMode: () => ({ perFrame: false, overscan: 3 }) },
		);
		chart.scales.x = { min: 40, max: 60 };

		group.begin();
		group.preview(41, 61); // snapshot buffer → [21, 81]
		flushRaf();
		expect(chart.zoomScale).toHaveBeenCalledTimes(1);

		group.preview(85, 105); // beyond [21,81] → re-snapshot
		flushRaf();
		expect(chart.zoomScale).toHaveBeenCalledTimes(2);

		group.end(0); // final crisp apply
		expect(chart.zoomScale).toHaveBeenCalledTimes(3);
		expect(chart.zoomScale).toHaveBeenLastCalledWith('x', { min: 85, max: 105 }, 'none');
	});

	// A plain click in overscan mode must render nothing (lazy snapshot never triggered).
	it('overscan mode: plain click (begin+end, no move) renders nothing', () => {
		const chart = fakeChart();
		const group = new GesturePreviewGroup(
			// eslint-disable-next-line @typescript-eslint/no-explicit-any
			() => [chart] as any, undefined, { getMode: () => ({ perFrame: false, overscan: 3 }) },
		);
		group.begin();
		group.end(0);
		expect(chart.zoomScale).not.toHaveBeenCalled();
	});
});

// CG-02 + CG-07: the gesture contract the whole ID-14/ID-15 design rests on — attachWheelZoom
// anchor/clamp math, the full-extent early-return, the begin-during-debounce continuation, and
// cancel() semantics. fakeChart.zoomScale mutates scales.x, so the applied range is read back off it.
describe('attachWheelZoom + gesture contract (CG-02 / CG-07)', () => {
	beforeEach(() => {
		vi.useFakeTimers();
		// preview() schedules an rAF (bitmap draw) we never need here — pending is set synchronously,
		// so the apply reads it without a frame. Keep a queue stub so rAF doesn't throw.
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		(globalThis as any).requestAnimationFrame = (): number => 1;
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		(globalThis as any).cancelAnimationFrame = (): void => {};
	});
	afterEach(() => {
		vi.useRealTimers();
		vi.restoreAllMocks();
	});

	function wheel(canvas: HTMLElement, deltaY: number, clientX: number): void {
		canvas.dispatchEvent(new WheelEvent('wheel', { deltaY, clientX, cancelable: true }));
	}

	it('CG-02: wheel zoom-out is clamped by getBounds, not the mutated axis options', () => {
		const chart = fakeChart();
		chart.scales.x = { min: 40, max: 60 }; // post-zoom-in window
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const group = new GesturePreviewGroup(() => [chart] as any);
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		attachWheelZoom(chart.canvas, () => chart as any, group, () => ({ min: 0, max: 100 }));

		wheel(chart.canvas, 120, 50); // zoom OUT, cursor at plot center
		vi.advanceTimersByTime(200);   // fire the debounced apply

		expect(chart.zoomScale).toHaveBeenCalledTimes(1);
		expect(chart.scales.x.max - chart.scales.x.min).toBeGreaterThan(20); // widened past [40,60]
		expect(chart.scales.x.min).toBeGreaterThanOrEqual(0);
		expect(chart.scales.x.max).toBeLessThanOrEqual(100);
	});

	it('CG-07: wheel zoom-in anchors the value under the cursor', () => {
		const chart = fakeChart(); // [0,100]
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const group = new GesturePreviewGroup(() => [chart] as any);
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		attachWheelZoom(chart.canvas, () => chart as any, group, () => ({ min: 0, max: 100 }));

		wheel(chart.canvas, -120, 50); // zoom IN at center
		vi.advanceTimersByTime(200);

		const span = chart.scales.x.max - chart.scales.x.min;
		expect(span).toBeCloseTo(90, 5); // one 0.9× step
		const frac = (50 - chart.scales.x.min) / span;
		expect(frac).toBeCloseTo(0.5, 5); // 50 stayed under the cursor
	});

	it('CG-07: wheel at full extent zooming out does not start a gesture', () => {
		const chart = fakeChart(); // [0,100], already full
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const group = new GesturePreviewGroup(() => [chart] as any);
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		attachWheelZoom(chart.canvas, () => chart as any, group, () => ({ min: 0, max: 100 }));

		wheel(chart.canvas, 120, 50); // zoom OUT at full extent
		vi.advanceTimersByTime(200);

		expect(chart.zoomScale).not.toHaveBeenCalled();
		expect(group.isActive).toBe(false); // begin() never ran
	});

	it('CG-07: begin during the apply-debounce continues the gesture (one apply, latest range)', () => {
		const chart = fakeChart();
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const group = new GesturePreviewGroup(() => [chart] as any);

		group.begin();
		group.preview(10, 90);
		group.end(150);              // deferred apply scheduled
		vi.advanceTimersByTime(100); // …not yet fired
		group.begin();               // continuation cancels the pending apply
		group.preview(20, 80);
		group.end(0);                // synchronous apply of the newest range
		vi.advanceTimersByTime(200); // the original 150ms timer must NOT also fire

		expect(chart.zoomScale).toHaveBeenCalledTimes(1);
		expect(chart.zoomScale).toHaveBeenCalledWith('x', { min: 20, max: 80 }, 'none');
	});

	it('CG-07: cancel discards the pending range and clears the panning flag', () => {
		const chart = fakeChart();
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		const group = new GesturePreviewGroup(() => [chart] as any);

		group.begin();
		group.preview(30, 70);
		group.cancel();
		vi.advanceTimersByTime(200);

		expect(chart.zoomScale).not.toHaveBeenCalled();
		expect(isChartPanning()).toBe(false);
	});
});
