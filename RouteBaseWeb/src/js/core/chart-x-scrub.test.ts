/**
 * The four guards this module exists to hold — throttle, same-pixel dedupe, pan suppression and
 * abort-on-reattach — are each a bug that was already fixed once in `chart-factory` and once in
 * `segment-stats-charts-readout`. Pinned here so the shared copy cannot lose one of them.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { attachChartXScrub, detachChartXScrub } from './chart-x-scrub';
import { setChartPanning } from './chart-pan-state';
import { CHART_HOVER_THROTTLE_MS } from './constants';
import type { ChartJsInstance } from './core-types';

let canvas: HTMLCanvasElement;
let now = 1000;

/** Enough of a chart for the scrub: a canvas back-reference and a plot area. */
function fakeChart(): ChartJsInstance {
	return {
		canvas,
		chartArea: { top: 0, bottom: 100, left: 0, right: 200 },
		scales: { x: { getValueForPixel: (p: number) => p } },
	} as unknown as ChartJsInstance;
}

function move(clientX: number): void {
	canvas.dispatchEvent(new MouseEvent('mousemove', { clientX, clientY: 5, bubbles: true }));
}

/** Past the time-throttle, so the next move is judged on its own merits. */
function tick(): void {
	now += CHART_HOVER_THROTTLE_MS + 1;
}

beforeEach(() => {
	now = 1000;
	vi.spyOn(performance, 'now').mockImplementation(() => now);
	canvas = document.createElement('canvas');
	document.body.append(canvas);
	setChartPanning(false);
});

afterEach(() => {
	detachChartXScrub(canvas);
	setChartPanning(false);
	vi.restoreAllMocks();
	document.body.replaceChildren();
});

describe('attachChartXScrub', () => {
	it('reports the pointer x, in canvas pixels', () => {
		const onScrub = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub });

		move(42);

		expect(onScrub).toHaveBeenCalledTimes(1);
		expect(onScrub.mock.calls[0]?.[0]).toBe(42);
	});

	it('throttles: a second move inside the window is dropped', () => {
		const onScrub = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub });

		move(10);
		move(20);   // same frame — too soon

		expect(onScrub).toHaveBeenCalledTimes(1);
	});

	it('lets the move through once the window has passed', () => {
		const onScrub = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub });

		move(10);
		tick();
		move(20);

		expect(onScrub).toHaveBeenCalledTimes(2);
	});

	it('skips a move that lands on the same x-pixel — the crosshair is x-only', () => {
		const onScrub = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub });

		move(10);
		tick();
		move(10.4);   // rounds to the same pixel

		expect(onScrub).toHaveBeenCalledTimes(1);
	});

	it('does not let a stationary cursor starve the next real move of its slot', () => {
		// The dedupe is checked BEFORE the timestamp is taken; if it were after, the suppressed
		// same-pixel event would reset the clock and swallow the move that follows it.
		const onScrub = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub });

		move(10);
		tick();
		move(10);       // same pixel — suppressed, and must NOT restamp the throttle
		move(25);       // still inside the window, but the clock was not reset by the line above

		expect(onScrub).toHaveBeenCalledTimes(2);
		expect(onScrub.mock.calls[1]?.[0]).toBe(25);
	});

	it('stays quiet while the chart is being panned', () => {
		const onScrub = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub });
		setChartPanning(true);

		move(42);

		expect(onScrub).not.toHaveBeenCalled();
	});

	it('stays quiet when there is no chart yet', () => {
		const onScrub = vi.fn();
		attachChartXScrub(canvas, { getChart: () => null, onScrub });

		move(42);

		expect(onScrub).not.toHaveBeenCalled();
	});

	it('follows a single finger', () => {
		const onScrub = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub });

		canvas.dispatchEvent(new TouchEvent('touchstart', {
			touches: [{ clientX: 33, clientY: 5 } as unknown as Touch], bubbles: true,
		}));

		expect(onScrub).toHaveBeenCalledWith(33, expect.anything());
	});

	it('ignores a two-finger touch — that gesture belongs to the pinch handler', () => {
		const onScrub = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub });

		canvas.dispatchEvent(new TouchEvent('touchmove', {
			touches: [
				{ clientX: 33, clientY: 5 } as unknown as Touch,
				{ clientX: 66, clientY: 5 } as unknown as Touch,
			],
			bubbles: true,
		}));

		expect(onScrub).not.toHaveBeenCalled();
	});

	it('calls onLeave when the mouse goes, and redraws on re-entry at the same pixel', () => {
		const onScrub = vi.fn();
		const onLeave = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub, onLeave });

		move(10);
		canvas.dispatchEvent(new MouseEvent('mouseleave', { bubbles: true }));
		tick();
		move(10);   // same pixel as before — but the box is gone, so it must be drawn again

		expect(onLeave).toHaveBeenCalledTimes(1);
		expect(onScrub).toHaveBeenCalledTimes(2);
	});

	it('aborts the previous listeners when a rebuilt chart re-attaches', () => {
		const first = vi.fn();
		const second = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub: first });
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub: second });

		move(42);

		expect(first).not.toHaveBeenCalled();
		expect(second).toHaveBeenCalledTimes(1);
	});

	it('goes silent after detach', () => {
		const onScrub = vi.fn();
		attachChartXScrub(canvas, { getChart: fakeChart, onScrub });

		detachChartXScrub(canvas);
		move(42);

		expect(onScrub).not.toHaveBeenCalled();
	});
});
