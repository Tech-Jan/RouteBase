/**
 * The shell the replay strips are built on: an animated, hand-drawn canvas strip with a static backdrop
 * blitted underneath a moving foreground, a shared hover, a playhead and a lead line — over one linear
 * x-axis. `core/profile-chart.ts` is the consumer; a second one, the race's own field-speed strip, was
 * drawn STACKED under it and sharing this x-axis, and anything the two redid independently drifted. That
 * strip is now a trace INSIDE the profile (ProfileTrace), which is the same fix taken to its end: one
 * canvas cannot disagree with itself about a position.
 *
 * What lives here is the machinery that was, verbatim, in both: the dual canvas and its DPR sizing, the
 * distance↔pixel maths, `drawRule`, the hover label box, the RIDER_HZ throttle and the idempotent
 * `setHover`, the `ResizeObserver`, and — the part that matters most — the theme colours for the rules and
 * the hover, resolved through `cssVar()` from ONE source so the two strips can never disagree about them.
 *
 * What stays with a consumer is the picture: its `drawStatic` paints the backdrop (terrain, or a spread
 * band and a median), its optional `drawForeground` paints the per-frame layer on top (the race's dot
 * field), and its `hoverLabel` names the readout. Everything else is the shell's.
 *
 * ## Two layers, and the reason
 *
 * The backdrop is STATIC: it changes on a resize or a data change — a human-scale event, not a frame. The
 * foreground moves every frame. So the static half is rendered once into an offscreen canvas and blitted;
 * only the foreground, the playhead, the lead line and the hover are redrawn per frame. Without the split,
 * animating the field would re-stroke the whole terrain path sixty times a second to draw the same picture
 * underneath the dots.
 *
 * ## The hidden-container hazard
 *
 * `Math.max(1, Math.round(rect.width))` turns a hidden (0-width) box into a 1 px plot rather than an error,
 * which would stack every point on the left edge. So a first paint into a degenerate box is DEFERRED: the
 * strip waits for the `ResizeObserver` to report a real size, then paints. A caller that sets data and
 * ticks before that happens loses nothing — the picture reads live state, so the deferred paint is correct.
 */

import { axisValueAtX, xAtAxisValue } from './profile-axis';
import { cssVar } from './css-vars';
import { createTickThrottle } from './tick-throttle';
import { createTrailingThrottle } from './trailing-throttle';

export interface StripPadding {
	readonly top: number;
	readonly right: number;
	readonly bottom: number;
	readonly left: number;
}

export interface PlotBox {
	readonly left: number;
	readonly width: number;
	readonly top: number;
	readonly height: number;
}

/**
 * The padding every strip shares. One source so the plots line up on the x-axis: a different left/right on
 * one would slide its plot sideways and two strips would no longer read as one picture.
 *
 * `bottom` is 0 deliberately. It was 14 px, left over from an x-axis that no longer exists — nothing is
 * drawn below the plot by any consumer, so the band was dead space that read as a border under the
 * terrain. `top` stays: the hover label box sits in it.
 */
export const STRIP_PADDING: StripPadding = { top: 10, right: 8, bottom: 0, left: 8 };

/** The frame-loop rate cap and the pointer-hover cap. Matches the marker layer, so nothing reads as a
 *  different moment. One source so the stacked strips animate in lockstep. */
export const STRIP_HZ = 30;

/** The geometry + target context handed to a draw hook. `g` is the background context for `drawStatic`
 *  and the visible one for `drawForeground`; the maths is the same either way. */
export interface StripSurface {
	readonly g: CanvasRenderingContext2D;
	plot(): PlotBox;
	/** Axis value → x pixel. */
	xAt(value: number): number;
	/** Domain value → y pixel, over the y-domain the consumer set. */
	yAt(value: number): number;
}

/** A press on an interactive strip, in both axis and pixel space so a consumer can hit-test its markers. */
export interface StripPress {
	readonly value: number;
	readonly x: number;
	xAt(value: number): number;
}

/**
 * The write path, in the consumer's hands. The shell owns the pointer wiring and the capture; the consumer
 * owns what a press means and what a drag does. Omit it for a read-only strip.
 */
export interface StripInteraction {
	/** A press landed. Return true to begin a drag (the shell captures the pointer), false to ignore it. */
	onPress(press: StripPress): boolean;
	/** The drag moved to this axis value — called every pointermove while dragging, throttle bypassed. */
	onDrag(value: number): void;
	/** The drag was released. */
	onRelease(): void;
}

