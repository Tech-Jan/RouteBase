/**
 * shape-filter/shape-interactions.ts — pointer input: draw / move / resize zones + gates.
 *
 * Attaches to the overlay element and mutates the store. No rendering, no network.
 * While enabled the overlay captures pointer events (so the map does not pan);
 * while disabled it is transparent to pointer events and the map behaves normally.
 *
 * Gate drawing is a drag (press at endpoint A, drag to B, release) rather than the two
 * separate taps sketched in the design doc — it reuses the same single-gesture capture
 * as zone drawing instead of a cross-gesture state machine, and works identically with
 * mouse and touch.
 */

import type { MapAdapter } from './map-adapter';
import { must } from '../../core/invariant';
import type { ShapeStore } from './shape-store';
import type { CrossDirection, GateRole, LatLng, ZoneRole } from './shape-model';
import { gateFromPoints, zoneBoundsFromAnchor, zoneFromCorners, zoneResizeAnchor } from './shape-model';
import { gateScreenLine, handleCentersFor, HANDLE_SIZE, shapeScreenRect, type HandleId } from './shape-layer';

export type Tool = 'select' | 'draw-zone' | 'draw-gate';

/** Where the just-selected shape sits in the overlap stack under the pointer (for a "(2/3)" hint). */
export interface CycleInfo {
	index: number;
	total: number;
}

interface Deps {
	getTool: () => Tool;
	getSelectedId: () => string | null;
	onSelect: (id: string | null, cycle?: CycleInfo) => void;
	onDrawFinished: (placed: boolean) => void;
	getDefaultZoneRole: () => ZoneRole;
	getDefaultGateRole: () => GateRole;
	getDefaultGateDirection: () => CrossDirection;
}

type Gesture =
	| { kind: 'draw-zone'; id: string; start: LatLng }
	| { kind: 'draw-gate'; id: string; start: LatLng }
	| { kind: 'move-zone'; id: string; grab: LatLng; orig: { s: number; w: number; n: number; e: number } }
	| { kind: 'move-gate'; id: string; grab: LatLng; orig: { a: LatLng; b: LatLng } }
	| { kind: 'resize'; id: string; handle: HandleId; zoneAnchor: LatLng | null };

const MIN_DRAW_PIXELS = 4;
const GATE_HIT_PIXELS = 11; // ≈ half the fat hit-line stroke, so tapping near a gate selects it
const CYCLE_TAP_PIXELS = 8; // repeat taps within this radius cycle through the overlap stack

/** Shortest distance from point p to segment a–b, in the same (pixel) units as its inputs. */
function distToSegment(p: { x: number; y: number }, a: { x: number; y: number }, b: { x: number; y: number }): number {
	const dx = b.x - a.x, dy = b.y - a.y;
	const lenSq = dx * dx + dy * dy;
	if (lenSq === 0) return Math.hypot(p.x - a.x, p.y - a.y);
	const t = Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / lenSq));
	return Math.hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy));
}

export class ShapeInteractions {
	private gesture: Gesture | null = null;
	private enabled = false;

	// Overlap click-to-cycle state, captured at pointerdown and read at pointerup.
	private downPx: { x: number; y: number } | null = null;
	private downStack: string[] = [];
	private downSelectionChanged = false;
	// Anchor of the last clean tap; a nearby repeat tap advances through the stack. A real
	// drag (move/resize) clears it so cycling never fights dragging.
	private lastTapPx: { x: number; y: number } | null = null;

	constructor(
		private readonly overlay: HTMLElement,
		private readonly adapter: MapAdapter,
		private readonly store: ShapeStore,
		private readonly deps: Deps,
	) {
		this.overlay.addEventListener('pointerdown', this.onPointerDown);
		this.overlay.addEventListener('pointermove', this.onPointerMove);
		this.overlay.addEventListener('pointerup', this.onPointerUp);
		this.overlay.addEventListener('pointercancel', this.onPointerUp);
		// Empty-space taps never reach the overlay in select mode (it is click-through so the map
		// pans), so deselect is caught here in the capture phase — before the map's own drag starts,
		// and without preventDefault so the press still pans.
		this.adapter.getContainer().addEventListener('pointerdown', this.onContainerPointerDownCapture, true);
	}

