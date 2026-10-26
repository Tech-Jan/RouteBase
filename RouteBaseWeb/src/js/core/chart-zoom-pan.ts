/**
 * chart-zoom-pan.ts — X-only zoom/pan/pinch interaction for line charts.
 *
 * Owns the shared zoom-plugin config, the axis-freeze that stops the Y axis
 * rescaling on X-zoom, click-drag + touch pan, touch pinch, and the one-call
 * `finalizeChart` that wires them onto a freshly-built chart.
 *
 * Gestures drive a GesturePreviewGroup when one is passed (bitmap preview, one real
 * render on release — see chart-gesture-preview.ts). That is the DEFAULT for pages
 * with x-synced chart groups (the group's apply IS the cross-chart sync); the
 * per-frame zoomScale fallback below is for single standalone charts only.
 */
import { must } from './invariant';
import { setChartPanning } from './chart-pan-state';
import { scaleInternals } from './chart-internals';
import { getCrosshairPixelX } from './chart-overlay';
import { claimPinchGesture } from './claim-pinch-gesture';
import { reportChartError } from './chart-error-log';
import type { GesturePreviewGroup } from './chart-gesture-preview';

// Half-width of the crosshair grab-handle's touch target (CSS px). A 1-finger drag
// starting this close to the crosshair scrubs the bar instead of panning the chart.
const HANDLE_GRAB_PX = 24;

// A pan must move at least this far (CSS px) before it previews/renders. Below it the gesture is
// treated as a click — so a jittery mousedown doesn't trigger the (expensive, in individual mode)
// preview render for a movement the user never intended.
const PAN_ENGAGE_PX = 3;

type ChartInstance = InstanceType<typeof Chart>;

/**
 * Shift a [min,max] range back inside [boundsMin,boundsMax], preserving span where possible.
 * If min < boundsMin, shift both up; then if max > boundsMax, shift both down. A range at least as
 * wide as the bounds can't sit inside them, so it collapses to the full [boundsMin,boundsMax] extent
 * — the result NEVER spills past a bound. (Every caller keeps span ≤ bounds span, so the wide case is
 * defensive; the old two-step-only form let min fall below boundsMin there, breaking the contract.)
 */
export function clampRangeToBounds(
	min: number,
	max: number,
	boundsMin: number,
	boundsMax: number,
): { min: number; max: number } {
	if (max - min >= boundsMax - boundsMin) return { min: boundsMin, max: boundsMax };
	if (min < boundsMin) { max += boundsMin - min; min = boundsMin; }
	if (max > boundsMax) { min -= max - boundsMax; max = boundsMax; }
	return { min, max };
}

/**
 * Standard X-only zoom plugin config — single source of truth shared by every chart.
 * `onZoom` fires on wheel zoom (e.g. redecimation and/or cross-chart sync).
 */
export function xZoomConfig(onZoom?: ((chart: ChartInstance) => void) | null, opts?: { wheel?: boolean }) {
	return {
		pan: { enabled: false },
		zoom: {
			// wheel:false → the chart uses attachWheelZoom (bitmap gesture preview) instead of the
			// plugin's per-tick re-render; the plugin stays configured for zoomScale/resetZoom state.
			wheel: { enabled: opts?.wheel !== false, speed: 0.1 },
			pinch: { enabled: false }, // handled by setupPanning's touch handler
			mode: "x",
			onZoom: onZoom ? ({ chart: c }: { chart: ChartInstance }) => onZoom(c) : undefined,
		},
		limits: { x: { min: "original", max: "original" } },
	};
}

/** Result of xZoomConfig() — for consumers that store the config in a typed field. */
export type XZoomConfigResult = ReturnType<typeof xZoomConfig>;