export interface CanvasStripOptions {
	readonly canvas: HTMLCanvasElement;
	/** The x-axis span in axis units. Change it with `setAxisMax` when the backdrop is swapped. */
	readonly axisMax: number;
	/** The initial y-domain; `yAt` maps `min`→bottom, `min + span`→top. Update it with `setYDomain`. */
	readonly yDomain: { readonly min: number; readonly span: number };
	/** Paints the static backdrop. The offscreen context is pre-cleared and DPR-scaled. */
	drawStatic(surface: StripSurface): void;
	/** Paints the per-frame foreground over the blitted backdrop, before the rules and hover. */
	drawForeground?: ((surface: StripSurface) => void) | undefined;
	/** The hover readout at an axis value; null draws the hover line with no label box. */
	hoverLabel(value: number): string | null;
	/** Pointer moved over the strip (axis units) — drives a shared hover. Null on leave. */
	onHover?: ((value: number | null) => void) | undefined;
	readonly interaction?: StripInteraction | undefined;
	/** Defaults to STRIP_PADDING; override only with a reason to break x-axis alignment. */
	readonly padding?: StripPadding | undefined;
	/** Defaults to STRIP_HZ. */
	readonly hz?: number | undefined;
	readonly now?: (() => number) | undefined;
}

export interface CanvasStrip {
	plot(): PlotBox;
	xAt(value: number): number;
	/** x pixel → axis value, clamped to [0, axisMax]. */
	valueAt(x: number): number;
	yAt(value: number): number;
	setAxisMax(axisMax: number): void;
	setYDomain(min: number, span: number): void;
	/**
	 * Where the WATCHED item is, in axis units. Sets state only — drawn on the next frame — so a per-frame
	 * caller does not pay a repaint per playhead move.
	 */
	setPlayhead(value: number | null): void;
	/** A second rule (the leader), set only while the playhead is someone else. Null clears it. */
	setLeadLine(value: number | null): void;
	/** Draw the hover line here — from another chart or the map. Idempotent for the firing strip. */
	setHover(value: number | null): void;
	setCursor(cursor: string): void;
	/** Repaint the static backdrop then a frame — after a data, decoration or size change. */
	repaint(): void;
	/** Repaint just the moving half. */
	repaintFrame(): void;
	/** Throttled frame repaint for the animation loop. `force` bypasses the cap — a seek must not lag. */
	tick(force?: boolean): void;
	destroy(): void;
}

/**
 * The rule and hover colours, resolved to concrete strings (a canvas cannot take `var(--x)`). Re-read
 * whenever the static half is redrawn, never per frame: `getComputedStyle` is a layout read and the
 * foreground repaints RIDER_HZ times a second. One source for BOTH strips — the drift this shell exists
 * to kill was these five values, hard-coded in one strip and `cssVar`-resolved in the other.
 */
interface StripTheme {
	playhead: string;
	leadLine: string;
	hoverLine: string;
	hoverBox: string;
	hoverText: string;
}

function readTheme(): StripTheme {
	return {
		// Both rules are translucent: they sit ON the foreground, and an opaque bar hides exactly what the
		// line is pointing at.
		playhead: cssVar('--profile-playhead', 'rgba(249, 115, 22, 0.75)'),
		leadLine: cssVar('--profile-lead-line', 'rgba(250, 204, 21, 0.6)'),
		hoverLine: cssVar('--profile-hover-line', 'rgba(226, 232, 240, 0.65)'),
		hoverBox: cssVar('--profile-hover-box', 'rgba(15, 23, 42, 0.85)'),
		hoverText: cssVar('--profile-hover-text', '#e2e8f0'),
	};
}