	enable(): void {
		this.enabled = true;
		this.lastTapPx = null; // fresh tool arm → no stale cycle anchor
		// In select mode only the shapes themselves are clickable (CSS), so empty space
		// still pans the map; in draw mode the whole overlay captures.
		this.overlay.classList.toggle('shape-filter-overlay--select', this.deps.getTool() === 'select');
		this.applyToolResting();
	}

	disable(): void {
		this.enabled = false;
		this.gesture = null;
		this.lastTapPx = null;
		this.overlay.classList.remove('shape-filter-overlay--select');
		this.overlay.style.pointerEvents = 'none';
		this.adapter.setInteractive(true); // hand pan/zoom back to the map
	}

	/** Clear the cycle anchor so the next tap starts fresh (used by an Escape-key deselect). */
	resetCycle(): void {
		this.lastTapPx = null;
	}

	/**
	 * The resting pointer/map configuration for the current tool (between gestures):
	 *  - draw-*  → overlay captures everything, map locked (every drag draws).
	 *  - select → overlay is click-through, map free (empty space pans; shapes catch taps).
	 */
	private applyToolResting(): void {
		if (this.deps.getTool() === 'select') {
			this.overlay.style.pointerEvents = 'none';
			this.adapter.setInteractive(true);
		} else {
			this.overlay.style.pointerEvents = 'auto';
			this.adapter.setInteractive(false);
		}
	}

	/** Take ownership of a gesture: capture the pointer and freeze the map until pointerup. */
	private beginCapture(e: PointerEvent): void {
		this.overlay.style.pointerEvents = 'auto';
		this.adapter.setInteractive(false);
		try {
			this.overlay.setPointerCapture(e.pointerId);
		} catch {
			/* capture unsupported / pointer gone */
		}
	}

	destroy(): void {
		this.overlay.removeEventListener('pointerdown', this.onPointerDown);
		this.overlay.removeEventListener('pointermove', this.onPointerMove);
		this.overlay.removeEventListener('pointerup', this.onPointerUp);
		this.overlay.removeEventListener('pointercancel', this.onPointerUp);
		this.adapter.getContainer().removeEventListener('pointerdown', this.onContainerPointerDownCapture, true);
	}

	// ── pointer → container-relative pixel → geographic ──────────────────────

	private toPixel(e: PointerEvent): { x: number; y: number } {
		const rect = this.adapter.getContainer().getBoundingClientRect();
		return { x: e.clientX - rect.left, y: e.clientY - rect.top };
	}

	private toLatLng(e: PointerEvent): LatLng {
		const p = this.toPixel(e);
		return this.adapter.unproject(p.x, p.y);
	}

	private hitHandle(px: { x: number; y: number }): HandleId | null {
		const id = this.deps.getSelectedId();
		if (!id) return null;
		const shape = this.store.get(id);
		if (!shape) return null;
		const centers = handleCentersFor(shape, this.adapter);
		for (const h of Object.keys(centers) as HandleId[]) {
			const c = centers[h];
			if (c && Math.abs(px.x - c.x) <= HANDLE_SIZE && Math.abs(px.y - c.y) <= HANDLE_SIZE) return h;
		}
		return null;
	}

	/** Every shape under the pointer, smallest hit-area first (a gate crossing a zone outranks
	 * the zone; ties → later-drawn first) — so a click cycles through the stack front-to-back. */
	private hitStack(px: { x: number; y: number }): string[] {
		const hits: { id: string; area: number; order: number }[] = [];
		this.store.getAll().forEach((s, order) => {
			if (s.kind === 'zone') {
				const r = shapeScreenRect(s, this.adapter);
				if (px.x >= r.x && px.x <= r.x + r.w && px.y >= r.y && px.y <= r.y + r.h)
					hits.push({ id: s.id, area: r.w * r.h, order });
			} else {
				const line = gateScreenLine(s, this.adapter);
				if (distToSegment(px, line.a, line.b) <= GATE_HIT_PIXELS) {
					const len = Math.hypot(line.b.x - line.a.x, line.b.y - line.a.y);
					hits.push({ id: s.id, area: len * 2 * GATE_HIT_PIXELS, order });
				}
			}
		});
		hits.sort((a, b) => a.area - b.area || b.order - a.order);
		return hits.map((h) => h.id);
	}

	// ── gesture handlers ─────────────────────────────────────────────────────

