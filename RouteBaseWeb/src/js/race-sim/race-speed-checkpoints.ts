/**
 * Placing and dragging section boundaries ON THE SPEED CHART.
 *
 * This is the same job `race-profile.ts` does for the dock strip, against a Chart.js canvas instead of a
 * hand-drawn one — and it is deliberately the same RULES, not a second set: `snapCheckpoint`,
 * `checkpointBoundsFor` and `roleOf` are imported from `race-clock.ts`, so a spot this surface offers is
 * one the strip would offer and one the server will accept. Only the pixel↔value conversion differs, and
 * that goes through `race-speed-backdrop`'s `percentOf`/`metresOf` pair.
 *
 * **Why a click here is an unambiguous chainage.** The strip has always owned placement because its x-axis
 * IS chainage, while a map pixel on a course that passes the same place seven times is seven chainages
 * (race-profile.ts). This chart's x is `chainage / totalMeters * 100` — a linear reparametrisation of the
 * same quantity — so it inherits that property exactly. Placement moved here when the strip became
 * fullscreen-only; it did not become approximate on the way.
 *
 * ## Living with the chart's own gestures
 *
 * The canvas already has three pointer stacks on it: drag/pinch pan (`chart-zoom-pan`), wheel zoom, and
 * the crosshair scrub. Pan yields to this one through `setPanYield`, asked at PRESS time with the same
 * `checkpointAtPixel` hit-test this layer uses — the two must agree, or a press that stops the pan and
 * then misses the drag is a dead gesture. Pan is also inactive at full zoom, so in the common case there
 * is nothing to yield.
 */

import { scaleInternals } from '../core/chart-internals';
import { setPanIdleCursor, setPanYield } from '../core/chart-zoom-pan';
import { checkpointBoundsFor, roleOf, snapCheckpoint } from './race-clock';
import { checkpointAtPixel, metresOf } from './race-speed-backdrop';
import type { ChartJsInstance } from '../core/core-types';
import type { RaceCheckpointDto } from './race-api';

/** How near a rule a press has to land to grab it. Generous — it is a 1 px line with a 10 px foot. */
const GRAB_PX = 12;
/** Past this the press is a drag, not a click. Matches chart-zoom-pan's PAN_ENGAGE_PX. */
const DRAG_ENGAGE_PX = 3;

export interface RaceSpeedCheckpointsOptions {
	readonly canvas: HTMLCanvasElement;
	/** The live chart — read fresh, never captured: the panel rebuilds it. */
	readonly chart: () => ChartJsInstance | null;
	/** The course grid's length. x is a percentage OF this. */
	readonly totalMeters: number;
	/** The draft checkpoints, read live — a drag moves one locally before the server hears about it. */
	readonly checkpoints: () => readonly RaceCheckpointDto[];
	/** A boundary moved locally during a drag; the backdrop repaints from it. */
	readonly onLocalMove: (checkpointId: number, chainageMeters: number) => void;
	/** Placing and dragging are writes, so only an editor gets them. A viewer still gets `onSelect`. */
	readonly editable: boolean;
	readonly onPlace?: ((chainageMeters: number) => void) | undefined;
	readonly onMove?: ((checkpointId: number, chainageMeters: number) => void) | undefined;
	readonly onSelect?: ((checkpointId: number) => void) | undefined;
}

export interface RaceSpeedCheckpoints {
	/** Arm placement: only while armed does a click on an empty stretch place a checkpoint. */
	setPlaceable(armed: boolean): void;
	destroy(): void;
}