export function createCanvasStrip(options: CanvasStripOptions): CanvasStrip {
	const { canvas, interaction } = options;
	const padding = options.padding ?? STRIP_PADDING;
	const hz = options.hz ?? STRIP_HZ;

	const ctx = canvas.getContext('2d');
	// The static half: rendered only when the backdrop or the size changes.
	const background = document.createElement('canvas');
	const backgroundCtx = background.getContext('2d');
	canvas.style.cursor = 'default';

	// The frame throttle is TRAILING, not leading-only. A drag-seek delivers its final position as the last
	// of a burst of tick(false) calls; a leading-edge gate drops that last one, so the playhead lags one
	// 30 Hz frame at seek-end until the next interaction — on the segment and activity replays (the race
	// replay force-ticks on pause/seek-end, race-replay-view.ts, and dodges it). A trailing timer re-runs the
	// dropped call once the interval is up, so the final frame always paints. Same "leading-only throttle
	// swallows the LAST call" class + fix as core/trailing-throttle.ts (PERF-2) and the race leaderboard's
	// force-render-on-pause.
	const frameThrottle = createTrailingThrottle(1000 / hz, () => paintFrame(), {
		now: options.now ?? (() => performance.now()),
		setTimeout: (fn, ms) => window.setTimeout(fn, ms),
		clearTimeout: (id) => window.clearTimeout(id),
	});
	// A pointer hover fires far faster than the eye needs, and each move repaints the frame. Cap it at hz,
	// exactly as the map hover is capped (race-charts.ts onMapMove) — RF-03. Leading-only is right here: a
	// dropped last hover is corrected by pointerleave, and it never moves the playhead.
	const hoverThrottle = createTickThrottle(1000 / hz, options.now);

	let theme = readTheme();
	let axisMax = options.axisMax;
	let yMin = options.yDomain.min;
	let ySpan = options.yDomain.span;
	let playhead: number | null = null;
	let leadLine: number | null = null;
	/** Axis value under the pointer. Drives the hover readout; null when the pointer is away. */
	let hover: number | null = null;
	let dragging = false;
	let width = 0;
	let height = 0;

	function plot(): PlotBox {
		return {
			left: padding.left,
			width: Math.max(1, width - padding.left - padding.right),
			top: padding.top,
			height: Math.max(1, height - padding.top - padding.bottom),
		};
	}

	function xAt(value: number): number {
		const p = plot();
		return xAtAxisValue(value, p.left, p.width, axisMax);
	}

	function valueAt(x: number): number {
		const p = plot();
		return axisValueAtX(x, p.left, p.width, axisMax);
	}

	function yAt(value: number): number {
		const p = plot();
		return p.top + p.height - ((value - yMin) / ySpan) * p.height;
	}

	const staticSurface: StripSurface | null = backgroundCtx ? { g: backgroundCtx, plot, xAt, yAt } : null;
	const frameSurface: StripSurface | null = ctx ? { g: ctx, plot, xAt, yAt } : null;

	/** Resize both canvases to the element's CSS box. Returns true when the geometry actually changed. */
	function resize(): boolean {
		const rect = canvas.getBoundingClientRect();
		const dpr = window.devicePixelRatio || 1;
		const w = Math.max(1, Math.round(rect.width));
		const h = Math.max(1, Math.round(rect.height));
		if (w === width && h === height && canvas.width === Math.round(w * dpr)) return false;

		width = w;
		height = h;
		for (const c of [canvas, background]) {
			c.width = Math.round(w * dpr);
			c.height = Math.round(h * dpr);
		}
		ctx?.setTransform(dpr, 0, 0, dpr, 0, 0);
		backgroundCtx?.setTransform(dpr, 0, 0, dpr, 0, 0);
		return true;
	}

	/** A full-height vertical rule at an axis value — the playhead and the lead line are the same stroke. */
	function drawRule(g: CanvasRenderingContext2D, value: number, color: string): void {
		const p = plot();
		const x = xAt(value);
		g.beginPath();
		g.moveTo(x, p.top);
		g.lineTo(x, p.top + p.height);
		g.lineWidth = 2;
		g.strokeStyle = color;
		g.stroke();
	}

	/** The readout under the pointer: a line, and the consumer's label in a box that flips off the right edge. */
	function drawHover(g: CanvasRenderingContext2D, value: number): void {
		const p = plot();
		const x = xAt(value);
		g.beginPath();
		g.moveTo(x, p.top);
		g.lineTo(x, p.top + p.height);
		g.lineWidth = 1;
		g.strokeStyle = theme.hoverLine;
		g.stroke();

		const label = options.hoverLabel(value);
		if (label == null) return;
		g.font = '11px system-ui, sans-serif';
		g.textBaseline = 'middle';

		const pad = 4;
		const boxWidth = g.measureText(label).width + pad * 2;
		const boxHeight = 16;
		// Flip to the left of the line near the right edge, so the label never runs off the canvas.
		const boxLeft = x + 6 + boxWidth <= p.left + p.width ? x + 6 : x - 6 - boxWidth;
		const boxTop = p.top + 2;

		g.fillStyle = theme.hoverBox;
		g.fillRect(boxLeft, boxTop, boxWidth, boxHeight);
		g.fillStyle = theme.hoverText;
		g.fillText(label, boxLeft + pad, boxTop + boxHeight / 2);
	}

	/** The static half. Re-reads the theme; the consumer paints the backdrop over a cleared canvas. */
	function paintBackground(): void {
		if (!backgroundCtx || !staticSurface) return;
		theme = readTheme();
		backgroundCtx.clearRect(0, 0, width, height);
		options.drawStatic(staticSurface);
	}

	/** The moving half: blit the backdrop, then the foreground, then the rules and the hover on top. */
	function paintFrame(): void {
		if (!ctx || !frameSurface) return;
		ctx.clearRect(0, 0, width, height);
		ctx.drawImage(background, 0, 0, width, height);

		options.drawForeground?.(frameSurface);

		// The leader goes down first, so where the two coincide the playhead — the item the viewer chose to
		// watch — is the one left visible.
		if (leadLine != null) drawRule(ctx, leadLine, theme.leadLine);
		if (playhead != null) drawRule(ctx, playhead, theme.playhead);

		if (hover != null) drawHover(ctx, hover);
	}

	/** True unless the box is degenerate (a hidden container). See the hidden-container note up top. */
	function boxIsReal(): boolean {
		const rect = canvas.getBoundingClientRect();
		return Math.round(rect.width) >= 1 && Math.round(rect.height) >= 1;
	}

	function repaint(): void {
		if (!boxIsReal()) return;   // defer; the ResizeObserver paints once a real size arrives
		resize();
		paintBackground();
		paintFrame();
	}

	// ── pointer ───────────────────────────────────────────────────────────────────────────────────

	function localX(event: PointerEvent): number {
		return event.clientX - canvas.getBoundingClientRect().left;
	}

	function onPointerDown(event: PointerEvent): void {
		const x = localX(event);

		// TOUCH HAS NO HOVER. A finger that has not moved yet has fired no pointermove, so a tap used to
		// read nothing at all — the readout only appeared once you slid. That is the only way to read a
		// value off this strip (neither the terrain nor the trace draws an axis), so the press itself has
		// to produce one. Unconditional, and before the interaction check: a read-only strip — the segment
		// replay, the activity replay, a share fork — is exactly the case that has no other route in.
		const p = plot();
		hover = valueAt(Math.min(p.left + p.width, Math.max(p.left, x)));
		options.onHover?.(hover);
		paintFrame();

		if (!interaction) return;
		if (interaction.onPress({ value: valueAt(x), x, xAt })) {
			dragging = true;
			canvas.setPointerCapture(event.pointerId);
		}
	}

	function onPointerMove(event: PointerEvent): void {
		const p = plot();
		const x = localX(event);
		hover = valueAt(Math.min(p.left + p.width, Math.max(p.left, x)));

		if (dragging) {
			// The consumer snaps it and repaints — so the drag feels live while the rules about where it may
			// land stay entirely on its side. Throttle bypassed: a drag must track the finger.
			options.onHover?.(hover);
			interaction?.onDrag(hover);
			return;
		}

		// Throttle to hz like the map hover. onHover syncs the OTHER strip + the map marker; setHover is
		// idempotent for this (firing) strip, so our own hover is drawn once, here.
		if (!hoverThrottle.shouldRun(false)) return;
		options.onHover?.(hover);
		paintFrame();
	}

	function onPointerUp(event: PointerEvent): void {
		if (!dragging) return;
		dragging = false;
		if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
		interaction?.onRelease();
	}

	function onPointerLeave(): void {
		hover = null;
		options.onHover?.(null);
		paintFrame();
	}

	canvas.addEventListener('pointermove', onPointerMove);
	canvas.addEventListener('pointerleave', onPointerLeave);
	// pointerdown ALWAYS — it is what gives a touch device a readout at all (see onPointerDown). Only the
	// drag half is gated on `interaction`.
	canvas.addEventListener('pointerdown', onPointerDown);
	if (interaction) {
		canvas.addEventListener('pointerup', onPointerUp);
		canvas.addEventListener('pointercancel', onPointerUp);
	}

	const observer = new ResizeObserver(() => repaint());
	observer.observe(canvas);
	// Paint now rather than waiting for the observer's first callback: a caller that sets data and ticks
	// before that lands would otherwise paint into an unsized plot. Deferred when the box is still hidden.
	repaint();

	return {
		plot,
		xAt,
		valueAt,
		yAt,
		setAxisMax(next) {
			axisMax = next;
		},
		setYDomain(min, span) {
			yMin = min;
			ySpan = span;
		},
		setPlayhead(value) {
			playhead = value;
		},
		setLeadLine(value) {
			leadLine = value;
		},
		setHover(value) {
			// Idempotent for the firing strip: on its own pointermove it already set `hover` and drew, so a
			// re-entrant setHover with the same value is a no-op. A move from the map or the other strip
			// changes the value and redraws (RF-03).
			if (value === hover) return;
			hover = value;
			paintFrame();
		},
		setCursor(cursor) {
			canvas.style.cursor = cursor;
		},
		repaint,
		repaintFrame: paintFrame,
		tick(force = false) {
			// force bypasses the cap and runs now (a caller about to stop); otherwise the trailing timer
			// guarantees the last request of a burst still paints — a seek must not lag its final frame.
			frameThrottle.request(force);
		},
		destroy() {
			frameThrottle.cancel();
			observer.disconnect();
			canvas.removeEventListener('pointermove', onPointerMove);
			canvas.removeEventListener('pointerleave', onPointerLeave);
			canvas.removeEventListener('pointerdown', onPointerDown);
			canvas.removeEventListener('pointerup', onPointerUp);
			canvas.removeEventListener('pointercancel', onPointerUp);
		},
	};
}