/**
 * Build the x-axis `afterFit` hook that stops tick-label reflow from resizing the plot during zoom
 * (anti-flicker). The height is pinned only while the layout width is unchanged — zoom keeps width
 * constant, so the flicker case is covered — but a real re-layout (window resize, panel expand,
 * rotation) changes the width and re-measures both, so the axis box can no longer be clipped/over-
 * padded to a stale height for the chart's lifetime (CG-06).
 */
export function makeAxisHeightLock(): (sc: { height: number; chart: { width: number } }) => void {
	let lockedH = -1;
	let lockedW = -1;
	return (sc) => {
		const w = sc.chart.width;
		if (lockedH < 0 || w !== lockedW) {
			lockedH = sc.height;
			lockedW = w;
		} else {
			sc.height = lockedH;
		}
	};
}

/**
 * Freeze the chart layout for X-only zoom:
 *  • pin every Y axis to the bounds Chart.js already computed for the full data, so it stops
 *    auto-rescaling while zooming X (keeps the natural auto-fit range — just frozen);
 *  • lock the X-axis box height so a tick label appearing/disappearing on zoom can't resize the
 *    plot area (anti-flicker).
 * Call after (re)building a chart at its full extent (before any zoom), while data is complete.
 * `skipUpdate` — the caller just ran chart.update() itself, so pinning options to the
 * already-rendered values needs no extra repaint (halves the cost of every axis re-fit).
 */
function freezeAxesForXZoom(chart: ChartInstance | null, skipUpdate?: boolean): void {
	if (!chart) return;
	try {
		for (const scale of Object.values(chart.scales)) {
			const s = scaleInternals(scale);
			if (s.axis !== "y") continue;
			// Reuse Chart.js's own full-data min/max (already nicely rounded, respects beginAtZero).
			if (typeof s.min === "number" && typeof s.max === "number" && Number.isFinite(s.min) && Number.isFinite(s.max)) {
				s.options.min = s.min;
				s.options.max = s.max;
			}
		}
		// Lock the x-axis box height so labels can't reflow the plot during zoom (re-measures on a
		// real width change — CG-06).
		const xOpts = chart.scales.x ? scaleInternals(chart.scales.x).options : undefined;
		if (xOpts && typeof xOpts.afterFit !== "function") {
			xOpts.afterFit = makeAxisHeightLock();
		}
		if (!skipUpdate) chart.update("none");
	} catch (e) {
		console.error("freezeAxesForXZoom failed:", e);
	}
}

/**
 * One-call finalize for any zoomable time/line/bar chart. Call once, right after `new Chart(...)`.
 *
 * This is the single home for the chart defaults that are easy to forget when adding a NEW chart —
 * route new charts through here instead of re-listing these by hand:
 *   • X-only drag + pinch panning              (setupPanning)
 *   • Freeze each Y axis during X-zoom          (freezeAxesForXZoom — stops the Y axis rescaling on zoom)
 *   • Last X tick "inner"-aligned               (last label can't overflow the canvas edge)
 *   • Tight Y tick padding                       (numbers sit close to the plot)
 *
 * Pass `previewGroup` for any chart in an x-synced group (SegmentStats, Details): gestures then
 * cost a bitmap draw per frame instead of a real re-render per chart — treat it as the default
 * for new zoomable chart pages, not an optional extra (omitting it = the measured F2 lag class).
 *
 * Per-chart specifics (tick callbacks, titles, colors, decimation) still live in each chart's own
 * config. The visual tweaks here are applied only when the chart hasn't already set them, so any
 * explicit per-chart config wins.
 */
export function finalizeChart(
	canvas: HTMLCanvasElement,
	chart: ChartInstance | null,
	chartName: string,
	onZoomCallback?: ((chart: ChartInstance) => void) | null,
	previewGroup?: GesturePreviewGroup | null,
): void {
	if (!chart) return;
	try {
		const scales = (chart.options as { scales?: Record<string, { ticks?: Record<string, unknown> }> }).scales;
		const xTicks = scales?.x?.ticks;
		if (xTicks && xTicks.align == null) xTicks.align = "inner";
		for (const scale of Object.values(chart.scales)) {
			const s = scaleInternals(scale);
			if (s.axis === "y" && s.options?.ticks && s.options.ticks.padding == null) {
				s.options.ticks.padding = 2;
			}
		}
	} catch (e) {
		console.error("chart finalize (visual defaults) failed:", e);
	}
	setupPanning(canvas, chart, chartName, onZoomCallback, previewGroup);
	freezeAxesForXZoom(chart); // also runs chart.update("none")
}

