import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createCanvasStrip } from './canvas-strip';

/**
 * The shell both replay strips are built on. These pin the three things it exists to get right, and that
 * neither consumer's own test could reach:
 *
 *   - it paints on first mount WITHOUT waiting for the ResizeObserver (the race speed strip had no such
 *     paint, and its test could not see the gap because its FakeResizeObserver.observe() was a no-op);
 *   - a hidden (zero-width) container DEFERS the paint and gets it once it has a real size, instead of a
 *     1 px plot with every point stacked on the left edge;
 *   - the rule/hover colours come from cssVar(), so the two stacked strips can never drift apart on them.
 */

/** Counts the paint calls that matter and remembers each stroke's colour + start x — enough to tell the
 *  vertical rules apart. Everything else is a no-op so jsdom needs no real canvas. */
function recordingContext() {
	const counts = { clearRect: 0, fill: 0, fillText: 0, stroke: 0 };
	const strokes: Array<{ style: string; x: number }> = [];
	let startX = 0;
	const ctx = {
		fillStyle: '', strokeStyle: '', globalAlpha: 1, lineWidth: 1, lineJoin: '', font: '',
		textAlign: 'left', textBaseline: 'top',
		clearRect() { counts.clearRect++; },
		fill() { counts.fill++; },
		stroke() { counts.stroke++; strokes.push({ style: String(ctx.strokeStyle), x: startX }); },
		fillText() { counts.fillText++; },
		beginPath() {}, moveTo(x: number) { startX = x; }, lineTo() {}, closePath() {}, arc() {},
		rect() {}, clip() {}, save() {}, restore() {}, fillRect() {}, drawImage() {},
		setTransform() {}, scale() {}, setLineDash() {},
		measureText: () => ({ width: 10 }),
	};
	return { ctx, counts, strokes };
}

/** A ResizeObserver whose callback we can fire on demand — the no-op the consumers' tests use cannot. */
let roCallbacks: Array<() => void> = [];
class FiringResizeObserver {
	constructor(cb: () => void) { roCallbacks.push(cb); }
	observe(): void {}
	unobserve(): void {}
	disconnect(): void {}
}
function fireResize(): void { for (const cb of roCallbacks) cb(); }

/** The canvas's measured box, mutable so a test can hide it and then give it a size. */
let currentRect = { left: 0, top: 0, width: 800, height: 120, right: 800, bottom: 120 };
function setRect(width: number, height: number): void {
	currentRect = { left: 0, top: 0, width, height, right: width, bottom: height };
}

function mount(overrides: Partial<Parameters<typeof createCanvasStrip>[0]> = {}) {
	vi.stubGlobal('ResizeObserver', FiringResizeObserver);
	const main = recordingContext();
	const back = recordingContext();
	// The shell takes the visible canvas's context first, then the offscreen backdrop one.
	let handed = 0;
	HTMLCanvasElement.prototype.getContext = vi.fn(() => (handed++ === 0 ? main.ctx : back.ctx)) as never;
	HTMLCanvasElement.prototype.getBoundingClientRect = vi.fn(() => currentRect) as never;

	const canvas = document.createElement('canvas');
	// jsdom implements no pointer capture at all — the drag path calls it, so stub the three it uses.
	canvas.setPointerCapture = vi.fn();
	canvas.releasePointerCapture = vi.fn();
	canvas.hasPointerCapture = vi.fn(() => false);
	document.body.appendChild(canvas);

	const staticCalls = { n: 0 };
	const strip = createCanvasStrip({
		canvas,
		axisMax: 1000,
		yDomain: { min: 0, span: 100 },
		drawStatic: () => { staticCalls.n++; },
		hoverLabel: (v) => `${Math.round(v)}`,
		...overrides,
	});
	return { canvas, strip, main, back, staticCalls };
}

