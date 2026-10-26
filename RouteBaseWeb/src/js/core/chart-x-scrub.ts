/**
 * chart-x-scrub.ts — the throttled mouse+touch x-scrub every hoverable chart needs.
 *
 * ## Why a chart must not use Chart.js's own hover
 *
 * Chart.js's `onHover` fires inside its native event → update → render → draw cycle, so every
 * mousemove repaints every line. On a multi-series chart that reads as FLICKER, and it is why each
 * chart here sets `events: ["mouseout"]` and drives the hover itself: a direct DOM listener, a
 * binary search, and an overlay-canvas crosshair that never touches the chart.
 *
 * What that costs is ~40 lines of listener bookkeeping, and it had been written twice — in
 * `core/chart-factory.ts` (activity details) and `segment-stats/segment-stats-charts-readout.ts`.
 * The race speed chart was the third, so the shared half lives here now (rule of three). Both
 * existing copies still hold their own; they are migration candidates, not settled design.
 *
 * ## What is shared, and what is not
 *
 * Shared — and all of it easy to get subtly wrong:
 *   • abort the canvas's PREVIOUS listeners, because charts are rebuilt on the same canvas
 *   • suppress the scrub mid-pan (`isChartPanning`), or a drag fights the crosshair
 *   • throttle on time, and skip a move that lands on the same x-PIXEL — the bar is x-only
 *   • mirror the whole path onto touch, passive, single-finger only (Chart.js fires no onHover on
 *     touchmove at all, which is what leaves a mobile readout frozen while the bar follows the finger)
 *
 * Not shared: what the x MEANS. One caller maps it to a GPS index, another to a dataset element,
 * this one to a rider's speed. So the callback hands over a pixel and gets out of the way.
 */

import { CHART_HOVER_THROTTLE_MS } from './constants';
import { isChartPanning } from './chart-pan-state';
import type { ChartJsInstance } from './core-types';

export interface ChartXScrubOptions {
	/** Read live: the chart is rebuilt on this canvas, and the listeners outlive any one instance. */
	readonly getChart: () => ChartJsInstance | null;
	/** Pointer is at `pixelX` CSS px from the canvas's left edge. Already throttled and de-duped. */
	readonly onScrub: (pixelX: number, chart: ChartJsInstance) => void;
	/** Mouse left the canvas. A touch has no leave event — the readout box's ✕ dismisses that. */
	readonly onLeave?: () => void;
}

// One controller per canvas: re-attaching aborts the previous listeners rather than stacking them.
// Same pattern (and same reason) as attachWheelZoom and setupPanning.
const _scrubControllers = new WeakMap<HTMLElement, AbortController>();

export function attachChartXScrub(canvas: HTMLElement, options: ChartXScrubOptions): void {
	_scrubControllers.get(canvas)?.abort();
	const ac = new AbortController();
	_scrubControllers.set(canvas, ac);
	const signal = ac.signal;

	let lastAt = 0;
	let lastPixel = -1;

	/** True when the scrub actually ran — the two guards are identical for mouse and touch. */
	function run(clientX: number): void {
		if (isChartPanning()) return;
		const chart = options.getChart();
		if (!chart?.chartArea) return;

		const now = performance.now();
		if (now - lastAt < CHART_HOVER_THROTTLE_MS) return;

		const pixelX = clientX - chart.canvas.getBoundingClientRect().left;
		const rounded = Math.round(pixelX);
		// Same x-pixel → the crosshair would land where it already is. Checked BEFORE the time stamp
		// is taken, so a stationary cursor never starves a later real move of its slot.
		if (rounded === lastPixel) return;
		lastAt = now;
		lastPixel = rounded;
		options.onScrub(pixelX, chart);
	}

	canvas.addEventListener('mousemove', (e: MouseEvent) => run(e.clientX), { signal });

	// touchstart as well as touchmove: the bar must appear on finger-DOWN, not only once it slides.
	// Passive — the pan/pinch gestures are chart-zoom-pan's, and it sets `touch-action: pan-y` on the
	// canvas so a one-finger vertical page scroll still works over the chart.
	const onTouch = (e: TouchEvent): void => {
		const touch = e.touches.length === 1 ? e.touches[0] : null;
		if (touch) run(touch.clientX);
	};
	canvas.addEventListener('touchstart', onTouch, { passive: true, signal });
	canvas.addEventListener('touchmove', onTouch, { passive: true, signal });

	if (options.onLeave) {
		const onLeave = options.onLeave;
		canvas.addEventListener('mouseleave', () => {
			lastPixel = -1;   // the next entry must draw, even if it lands on the same pixel
			onLeave();
		}, { signal });
	}
}

/** Drop a canvas's scrub listeners — for a teardown that is not followed by a re-attach. */
export function detachChartXScrub(canvas: HTMLElement): void {
	_scrubControllers.get(canvas)?.abort();
	_scrubControllers.delete(canvas);
}
