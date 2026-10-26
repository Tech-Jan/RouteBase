/**
 * chart-gesture-preview.ts — bitmap preview for chart x-zoom/pan gestures
 *
 * A real Chart.js re-render per wheel tick / pan frame re-runs decimation and
 * rebuilds elements for EVERY dataset — with hundreds of effort lines that is
 * the whole interaction lag. A GesturePreviewGroup instead snapshots each
 * chart's plot area once per gesture and, per frame, redraws that bitmap
 * scaled/translated to the pending x-range (map-tile style, briefly blurry).
 * ONE real zoomScale render per chart happens when the gesture ends.
 *
 * Gesture MODE (captured at begin() from an optional getMode() callback — ID-15):
 *  • snapshot (default) — the bitmap-preview path above. `overscan` widens the snapshot to
 *    `overscan×` the viewport so a pan hits black only past that buffer; when the pending range
 *    exceeds the snapshot's data range it re-snapshots. overscan = 1 ⇒ exactly the legacy behavior.
 *  • perFrame — no snapshot; preview() does a real zoomScale render per chart per frame. For chart
 *    sets whose apply render is cheap (few datasets), this kills the black area outright with no
 *    bitmap. Assumes no per-frame data prep is needed (the apply hooks run once, at end).
 *
 * Generic over any group of charts sharing a linear x-axis; wired in via
 * chart-zoom-pan's finalizeChart/setupPanning (pan + pinch) and attachWheelZoom.
 */
import { must } from './invariant';
import { setChartPanning } from './chart-pan-state';
import { chartDevicePixelRatio } from './chart-internals';
import { clampRangeToBounds } from './chart-zoom-pan';
import { reportChartError } from './chart-error-log';

type ChartInstance = InstanceType<typeof Chart>;

export interface XRange {
	min: number;
	max: number;
}

interface Snapshot {
	bitmap: HTMLCanvasElement; // device-pixel copy of the plot area
	xMin: number;
	xMax: number;
}

/**
 * Optional apply-phase hooks — for charts whose data must be prepared for the final range
 * before the one real render (e.g. chart-factory's LTTB redecimation: plugin zoomScale does
 * NOT fire onZoom, so nothing else re-decimates), plus one-shot post-apply page work
 * (e.g. refreshing a DOM overlay that stayed stale during the bitmap preview).
 */
export interface ApplyHooks {
	/** Per chart, right before its zoomScale apply render. */
	beforeApply?: (chart: ChartInstance, range: XRange) => void;
	/** Once, after every chart has been applied. */
	afterApply?: (range: XRange) => void;
}

/**
 * Per-gesture mode, read from getMode() at begin() (see the header). `perFrame` charts skip the
 * snapshot and render live; snapshot charts widen the bitmap to `overscan×` the viewport.
 */
export interface GestureMode {
	perFrame: boolean;
	/** Snapshot-mode only: capture `overscan×` the viewport width (default 1 = legacy). */
	overscan?: number;
}

const DEFAULT_MODE: GestureMode = { perFrame: false, overscan: 1 };

export class GesturePreviewGroup {
	private readonly getCharts: () => (ChartInstance | null | undefined)[];
	private readonly hooks: ApplyHooks;
	private readonly getMode?: (() => GestureMode) | undefined;
	private mode: GestureMode = DEFAULT_MODE; // captured at begin(), held for the gesture
	private snapshots = new Map<ChartInstance, Snapshot>();
	private pending: XRange | null = null;
	private rafId = 0;
	private applyTimer: ReturnType<typeof setTimeout> | 0 = 0;
	private active = false;

	constructor(
		getCharts: () => (ChartInstance | null | undefined)[],
		hooks?: ApplyHooks,
		opts?: { getMode?: () => GestureMode },
	) {
		this.getCharts = getCharts;
		this.hooks = hooks ?? {};
		this.getMode = opts?.getMode;
	}

	get isActive(): boolean {
		return this.active;
	}

	/** Pending preview range while a gesture is in flight, else the chart's live scale range. */
	currentRange(chart: ChartInstance): XRange {
		if (this.pending) return this.pending;
		const x = must(chart.scales.x, 'x scale configured');
		return { min: x.min, max: x.max };
	}

	/**
	 * Start (or continue) a gesture. Idempotent: while a gesture — including a pending
	 * apply-debounce — is in flight, the existing snapshots are reused. Re-snapshotting
	 * an already-previewed canvas would compound the blur.
	 */
	begin(): void {
		if (this.applyTimer) {
			clearTimeout(this.applyTimer);
			this.applyTimer = 0;
		}
		// Hover is suppressed while previewing: the live scales are stale vs the drawn bitmap, so
		// crosshair/readout positions computed from them would not match what's on screen. Set it
		// BEFORE the active-return so a begin() during a gesture (pan→pinch, wheel→drag) re-asserts
		// the flag rather than running the rest of the gesture with it stale (CG-03).
		setChartPanning(true);
		if (this.active) return;
		this.active = true;
		this.mode = this.getMode?.() ?? DEFAULT_MODE;
		if (this.mode.perFrame) return; // no snapshot — preview() renders live
		// Overscan snapshots are LAZY (created on the first preview frame): the overscan render is
		// expensive, so a plain click (begin+end, no move) must render nothing and leave scales as-is.
		if ((this.mode.overscan ?? 1) > 1) return;
		// factor 1 (default / Details) — eager copy of the already-rendered viewport, byte-identical
		// to the legacy path (no render; panning past it shows black, unchanged).
		for (const chart of this.getCharts()) {
			if (!chart || !chart.chartArea) continue;
			const x = must(chart.scales.x, 'x scale configured');
			this._snapshot(chart, { min: x.min, max: x.max });
		}
	}