	private onPointerDown = (e: PointerEvent): void => {
		if (!this.enabled) return;
		const px = this.toPixel(e);
		const ll = this.adapter.unproject(px.x, px.y);
		const handle = this.hitHandle(px);
		this.downPx = px;
		this.downStack = [];
		this.downSelectionChanged = false;

		// Handles beat everything (both modes) — grabbing one always resizes the selection.
		if (handle) {
			this.own(e);
			const id = must(this.deps.getSelectedId(), 'a resize handle implies a selection');
			const shape = this.store.get(id);
			const zoneAnchor =
				shape?.kind === 'zone' && (handle === 'nw' || handle === 'ne' || handle === 'sw' || handle === 'se')
					? zoneResizeAnchor(shape, handle)
					: null;
			this.gesture = { kind: 'resize', id, handle, zoneAnchor };
			return;
		}

		if (this.deps.getTool() === 'select') {
			// Empty space in select mode: let the press pan the map (the container listener deselects).
			const stack = this.hitStack(px);
			if (stack.length === 0) return;
			this.own(e);
			this.downStack = stack;
			// Keep the current selection if it's in the stack (a body-drag of the selected shape beats
			// the shapes below); otherwise select the smallest one under the pointer.
			const current = this.deps.getSelectedId();
			const keep = current !== null && stack.includes(current);
			const id = keep && current !== null ? current : must(stack[0], 'non-empty stack (guarded)');
			this.downSelectionChanged = !keep;
			if (!keep) this.deps.onSelect(id, { index: stack.indexOf(id), total: stack.length });
			this.armMove(id, ll);
			return;
		}

		// Draw mode: a press on an existing shape moves it; empty space starts a new shape.
		this.own(e);
		const shapeId = this.hitStack(px)[0] ?? null;
		if (shapeId) {
			this.deps.onSelect(shapeId);
			this.armMove(shapeId, ll);
			return;
		}
		const tool = this.deps.getTool();
		if (tool === 'draw-zone') {
			const shape = zoneFromCorners(ll, ll, this.deps.getDefaultZoneRole());
			this.store.add(shape);
			this.deps.onSelect(shape.id);
			this.gesture = { kind: 'draw-zone', id: shape.id, start: ll };
		} else if (tool === 'draw-gate') {
			const shape = gateFromPoints(ll, ll, this.deps.getDefaultGateRole(), this.deps.getDefaultGateDirection());
			this.store.add(shape);
			this.deps.onSelect(shape.id);
			this.gesture = { kind: 'draw-gate', id: shape.id, start: ll };
		}
	};

	/** We own this gesture — capture the pointer and freeze the map until pointerup. */
	private own(e: PointerEvent): void {
		e.preventDefault();
		e.stopPropagation();
		this.beginCapture(e);
	}

	/** Arm a move gesture on the given shape, grabbed at `ll`. */
	private armMove(id: string, ll: LatLng): void {
		const s = must(this.store.get(id), 'armMove id from hit stack / live selection');
		this.gesture =
			s.kind === 'zone'
				? { kind: 'move-zone', id, grab: ll, orig: { s: s.south, w: s.west, n: s.north, e: s.east } }
				: { kind: 'move-gate', id, grab: ll, orig: { a: s.a, b: s.b } };
	}

	/** Empty-space deselect in select mode (capture phase on the map container, no preventDefault so
	 * the press still pans). Fires before the overlay: a shape/handle tap is left to onPointerDown. */
	private onContainerPointerDownCapture = (e: PointerEvent): void => {
		if (!this.enabled || this.deps.getTool() !== 'select') return;
		if (this.deps.getSelectedId() === null) return;
		const px = this.toPixel(e);
		if (this.hitHandle(px) || this.hitStack(px).length > 0) return;
		this.deps.onSelect(null);
		this.lastTapPx = null;
	};