describe('createCanvasStrip', () => {
	beforeEach(() => {
		document.body.innerHTML = '';
		roCallbacks = [];
		setRect(800, 120);
	});

	it('paints on construction without waiting for the ResizeObserver', () => {
		const { strip, main, staticCalls } = mount();

		// The constructor painted: the static half once, and a frame (one clearRect on the visible canvas).
		expect(staticCalls.n).toBe(1);
		expect(main.counts.clearRect).toBe(1);
		// And it sized the plot from the real box, not a 1 px fallback: the right edge is near the canvas edge.
		expect(strip.xAt(1000)).toBeCloseTo(792, 0);
	});

	it('repaints and re-measures on a ResizeObserver callback', () => {
		const { strip, staticCalls } = mount();
		expect(staticCalls.n).toBe(1);
		const before = strip.xAt(1000);   // the right edge, measured against the 800 px box

		setRect(1600, 120);
		fireResize();

		expect(staticCalls.n).toBe(2);                      // repainted
		expect(strip.xAt(1000)).toBeGreaterThan(before);    // and re-measured to the wider box
	});

	it('defers the first paint into a hidden container, then paints once it has a size', () => {
		setRect(0, 0);
		const { strip, main, staticCalls } = mount();

		// A zero-width box would clamp the plot to 1 px and stack every point on the left edge — so the
		// shell paints nothing at all until a real size arrives.
		expect(staticCalls.n).toBe(0);
		expect(main.counts.clearRect).toBe(0);

		setRect(800, 120);
		fireResize();

		expect(staticCalls.n).toBe(1);
		expect(main.counts.clearRect).toBe(1);
		// Full-width plot, not the 1 px degenerate one.
		expect(strip.xAt(1000)).toBeCloseTo(792, 0);
	});

	it('resolves the rule colours through cssVar, so the two strips cannot drift apart on them', () => {
		// The drift regression guard: if the shell hard-coded the playhead literal instead of reading the
		// variable, this themed value would never reach the stroke.
		const spy = vi.spyOn(window, 'getComputedStyle').mockReturnValue({
			getPropertyValue: (name: string) => (name === '--profile-playhead' ? 'rgb(1, 2, 3)' : ''),
		} as unknown as CSSStyleDeclaration);

		const { strip, main } = mount();   // theme is read during the construction paint, with the stub in place
		main.strokes.length = 0;
		strip.setPlayhead(500);
		strip.tick(true);

		expect(main.strokes.some((s) => s.style === 'rgb(1, 2, 3)')).toBe(true);
		spy.mockRestore();
	});

	it('a fast seek does not swallow the last tick — the trailing timer paints the final playhead', () => {
		// The leading-only bug: a burst of tick(false) drops every call after the first within the interval,
		// so a drag-seek's FINAL position never reaches the canvas and the playhead lags a frame. The trailing
		// throttle re-runs the dropped call once the interval elapses. Drive the clock + the timer by hand.
		vi.useFakeTimers();
		try {
			let nowMs = 1000;
			const { strip, main } = mount({ now: () => nowMs });
			main.strokes.length = 0;

			// First seek tick: leading edge, paints immediately at 100.
			strip.setPlayhead(100);
			strip.tick();
			expect(main.strokes.some((s) => Math.abs(s.x - strip.xAt(100)) < 0.5)).toBe(true);

			main.strokes.length = 0;
			// Two more ticks inside the ~33 ms (30 Hz) window: the leading gate drops both. The final
			// position (900) is NOT on the canvas yet — this is exactly the frame that used to be lost.
			nowMs += 5; strip.setPlayhead(500); strip.tick();
			nowMs += 5; strip.setPlayhead(900); strip.tick();
			expect(main.strokes.some((s) => Math.abs(s.x - strip.xAt(900)) < 0.5)).toBe(false);

			// Interval elapses → the trailing timer fires and paints the CURRENT (final) playhead, 900.
			nowMs += 40;
			vi.advanceTimersByTime(40);
			expect(main.strokes.some((s) => Math.abs(s.x - strip.xAt(900)) < 0.5)).toBe(true);
		} finally {
			vi.useRealTimers();
		}
	});

	it('a null hover label draws the hover line but no readout box', () => {
		const { strip, main } = mount({ hoverLabel: () => null });
		main.counts.stroke = 0;
		main.counts.fillText = 0;

		strip.setHover(500);

		expect(main.counts.stroke).toBeGreaterThan(0);   // the vertical hover line
		expect(main.counts.fillText).toBe(0);            // no label, no box text
	});

	/**
	 * The static/moving split, counted rather than timed (PERF P4). The backdrop is the expensive half —
	 * it re-reads the theme through getComputedStyle, which is a layout read — so the animation loop must
	 * blit it, never repaint it. A counted proxy states that directly and survives in CI; a benchmark
	 * would measure the machine instead, and a heap delta cannot prove an allocation claim at all.
	 */
	it('an animation loop repaints only the moving half — the backdrop is painted once and blitted', () => {
		let nowMs = 1000;
		let foreground = 0;
		const { strip, staticCalls } = mount({
			now: () => nowMs,
			drawForeground: () => { foreground++; },
		});

		// Construction paints both halves exactly once.
		expect(staticCalls.n).toBe(1);
		expect(foreground).toBe(1);

		// 30 frames, each a full 30 Hz interval apart so the throttle drops none of them.
		const FRAMES = 30;
		for (let i = 0; i < FRAMES; i++) {
			nowMs += 34;
			strip.setPlayhead(i * 10);
			strip.tick();
		}

		expect(foreground).toBe(FRAMES + 1);   // one per frame, plus the construction paint
		expect(staticCalls.n).toBe(1);         // ...and the backdrop never repainted
	});

	/** The other half of the split: a data/size change is exactly when the backdrop MAY be repainted. */
	it('repaint() redraws the backdrop — the split is a frame-path rule, not a ban', () => {
		const { strip, staticCalls } = mount();
		expect(staticCalls.n).toBe(1);

		strip.repaint();

		expect(staticCalls.n).toBe(2);
	});

	/**
	 * TOUCH HAS NO HOVER: a finger that has not moved fires no pointermove, so a tap must produce the
	 * readout by itself. This is the only route to a value on a strip that draws no y-axis, and a
	 * READ-ONLY strip (the segment and activity replays, a share fork) has no other one — which is why
	 * pointerdown is bound whether or not the consumer passed an `interaction`.
	 */
	describe('a press reads out, with no move first', () => {
		const press = (canvas: HTMLCanvasElement, clientX: number): void =>
			void canvas.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX, clientY: 10 }));

		it('reports the value under a tap on a read-only strip', () => {
			const onHover = vi.fn();
			const { canvas } = mount({ onHover });   // no `interaction` — read-only

			press(canvas, 400);

			expect(onHover).toHaveBeenCalledTimes(1);
			expect(onHover.mock.calls[0]?.[0]).toBeGreaterThan(0);
		});

		it('clamps a tap in the padding to the plot, rather than reporting off-axis', () => {
			const onHover = vi.fn();
			const { canvas } = mount({ onHover });

			press(canvas, 0);       // hard against the left edge, inside the plot's own padding
			press(canvas, 100_000); // and far past the right

			const [first, second] = onHover.mock.calls.map((c) => c[0] as number);
			expect(first).toBeGreaterThanOrEqual(0);
			expect(second).toBeLessThanOrEqual(1000);   // axisMax
		});

		it('still starts a drag when the consumer wants one', () => {
			const onPress = vi.fn(() => true);
			const onHover = vi.fn();
			const { canvas } = mount({
				onHover,
				interaction: { onPress, onDrag: vi.fn(), onRelease: vi.fn() },
			});

			press(canvas, 400);

			expect(onPress).toHaveBeenCalledTimes(1);
			expect(onHover).toHaveBeenCalledTimes(1);   // and the readout happened too
		});
	});
});