// Re-wiring a canvas (charts are destroyed + recreated on rebuild) aborts the previous
// listeners — otherwise stale handlers with closures over destroyed charts stack up.
const _panControllers = new WeakMap<HTMLElement, AbortController>();

// A canvas may host something draggable of its own ON TOP of the plot — the race speed chart's
// checkpoint rules are the case this exists for. The predicate is asked, at press time, whether the
// gesture starting at this x belongs to that something; true means pan yields the whole drag.
//
// Same shape as the crosshair grab-handle yield below (HANDLE_GRAB_PX), generalised: the owner of the
// draggable thing knows its hit-test, this file does not. Keyed by element like _panControllers, so it
// dies with the canvas and is not an ambient singleton.
const _panYields = new WeakMap<HTMLElement, (pixelX: number) => boolean>();

// The idle cursor, when the same owner has one to state. Pan rewrites canvas.style.cursor on EVERY
// mousemove (grab when zoomed, default otherwise), so an inline cursor set from outside survives exactly
// until the pointer moves — which is how the race chart's "click to place" crosshair silently became an
// arrow. This is the seam that lets the owner say what the idle cursor is instead of fighting for it.
const _panIdleCursors = new WeakMap<HTMLElement, string>();

/** Register (or clear, with null) this canvas's "that press is mine, do not pan" predicate. */
export function setPanYield(canvas: HTMLCanvasElement, predicate: ((pixelX: number) => boolean) | null): void {
	if (predicate) _panYields.set(canvas, predicate);
	else _panYields.delete(canvas);
}

/**
 * Claim this canvas's idle cursor, outranking pan's own grab/default. Null hands it back.
 *
 * It outranks rather than defers because the states that use it are MODES the user switched into — armed
 * placement — and a mode's affordance should not blink off because the chart happens to be zoomed.
 */
export function setPanIdleCursor(canvas: HTMLCanvasElement, cursor: string | null): void {
	if (cursor) _panIdleCursors.set(canvas, cursor);
	else _panIdleCursors.delete(canvas);
	// Apply at once: the cursor is read where the pointer already is, not only after it next moves.
	canvas.style.cursor = cursor ?? '';
}

/** True when the registered owner claims the press at `clientX`. Canvas-relative CSS px. */
function _yieldsPan(canvas: HTMLCanvasElement, clientX: number): boolean {
	const predicate = _panYields.get(canvas);
	if (!predicate) return false;
	try {
		return predicate(clientX - canvas.getBoundingClientRect().left);
	} catch (e) {
		// A throwing predicate must not wedge panning — the gesture is worth more than the claim.
		reportChartError("chart-zoom-pan:panYield", e);
		return false;
	}
}

/**
 * Attach click-drag panning to a chart canvas.
 * Pan is only active when the chart is zoomed in; at full zoom the cursor
 * stays default and no drag gesture is initiated.
 * `previewGroup` — gesture frames draw a bitmap preview instead of re-rendering
 * (see chart-gesture-preview.ts); the real render happens once, on release.
 */