	/**
	 * Capture a chart's plot area for the given view. In overscan mode the chart is first rendered
	 * once at `overscan×` the view (clamped to the data extent by the zoom plugin's `limits`), so the
	 * bitmap covers a wider data range than the viewport — panning stays inside it without hitting
	 * black. The overscan render is a one-time cost per gesture / buffer refill, never per frame.
	 * The visible view is then drawn back synchronously so the overscan render never paints.
	 */
	private _snapshot(chart: ChartInstance, view: XRange): void {
		const factor = this.mode.overscan ?? 1;
		if (factor > 1 && chart.chartArea) {
			const os = this._overscan(view, factor);
			try {
				chart.zoomScale("x", os, "none"); // one real render at the wider range
			} catch (e) { reportChartError("chart-gesture-preview:snapshot", e); }
			const x = must(chart.scales.x, 'x scale configured');
			const snap = this._capture(chart, x.min, x.max); // actual (clamped) rendered range
			if (snap) {
				this.snapshots.set(chart, snap);
				this._drawPreview(chart, snap, view); // restore the visible view (no overscan flash)
			}
		} else {
			// factor 1 (default / Details): copy the already-rendered viewport, no extra render.
			const x = must(chart.scales.x, 'x scale configured');
			const snap = this._capture(chart, x.min, x.max);
			if (snap) this.snapshots.set(chart, snap);
		}
	}

	/** device-pixel copy of a chart's current plot area, tagged with the data range it covers. */
	private _capture(chart: ChartInstance, xMin: number, xMax: number): Snapshot | null {
		const area = chart.chartArea;
		if (!area) return null;
		const dpr = this._dpr(chart);
		const w = Math.round((area.right - area.left) * dpr);
		const h = Math.round((area.bottom - area.top) * dpr);
		if (w <= 0 || h <= 0) return null;
		const bitmap = document.createElement("canvas");
		bitmap.width = w;
		bitmap.height = h;
		bitmap.getContext("2d")?.drawImage(chart.canvas, area.left * dpr, area.top * dpr, w, h, 0, 0, w, h);
		return { bitmap, xMin, xMax };
	}

	private _overscan(view: XRange, factor: number): XRange {
		const pad = ((view.max - view.min) * (factor - 1)) / 2;
		return { min: view.min - pad, max: view.max + pad };
	}

	/**
	 * Preview an x-range on every chart in the group (rAF-coalesced). perFrame mode renders live;
	 * snapshot mode redraws the bitmap, re-snapshotting any chart the pending range has panned past.
	 */
	preview(min: number, max: number): void {
		if (!this.active || !(max > min)) return;
		this.pending = { min, max };
		if (this.rafId) return;
		this.rafId = requestAnimationFrame(() => {
			this.rafId = 0;
			const range = this.pending;
			if (!range || !this.active) return;
			for (const chart of this.getCharts()) {
				if (!chart) continue;
				try {
					if (this.mode.perFrame) {
						chart.zoomScale("x", range, "none");
						continue;
					}
					const snap = this.snapshots.get(chart);
					// Overscan (factor > 1): create the snapshot lazily on the first frame, and
					// re-snapshot when the pan exceeds the buffer. factor 1 keeps the legacy behavior
					// (snap made eagerly at begin; panning past it shows black — never re-snapshot,
					// which for factor 1 would capture the bitmap-of-a-bitmap).
					const overscan = (this.mode.overscan ?? 1) > 1;
					if (overscan && (!snap || range.min < snap.xMin || range.max > snap.xMax)) {
						this._snapshot(chart, range);
					} else if (snap) {
						this._drawPreview(chart, snap, range);
					}
				} catch (e) { reportChartError("chart-gesture-preview:preview", e); }
			}
		});
	}

	/**
	 * End the gesture: after `delayMs` (0 = synchronously) apply ONE real render per chart
	 * via zoomScale. A begin() during the delay cancels the apply and continues the gesture
	 * from the same snapshots (wheel bursts, wheel→drag transitions).
	 */
	end(delayMs = 0): void {
		if (!this.active) return;
		if (this.applyTimer) clearTimeout(this.applyTimer);
		if (delayMs <= 0) {
			this.applyTimer = 0;
			this._apply();
			return;
		}
		this.applyTimer = setTimeout(() => {
			this.applyTimer = 0;
			this._apply();
		}, delayMs);
	}