export function createRaceSpeedCheckpoints(options: RaceSpeedCheckpointsOptions): RaceSpeedCheckpoints {
	const { canvas, totalMeters } = options;
	const controller = new AbortController();
	const signal = controller.signal;

	let placeable = false;
	/** The checkpoint under the finger, or null when this press is not a drag. */
	let dragging: RaceCheckpointDto | null = null;
	/** Where the drag started (metres), so a drag that never found a legal spot writes nothing. */
	let dragOrigin: number | null = null;
	/** Where the press landed, in canvas px. NaN between gestures — a release with no press places nothing. */
	let pressX = Number.NaN;
	let engaged = false;

	/** Pointer capture is not universal (and jsdom has no real implementation) — never assume it. */
	function capture(pointerId: number, on: boolean): void {
		try {
			if (on) canvas.setPointerCapture?.(pointerId);
			else if (canvas.hasPointerCapture?.(pointerId)) canvas.releasePointerCapture?.(pointerId);
		} catch { /* an unknown pointerId — the gesture still works, it just is not captured */ }
	}

	/** Canvas-relative CSS px from a client x. */
	function localX(clientX: number): number {
		return clientX - canvas.getBoundingClientRect().left;
	}

	function hit(pixelX: number): RaceCheckpointDto | null {
		return checkpointAtPixel(options.chart(), options.checkpoints(), pixelX, totalMeters, GRAB_PX);
	}

	/** Metres for a canvas x, or null when the pointer is off the plot or the axis is not laid out yet. */
	function metresAt(pixelX: number): number | null {
		const chart = options.chart();
		const area = chart?.chartArea;
		const xScale = chart?.scales?.x ? scaleInternals(chart.scales.x) : undefined;
		if (!area || typeof xScale?.getValueForPixel !== 'function') return null;
		if (pixelX < area.left || pixelX > area.right) return null;
		const percent = xScale.getValueForPixel(pixelX);
		if (percent == null || !Number.isFinite(percent)) return null;
		return metresOf(percent, totalMeters);
	}

	/** Where this checkpoint may be dragged to, against every other one and the grid's length. */
	function boundsOf(target: RaceCheckpointDto): ReturnType<typeof checkpointBoundsFor> {
		const all = options.checkpoints();
		const others = all.filter((c) => c.checkpointId !== target.checkpointId).map((c) => c.chainageMeters);
		return checkpointBoundsFor(roleOf(target, all), others, totalMeters);
	}

	// Pan asks this at press time. Same hit-test as onPointerDown below, on purpose — see the header.
	setPanYield(canvas, (pixelX) => options.editable && hit(pixelX) != null);

	function onPointerDown(event: PointerEvent): void {
		if (event.button !== 0) return;
		const x = localX(event.clientX);
		if (metresAt(x) == null) return;   // outside the plot — axis furniture, not the course
		pressX = x;
		engaged = false;

		const target = hit(x);
		if (!target) return;

		// Selecting is a read, so a viewer gets it; only an editor goes on to drag.
		options.onSelect?.(target.checkpointId);
		if (!options.editable) return;
		dragging = target;
		dragOrigin = target.chainageMeters;
		// Capture, so a drag that leaves the canvas (past the plot's edge) still ends on THIS element.
		capture(event.pointerId, true);
		// Also what keeps the pan off a MOUSE drag: preventDefault on pointerdown suppresses the
		// compatibility mousedown that chart-zoom-pan listens on. The touch path is covered by setPanYield.
		event.preventDefault();
	}

	function onPointerMove(event: PointerEvent): void {
		if (Number.isNaN(pressX)) return;
		const x = localX(event.clientX);
		if (!engaged) {
			if (Math.abs(x - pressX) < DRAG_ENGAGE_PX) return;
			engaged = true;
		}
		// A press on empty space that turned into a drag is the chart being panned, not a placement.
		// `engaged` is what a release reads to know that; there is nothing else to do for it here.
		if (!dragging) return;
		const metres = metresAt(x);
		if (metres == null) return;

		const others = options.checkpoints().filter((c) => c.checkpointId !== dragging?.checkpointId);
		const snapped = snapCheckpoint(metres, others, boundsOf(dragging));
		// null = nowhere legal here; freeze at the last good spot rather than following the pointer off
		// the course. Same behaviour as the strip.
		if (snapped == null) return;
		options.onLocalMove(dragging.checkpointId, snapped);
	}

	function onPointerUp(event: PointerEvent): void {
		const target = dragging;
		const from = dragOrigin;
		const wasEngaged = engaged;
		const hadPress = !Number.isNaN(pressX);
		dragging = null;
		dragOrigin = null;
		engaged = false;
		pressX = Number.NaN;
		capture(event.pointerId, false);
		if (!hadPress) return;

		if (target) {
			// Committed from the LIVE draft, not from the captured object: onLocalMove wrote into the
			// editor's draft, and that is the authority on where the boundary now is.
			const live = options.checkpoints().find((c) => c.checkpointId === target.checkpointId);
			// A drag whose every position was refused moved nothing — say nothing.
			if (wasEngaged && live && live.chainageMeters !== from) {
				options.onMove?.(target.checkpointId, live.chainageMeters);
			}
			return;
		}

		// Not a boundary: a click on an empty stretch, which places only while the Sections editor armed it.
		// `wasEngaged` here means the press travelled — that is the chart being panned, not a placement.
		if (wasEngaged || !options.editable || !placeable) return;
		const metres = metresAt(localX(event.clientX));
		if (metres == null) return;
		// A new checkpoint is an intermediate: strictly inside the two lines, and snapCheckpoint refuses a
		// spot on top of an existing one — two at one chainage is a zero-length interval, which is a
		// divide-by-zero in the pacing model.
		const all = options.checkpoints();
		const snapped = snapCheckpoint(
			metres, all, checkpointBoundsFor('intermediate', all.map((c) => c.chainageMeters), totalMeters));
		if (snapped != null) options.onPlace?.(snapped);
	}

	canvas.addEventListener('pointerdown', onPointerDown, { signal });
	canvas.addEventListener('pointermove', onPointerMove, { signal });
	canvas.addEventListener('pointerup', onPointerUp, { signal });
	// A cancelled pointer (a system gesture, a scroll takeover) must not leave a drag half-open — the next
	// pointermove would then move a boundary the user is no longer holding.
	canvas.addEventListener('pointercancel', () => {
		dragging = null;
		dragOrigin = null;
		engaged = false;
		pressX = Number.NaN;
	}, { signal });

	return {
		setPlaceable(armed) {
			placeable = armed;
			// A crosshair says "click to place". Through the pan seam and not canvas.style, because pan
			// rewrites the cursor on every mousemove — an inline one lasts until the pointer twitches.
			setPanIdleCursor(canvas, armed ? 'crosshair' : null);
		},
		destroy() {
			controller.abort();
			setPanYield(canvas, null);
			setPanIdleCursor(canvas, null);
		},
	};
}