function setupPanning(
	canvas: HTMLCanvasElement,
	chart: ChartInstance,
	chartName: string,
	onZoomCallback?: ((chart: ChartInstance) => void) | null,
	previewGroup?: GesturePreviewGroup | null,
): void {
	_panControllers.get(canvas)?.abort();
	const _ac = new AbortController();
	_panControllers.set(canvas, _ac);
	const _sig = { signal: _ac.signal };
	let isPanning = false;
	let panEngaged = false; // set once a pan passes PAN_ENGAGE_PX; gates the first preview/render
	let panStartX = 0;
	let panStartMin = 0;
	let panStartMax = 0;
	let originalMin = 0;
	let originalMax = 0;

	// Cached data bounds — computed once and reused by _isZoomed and
	// _getOriginalBounds instead of scanning the full labels/data arrays
	// on every mousemove (O(n) → O(1)).
	let _cachedDataMin = NaN;
	let _cachedDataMax = NaN;

	function _ensureBoundsCache(): void {
		if (!isNaN(_cachedDataMin)) return;
		const labels = chart.data.labels as number[] | undefined;
		if (labels && labels.length > 0) {
			// ?? NaN: i stays in-bounds; a NaN compares false and skips the assignment exactly as
			// the undefined comparison did.
			let mn = must(labels[0], 'non-empty (guarded above)'), mx = mn;
			for (let i = 1; i < labels.length; i++) {
				const v = labels[i] ?? NaN;
				if (v < mn) mn = v;
				if (v > mx) mx = v;
			}
			_cachedDataMin = mn;
			_cachedDataMax = mx;
		} else {
			let mn = Infinity, mx = -Infinity;
			(chart.data.datasets as {data?: unknown[]}[]).forEach((ds) => {
				(ds.data || []).forEach((pt: unknown) => {
					const x = pt !== null && typeof pt === "object" ? (pt as {x: number}).x : (pt as number);
					if (x < mn) mn = x;
					if (x > mx) mx = x;
				});
			});
			_cachedDataMin = isFinite(mn) ? mn : 0;
			_cachedDataMax = isFinite(mx) ? mx : 0;
		}
	}

	// Current view range: the preview group's pending range while a gesture (or its
	// apply-debounce) is in flight — the live scale is stale then — else the scale itself.
	function _viewRange(): { min: number; max: number } {
		if (previewGroup) return previewGroup.currentRange(chart);
		const xScale = must(chart.scales.x, 'x scale configured');
		return { min: xScale.min, max: xScale.max };
	}

	// Returns true when the current x-axis view is narrower than the full data range.
	function _isZoomed(): boolean {
		try {
			if (!chart.scales.x) return false;
			_ensureBoundsCache();
			const v = _viewRange();
			return v.max - v.min < (_cachedDataMax - _cachedDataMin) * 0.99;
		} catch (e) { // throttled: runs on every mousemove, so a broken scale must not spam
			reportChartError("chart-zoom-pan:isZoomed:" + chartName, e);
			return false;
		}
	}

	// Compute the original (full) data bounds for clamping pan.
	function _getOriginalBounds(): { min: number; max: number } {
		_ensureBoundsCache();
		return { min: _cachedDataMin, max: _cachedDataMax };
	}

	// rAF coalescing for pan — compute new bounds on every mousemove
	// (cheap math), but only call chart.zoomScale() once per frame.
	let _panRafId = 0;
	let _pendingMin = 0;
	let _pendingMax = 0;

	canvas.addEventListener("mousedown", (e: MouseEvent) => {
		if (e.button !== 0) return;
		if (!_isZoomed()) return;
		// Something on the canvas claims this press (a checkpoint rule) — yield the drag to its owner.
		if (_yieldsPan(canvas, e.clientX)) return;
		isPanning = true;
		panEngaged = false;
		setChartPanning(true);
		panStartX = e.clientX;
		const v = _viewRange();
		panStartMin = v.min;
		panStartMax = v.max;
		const bounds = _getOriginalBounds();
		originalMin = bounds.min;
		originalMax = bounds.max;
		previewGroup?.begin();
		canvas.style.cursor = "grabbing";
		e.preventDefault();
	}, _sig);

	canvas.addEventListener("mousemove", (e: MouseEvent) => {
		if (!isPanning) {
			// An owner-claimed cursor wins: it says the canvas is in a MODE, which outranks "you could pan".
			canvas.style.cursor = _panIdleCursors.get(canvas) ?? (_isZoomed() ? "grab" : "default");
			return;
		}

		// Compute new bounds (cheap math) on every event...
		const deltaX = e.clientX - panStartX;
		// Ignore sub-threshold jitter: a near-still mousedown shouldn't fire the (expensive in
		// individual mode) preview render for a pan the user didn't mean to make.
		if (!panEngaged) {
			if (Math.abs(deltaX) < PAN_ENGAGE_PX) return;
			panEngaged = true;
		}
		const chartArea = chart.chartArea;
		const dataRange = panStartMax - panStartMin;
		const pixelRange = chartArea.right - chartArea.left;
		const dataDelta = (deltaX / pixelRange) * dataRange;

		let newMin = panStartMin - dataDelta;
		let newMax = panStartMax - dataDelta;
		({ min: newMin, max: newMax } = clampRangeToBounds(newMin, newMax, originalMin, originalMax));

		// ...but only apply zoomScale() once per frame via rAF (the preview group
		// rAF-coalesces internally, so it's fed directly).
		_pendingMin = newMin;
		_pendingMax = newMax;
		if (previewGroup) {
			previewGroup.preview(newMin, newMax);
		} else if (!_panRafId) {
			_panRafId = requestAnimationFrame(() => {
				_panRafId = 0;
				chart.zoomScale("x", { min: _pendingMin, max: _pendingMax }, "none");
				if (onZoomCallback) onZoomCallback(chart);
			});
		}
		e.preventDefault();
	}, _sig);

	const stopPanning = () => {
		if (isPanning) {
			isPanning = false;
			setChartPanning(false);
			if (previewGroup) {
				previewGroup.end(0); // one real render, on release
			} else if (_panRafId) {
				cancelAnimationFrame(_panRafId);
				_panRafId = 0;
				// Apply final position
				chart.zoomScale("x", { min: _pendingMin, max: _pendingMax }, "none");
				if (onZoomCallback) onZoomCallback(chart);
			}
			canvas.style.cursor = _isZoomed() ? "grab" : "default";
		}
	};
	canvas.addEventListener("mouseup", stopPanning, _sig);
	canvas.addEventListener("mouseleave", () => {
		stopPanning();
		canvas.style.cursor = "default";
	}, _sig);

	// touch-action: pan-y — allows vertical page scroll at all times but tells the
	// browser not to handle pinch-zoom or horizontal pan, so those gestures fire
	// TouchEvents that our handlers below can process. Set directly on the canvas
	// in JS (more targeted than CSS on the container which blocked all scroll).
	canvas.style.touchAction = 'pan-y';
	// touch-action alone is ignored by iOS Safari for PAGE zoom — without this the pinch below
	// zoomed the chart on Android and the whole document on iPhone. See the helper's header.
	claimPinchGesture(canvas, { signal: _ac.signal });

	// ── Touch pan — one finger drag when zoomed in ──────────────────────
	canvas.addEventListener("touchstart", (e: TouchEvent) => {
		if (e.touches.length !== 1 || !_isZoomed()) return;
		const t0 = must(e.touches[0], 'length === 1 (guarded above)');
		// Something on the canvas claims this press (a checkpoint rule) — yield the drag to its owner.
		if (_yieldsPan(canvas, t0.clientX)) return;
		// Drag starts on the crosshair grab-handle → yield: the scrub handlers
		// (attached after this one, gated by isChartPanning) move the bar instead.
		const crosshairX = getCrosshairPixelX(chart);
		if (crosshairX != null) {
			const rect = canvas.getBoundingClientRect();
			if (Math.abs(t0.clientX - rect.left - crosshairX) <= HANDLE_GRAB_PX) return;
		}
		isPanning = true;
		setChartPanning(true);
		panStartX = t0.clientX;
		panEngaged = false;
		const v = _viewRange();
		panStartMin = v.min;
		panStartMax = v.max;
		const bounds = _getOriginalBounds();
		originalMin = bounds.min;
		originalMax = bounds.max;
		previewGroup?.begin();
	}, { passive: true, signal: _ac.signal });

	canvas.addEventListener("touchmove", (e: TouchEvent) => {
		if (!isPanning || e.touches.length !== 1) return;
		const deltaX = must(e.touches[0], 'length === 1 (guarded above)').clientX - panStartX;
		if (!panEngaged) {
			if (Math.abs(deltaX) < PAN_ENGAGE_PX) return;
			panEngaged = true;
		}
		const chartArea = chart.chartArea;
		const dataRange = panStartMax - panStartMin;
		const pixelRange = chartArea.right - chartArea.left;
		const dataDelta = (deltaX / pixelRange) * dataRange;
		let newMin = panStartMin - dataDelta;
		let newMax = panStartMax - dataDelta;
		({ min: newMin, max: newMax } = clampRangeToBounds(newMin, newMax, originalMin, originalMax));
		_pendingMin = newMin;
		_pendingMax = newMax;
		if (previewGroup) {
			previewGroup.preview(newMin, newMax);
		} else if (!_panRafId) {
			_panRafId = requestAnimationFrame(() => {
				_panRafId = 0;
				chart.zoomScale("x", { min: _pendingMin, max: _pendingMax }, "none");
				if (onZoomCallback) onZoomCallback(chart);
			});
		}
	}, { passive: true, signal: _ac.signal });

	const stopTouchPanning = () => {
		if (isPanning) {
			isPanning = false;
			setChartPanning(false);
			if (previewGroup) {
				previewGroup.end(0);
			} else if (_panRafId) {
				cancelAnimationFrame(_panRafId);
				_panRafId = 0;
				chart.zoomScale("x", { min: _pendingMin, max: _pendingMax }, "none");
				if (onZoomCallback) onZoomCallback(chart);
			}
		}
	};
	canvas.addEventListener("touchend", stopTouchPanning, { passive: true, signal: _ac.signal });
	canvas.addEventListener("touchcancel", stopTouchPanning, { passive: true, signal: _ac.signal });

	// ── Pinch zoom — two-finger spread/squeeze zooms the x-axis ─────────
	// Implemented with TouchEvents instead of relying on chartjs-plugin-zoom's
	// PointerEvent approach, which requires touch-action: none on the exact
	// target element and is inconsistent across mobile browsers.
	// pan-y (set above) blocks browser pinch-zoom so these events reach us.
	let _pinchActive = false;
	let _pinchStartDist = 0;
	let _pinchStartMid = 0;
	let _pinchStartMin = 0;
	let _pinchStartMax = 0;

	const _pinchDist = (e: TouchEvent): number => {
		const t0 = must(e.touches[0], 'two-finger gesture (caller-guarded)');
		const t1 = must(e.touches[1], 'two-finger gesture (caller-guarded)');
		return Math.hypot(t1.clientX - t0.clientX, t1.clientY - t0.clientY);
	};

	canvas.addEventListener("touchstart", (e: TouchEvent) => {
		if (e.touches.length !== 2) return;
		_pinchActive = true;
		isPanning = false;
		// A pinch is still an active gesture — keep hover suppressed (CG-03). The bare 1-finger
		// pan is being replaced, not ended.
		setChartPanning(true);
		if (_panRafId) { cancelAnimationFrame(_panRafId); _panRafId = 0; }

		_pinchStartDist = _pinchDist(e);
		const v = _viewRange();
		_pinchStartMin = v.min;
		_pinchStartMax = v.max;
		const rect = canvas.getBoundingClientRect();
		const t0 = must(e.touches[0], 'length === 2 (guarded above)');
		const t1 = must(e.touches[1], 'length === 2 (guarded above)');
		const midPixelX = (t0.clientX + t1.clientX) / 2 - rect.left;
		if (previewGroup && chart.chartArea) {
			// The live scale is stale during a preview gesture — resolve the midpoint
			// linearly from the pending view range instead (x is a linear scale).
			const area = chart.chartArea;
			const frac = (midPixelX - area.left) / (area.right - area.left);
			_pinchStartMid = _pinchStartMin + frac * (_pinchStartMax - _pinchStartMin);
			previewGroup.begin();
		} else {
			_pinchStartMid = must(chart.scales.x, 'x scale configured').getValueForPixel(midPixelX)
				?? (_pinchStartMin + (_pinchStartMax - _pinchStartMin) / 2);
		}
		// touchstart stays local — claimPinchGesture only refuses touchmove and the iOS gesture*
		// events. Preventing touchstart is what stops the browser starting its own scroll from
		// the second finger, and it must NOT be widened to one-finger touches (that kills the
		// compatibility click, the bug the map gesture handler documents).
		e.preventDefault();
	}, { passive: false, signal: _ac.signal });

	canvas.addEventListener("touchmove", (e: TouchEvent) => {
		if (!_pinchActive || e.touches.length !== 2) return;
		const currentDist = _pinchDist(e);
		if (_pinchStartDist === 0) return;

		// Spreading fingers (currentDist > start) → zoom in (show less data).
		const scale = _pinchStartDist / currentDist;
		const origRange = _pinchStartMax - _pinchStartMin;
		const newRange = origRange * scale;

		// Keep the pinch midpoint stationary in data space.
		const fracLeft = (_pinchStartMid - _pinchStartMin) / origRange;
		let newMin = _pinchStartMid - fracLeft * newRange;
		let newMax = _pinchStartMid + (1 - fracLeft) * newRange;

		_ensureBoundsCache();
		const totalRange = _cachedDataMax - _cachedDataMin;
		if (newMax - newMin < totalRange * 0.01) return; // prevent over-zoom
		if (newMax - newMin >= totalRange) {
			// Zoomed out past 100% — clamp to full view
			newMin = _cachedDataMin;
			newMax = _cachedDataMax;
		} else {
			({ min: newMin, max: newMax } = clampRangeToBounds(newMin, newMax, _cachedDataMin, _cachedDataMax));
		}

		_pendingMin = newMin;
		_pendingMax = newMax;
		if (previewGroup) {
			previewGroup.preview(newMin, newMax);
		} else if (!_panRafId) {
			_panRafId = requestAnimationFrame(() => {
				_panRafId = 0;
				chart.zoomScale("x", { min: _pendingMin, max: _pendingMax }, "none");
				if (onZoomCallback) onZoomCallback(chart);
			});
		}
		// No preventDefault here — claimPinchGesture refuses every multi-touch touchmove on this
		// canvas, including the paths this handler early-returns from (over-zoom clamp), which
		// used to leak the gesture back to the browser.
	}, { passive: false, signal: _ac.signal });

	const _endPinch = (): void => {
		if (!_pinchActive) return;
		_pinchActive = false;
		// Preview path resets the flag via end(0)→_apply→cancel(); the non-preview path must
		// clear it here or hover stays suppressed for the rest of the page (CG-03).
		if (previewGroup) previewGroup.end(0);
		else setChartPanning(false);
	};
	canvas.addEventListener("touchend",   (e: TouchEvent) => { if (e.touches.length < 2) _endPinch(); }, { passive: true, signal: _ac.signal });
	canvas.addEventListener("touchcancel", _endPinch, { passive: true, signal: _ac.signal });

	// CG-01: capture the full-data bounds eagerly, while the chart is at its full extent (finalizeChart
	// is called right after build, before any zoom). A wheel-zoom before the first mousemove would
	// otherwise cache the redecimated window as "full", permanently disabling pan / pinch-out.
	_ensureBoundsCache();
}