	/** Abort without applying (charts being rebuilt / zoom reset). */
	cancel(): void {
		if (this.applyTimer) {
			clearTimeout(this.applyTimer);
			this.applyTimer = 0;
		}
		if (this.rafId) {
			cancelAnimationFrame(this.rafId);
			this.rafId = 0;
		}
		this.snapshots.clear();
		this.pending = null;
		if (this.active) {
			this.active = false;
			setChartPanning(false);
		}
	}

	private _apply(): void {
		const range = this.pending;
		this.cancel();
		if (!range) return; // gesture ended without a preview (plain click) — nothing to render
		for (const chart of this.getCharts()) {
			if (!chart) continue;
			try {
				this.hooks.beforeApply?.(chart, range);
				chart.zoomScale("x", range, "none");
			} catch (e) { reportChartError("chart-gesture-preview:apply", e); }
		}
		this.hooks.afterApply?.(range);
	}

	private _dpr(chart: ChartInstance): number {
		return chartDevicePixelRatio(chart)
			?? window.devicePixelRatio
			?? 1;
	}

	// Clip to the plot area, clear it, and draw the snapshot scaled/translated so the
	// snapshot's data range [xMin,xMax] lands where it belongs in the previewed range.
	// Axes/ticks outside the plot area keep their (stale) pixels until the apply render.
	private _drawPreview(chart: ChartInstance, snap: Snapshot, range: XRange): void {
		const area = chart.chartArea;
		if (!area) return;
		const ctx = chart.ctx;
		const dpr = this._dpr(chart);
		const w = area.right - area.left;
		const h = area.bottom - area.top;
		const span = range.max - range.min;
		const destX = area.left + ((snap.xMin - range.min) / span) * w;
		const destW = ((snap.xMax - snap.xMin) / span) * w;
		ctx.save();
		// Draw in CSS px regardless of whatever transform Chart.js left on the context.
		ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
		ctx.beginPath();
		ctx.rect(area.left, area.top, w, h);
		ctx.clip();
		ctx.clearRect(area.left, area.top, w, h);
		ctx.drawImage(snap.bitmap, destX, area.top, destW, h);
		ctx.restore();
	}
}

// Re-attaching (chart rebuilds re-run wiring) aborts the previous listener instead of stacking.
const _wheelControllers = new WeakMap<HTMLElement, AbortController>();

/**
 * Custom wheel x-zoom driving a GesturePreviewGroup — replaces chartjs-plugin-zoom's wheel
 * handler, which triggers a real chart.update per tick and cannot preview. Anchors the zoom at the
 * cursor, clamps to the required `getBounds(chart)` (must return the DATA extent — the apply
 * render's zoomScale mutates `options.scales.x.min/max` to the zoomed window, so the axis options
 * are NOT a valid zoom-out clamp; CG-02), and applies the crisp render `applyDelayMs` after the
 * last tick.
 */
export function attachWheelZoom(
	canvas: HTMLElement,
	getChart: () => ChartInstance | null | undefined,
	group: GesturePreviewGroup,
	getBounds: (chart: ChartInstance) => XRange,
	opts?: { speed?: number; applyDelayMs?: number },
): void {
	const speed = opts?.speed ?? 0.1;
	const delay = opts?.applyDelayMs ?? 150;

	_wheelControllers.get(canvas)?.abort();
	const ac = new AbortController();
	_wheelControllers.set(canvas, ac);

	canvas.addEventListener("wheel", (e: WheelEvent) => {
		const chart = getChart();
		if (!chart || !chart.chartArea) return;
		e.preventDefault();

		const bounds = getBounds(chart);
		const fullSpan = bounds.max - bounds.min;
		if (!(fullSpan > 0)) return;

		const cur = group.currentRange(chart);
		const factor = e.deltaY < 0 ? 1 - speed : 1 + speed;
		// Already at full extent and zooming out further — don't start a pointless gesture.
		if (factor > 1 && cur.min <= bounds.min && cur.max >= bounds.max) return;

		group.begin();
		let newSpan = (cur.max - cur.min) * factor;
		if (newSpan >= fullSpan) {
			group.preview(bounds.min, bounds.max);
			group.end(delay);
			return;
		}
		newSpan = Math.max(newSpan, fullSpan * 0.01); // over-zoom guard (mirrors pinch)

		// Keep the data value under the cursor stationary.
		const area = chart.chartArea;
		const rect = chart.canvas.getBoundingClientRect();
		const px = e.clientX - rect.left;
		const frac = Math.min(1, Math.max(0, (px - area.left) / (area.right - area.left)));
		const anchor = cur.min + frac * (cur.max - cur.min);
		let newMin = anchor - frac * newSpan;
		let newMax = newMin + newSpan;
		({ min: newMin, max: newMax } = clampRangeToBounds(newMin, newMax, bounds.min, bounds.max));

		group.preview(newMin, newMax);
		group.end(delay);
	}, { passive: false, signal: ac.signal });
}