	private onPointerMove = (e: PointerEvent): void => {
		if (!this.enabled || !this.gesture) return;
		const ll = this.toLatLng(e);
		const g = this.gesture;

		if (g.kind === 'draw-zone') {
			this.store.updateZone(g.id, {
				south: Math.min(g.start.lat, ll.lat),
				north: Math.max(g.start.lat, ll.lat),
				west: Math.min(g.start.lng, ll.lng),
				east: Math.max(g.start.lng, ll.lng),
			});
		} else if (g.kind === 'draw-gate') {
			this.store.updateGate(g.id, { b: ll });
		} else if (g.kind === 'move-zone') {
			const dLat = ll.lat - g.grab.lat;
			const dLng = ll.lng - g.grab.lng;
			this.store.updateZone(g.id, {
				south: g.orig.s + dLat,
				north: g.orig.n + dLat,
				west: g.orig.w + dLng,
				east: g.orig.e + dLng,
			});
		} else if (g.kind === 'move-gate') {
			const dLat = ll.lat - g.grab.lat;
			const dLng = ll.lng - g.grab.lng;
			this.store.updateGate(g.id, {
				a: { lat: g.orig.a.lat + dLat, lng: g.orig.a.lng + dLng },
				b: { lat: g.orig.b.lat + dLat, lng: g.orig.b.lng + dLng },
			});
		} else {
			this.resize(g.id, g.handle, ll, g.zoneAnchor);
		}
	};

	/** Dispatch a handle drag to the matching kind's typed update. Zones recompute the whole rect
	 * from the fixed anchor + live cursor (never a per-field patch — see SG-01); gates move the
	 * grabbed endpoint. Resize handles (nw/ne/sw/se vs a/b) never overlap between kinds. */
	private resize(id: string, handle: HandleId, ll: LatLng, zoneAnchor: LatLng | null): void {
		const shape = this.store.get(id);
		if (!shape) return;
		if (shape.kind === 'zone' && zoneAnchor) {
			this.store.updateZone(id, zoneBoundsFromAnchor(zoneAnchor, ll));
		} else if (shape.kind === 'gate' && (handle === 'a' || handle === 'b')) {
			this.store.updateGate(id, handle === 'a' ? { a: ll } : { b: ll });
		}
	}

	/**
	 * Resolve the end of a select-mode press. A real drag (moved ≥ MIN_DRAW_PIXELS) already moved
	 * the shape and just clears the cycle anchor. A clean tap that repeats near the last tap advances
	 * the selection through the stack + a null step (reliable deselect, then wraps) — but only when
	 * pointerdown kept the existing selection, so a fresh pick doesn't double-step past it.
	 */
	private finishSelectTap(e: PointerEvent): void {
		const px = this.toPixel(e);
		const moved = this.downPx ? Math.hypot(px.x - this.downPx.x, px.y - this.downPx.y) : 0;
		if (moved >= MIN_DRAW_PIXELS) { this.lastTapPx = null; return; }

		const current = this.deps.getSelectedId();
		const near = this.lastTapPx !== null && Math.hypot(px.x - this.lastTapPx.x, px.y - this.lastTapPx.y) <= CYCLE_TAP_PIXELS;
		if (near && !this.downSelectionChanged && current !== null && this.downStack.includes(current)) {
			const cycle: (string | null)[] = [...this.downStack, null];
			const next = cycle[(cycle.indexOf(current) + 1) % cycle.length];
			if (next == null) this.deps.onSelect(null); // undefined impossible (modulo of non-empty cycle) — folds into deselect
			else this.deps.onSelect(next, { index: this.downStack.indexOf(next), total: this.downStack.length });
		}
		this.lastTapPx = px;
	}

	private onPointerUp = (e: PointerEvent): void => {
		if (!this.gesture) return;
		try {
			this.overlay.releasePointerCapture(e.pointerId);
		} catch {
			/* pointer already released */
		}
		const g = this.gesture;
		this.gesture = null;

		// Drop back to the current tool's resting state (in select mode this re-frees the
		// map so a pan can follow a move/resize immediately).
		this.applyToolResting();

		if (g.kind === 'move-zone' || g.kind === 'move-gate') { this.finishSelectTap(e); return; }
		if (g.kind === 'resize') { this.lastTapPx = null; return; } // a resize breaks the cycle anchor

		let placed = false;
		const shape = this.store.get(g.id);
		if (shape) {
			const tooSmall =
				shape.kind === 'zone'
					? (() => { const r = shapeScreenRect(shape, this.adapter); return r.w < MIN_DRAW_PIXELS || r.h < MIN_DRAW_PIXELS; })()
					: (() => { const l = gateScreenLine(shape, this.adapter); return Math.hypot(l.b.x - l.a.x, l.b.y - l.a.y) < MIN_DRAW_PIXELS; })();
			if (tooSmall) {
				this.store.remove(g.id); // a click, not a drag
				this.deps.onSelect(null);
			} else {
				placed = true;
			}
		}
		this.deps.onDrawFinished(placed); // when placed, the orchestrator disarms to navigation
	};
}
