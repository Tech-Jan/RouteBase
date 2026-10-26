/**
 * shape-filter/shape-layer.ts — renders zones + gates as an SVG overlay.
 *
 * Pure rendering: it reads the store + projects through the MapAdapter and draws.
 * It never handles input (that is shape-interactions) — it only exposes handle
 * geometry so the interaction layer can hit-test consistently.
 */

import type { MapAdapter } from './map-adapter';
import type { ShapeStore } from './shape-store';
import type { GateShape, Shape, ZoneRole, ZoneShape } from './shape-model';
import { badgeLabel } from './shape-model';

const SVG_NS = 'http://www.w3.org/2000/svg';

/** Suffix appended to a zone's badge to signal its role at a glance (empty for the plain roles). */
const zoneRoleMarker: Partial<Record<ZoneRole, string>> = {
	avoid: ' ⛔',
	start: ' 🚩',
	finish: ' 🏁',
	either: ' ⚑',
};
export const HANDLE_SIZE = 10;
const ARROW_LENGTH = 12;
const ARROW_WIDTH = 10;
const TICK_LENGTH = 14;
/** How far along its own normal a gate is probed to derive the forward direction, as a fraction of
 * the gate's length. Short enough that the camera cannot bend the direction over the span, long
 * enough that the projected delta stays numerically stable at any zoom. */
const NORMAL_PROBE = 0.25;

export type ZoneHandleId = 'nw' | 'ne' | 'sw' | 'se';
export type GateHandleId = 'a' | 'b';
export type HandleId = ZoneHandleId | GateHandleId;

export interface ScreenPoint {
	x: number;
	y: number;
}

/** Screen-space rectangle of a zone, used by both rendering and hit-testing. */
export interface ScreenRect {
	x: number;
	y: number;
	w: number;
	h: number;
}

export interface ScreenLine {
	a: ScreenPoint;
	b: ScreenPoint;
}

/** Project a zone's geographic bounds to a container-pixel rectangle. Projects all four corners
 * and takes their true bounding box: under pitch/bearing (the app's terrain default is 10° pitch,
 * and dragRotate is live) the two-corner shortcut mis-bounds the tilted quad — SG-02. At
 * bearing=0,pitch=0 the four corners already form an axis-aligned rect, so this is byte-identical
 * to the old min/max-of-two-corners answer. */
export function shapeScreenRect(shape: ZoneShape, adapter: MapAdapter): ScreenRect {
	const corners = [
		adapter.project(shape.north, shape.west),
		adapter.project(shape.north, shape.east),
		adapter.project(shape.south, shape.west),
		adapter.project(shape.south, shape.east),
	];
	const xs = corners.map((c) => c.x);
	const ys = corners.map((c) => c.y);
	const x = Math.min(...xs), y = Math.min(...ys);
	return { x, y, w: Math.max(...xs) - x, h: Math.max(...ys) - y };
}

/** Project a gate's two endpoints to container pixels. */
export function gateScreenLine(shape: GateShape, adapter: MapAdapter): ScreenLine {
	return { a: adapter.project(shape.a.lat, shape.a.lng), b: adapter.project(shape.b.lat, shape.b.lng) };
}

/** Corner handle centres for a screen rectangle. */
export function zoneHandleCenters(r: ScreenRect): Record<ZoneHandleId, ScreenPoint> {
	return {
		nw: { x: r.x, y: r.y },
		ne: { x: r.x + r.w, y: r.y },
		sw: { x: r.x, y: r.y + r.h },
		se: { x: r.x + r.w, y: r.y + r.h },
	};
}

/** Endpoint handle centres for a screen line. */
export function gateHandleCenters(line: ScreenLine): Record<GateHandleId, ScreenPoint> {
	return { a: line.a, b: line.b };
}

/** Handle centres for whichever kind is selected, keyed generically for hit-testing.
 * Only the current kind's keys are present — callers must guard for undefined. */
export function handleCentersFor(shape: Shape, adapter: MapAdapter): Partial<Record<HandleId, ScreenPoint>> {
	return shape.kind === 'zone' ? zoneHandleCenters(shapeScreenRect(shape, adapter)) : gateHandleCenters(gateScreenLine(shape, adapter));
}

/**
 * Unit vector 90° "forward" from the gate, in screen pixels — the projection of the backend's
 * GeoLine normal (the gate vector rotated +90° in the equirectangular lat/lng frame).
 *
 * Derived geographically and then projected, so it follows whatever camera the map has. Rotating
 * the drawn line 90° in screen space only equals the geographic normal on an unpitched map, and a
 * perspective projection does not preserve right angles — the app's terrain default is 10° pitch
 * and dragRotate/touchPitch are live, so the arrow drifted off the direction it claims to show.
 */
export function gateForward(shape: GateShape, adapter: MapAdapter): ScreenPoint {
	const midLat = (shape.a.lat + shape.b.lat) / 2;
	const midLng = (shape.a.lng + shape.b.lng) / 2;
	// Same frame GeoLine uses (x = lng·cos(refLat), y = lat), so the normal matches its sign convention.
	const cosRef = Math.cos((midLat * Math.PI) / 180) || 1e-9;
	const sx = (shape.b.lng - shape.a.lng) * cosRef;
	const sy = shape.b.lat - shape.a.lat;
	// Step off the midpoint along the normal (-sy, sx), converted back to degrees.
	const mid = adapter.project(midLat, midLng);
	const tip = adapter.project(midLat + sx * NORMAL_PROBE, midLng + (-sy * NORMAL_PROBE) / cosRef);

	const dx = tip.x - mid.x, dy = tip.y - mid.y;
	const len = Math.hypot(dx, dy);
	// Extreme pitch can project the probe above the horizon (non-finite / collapsed) — fall back to
	// the flat-screen rotation, which is exact for the unpitched case anyway.
	if (Number.isFinite(len) && len > 1e-6) return { x: dx / len, y: dy / len };
	const line = gateScreenLine(shape, adapter);
	return screenForward(line.a.x, line.a.y, line.b.x, line.b.y);
}

/** Flat-screen fallback for {@link gateForward}: the gate vector rotated 90°. Screen y is flipped
 * relative to latitude, so the equivalent screen rotation is (dy,-dx), not (-dy,dx). */
function screenForward(ax: number, ay: number, bx: number, by: number): ScreenPoint {
	const dx = bx - ax, dy = by - ay;
	const len = Math.hypot(dx, dy) || 1;
	return { x: dy / len, y: -dx / len };
}

const roleIcon: Record<GateShape['role'], string> = { start: '🚩', finish: '🏁', both: '🚩🏁' };

export class ShapeLayer {
	private readonly unsubscribers: Array<() => void> = [];

	constructor(
		private readonly svg: SVGSVGElement,
		private readonly adapter: MapAdapter,
		private readonly store: ShapeStore,
		private readonly getSelectedId: () => string | null,
	) {
		this.svg.appendChild(this.defs);
		this.unsubscribers.push(this.store.subscribe(() => this.render()));
		this.unsubscribers.push(this.adapter.onViewChange(() => this.render()));
	}

	/** Diagonal hatch fill for "avoid" zones — defined once and kept across render() clears
	 * (SVG url() fills need the referenced <pattern> to stay in the document). */
	private readonly defs = this.buildDefs();

	private buildDefs(): SVGDefsElement {
		const defs = document.createElementNS(SVG_NS, 'defs');
		const pattern = document.createElementNS(SVG_NS, 'pattern');
		pattern.setAttribute('id', 'shape-hatch-avoid');
		pattern.setAttribute('width', '8');
		pattern.setAttribute('height', '8');
		pattern.setAttribute('patternUnits', 'userSpaceOnUse');
		pattern.setAttribute('patternTransform', 'rotate(45)');
		const line = document.createElementNS(SVG_NS, 'line');
		line.setAttribute('x1', '0');
		line.setAttribute('y1', '0');
		line.setAttribute('x2', '0');
		line.setAttribute('y2', '8');
		line.setAttribute('class', 'shape-hatch-line');
		pattern.appendChild(line);
		defs.appendChild(pattern);
		return defs;
	}

	render(): void {
		for (const child of Array.from(this.svg.children)) if (child !== this.defs) this.svg.removeChild(child);
		const selectedId = this.getSelectedId();
		const all = this.store.getAll();

		for (const shape of all) {
			if (shape.kind === 'zone') this.renderZone(shape, all, shape.id === selectedId);
			else this.renderGate(shape, all, shape.id === selectedId);
		}

		const selected = selectedId ? this.store.get(selectedId) : null;
		if (selected) this.drawHandles(selected);
	}

	private renderZone(shape: ZoneShape, all: readonly Shape[], selected: boolean): void {
		const r = shapeScreenRect(shape, this.adapter);

		const rect = document.createElementNS(SVG_NS, 'rect');
		rect.setAttribute('x', String(r.x));
		rect.setAttribute('y', String(r.y));
		rect.setAttribute('width', String(r.w));
		rect.setAttribute('height', String(r.h));
		rect.setAttribute('data-id', shape.id);
		rect.setAttribute('class', `shape-rect shape-rect--${shape.role}` + (selected ? ' shape-rect--selected' : ''));
		this.svg.appendChild(rect);

		const label = badgeLabel(all, shape.id) + (zoneRoleMarker[shape.role] ?? '');
		this.svg.appendChild(this.text(r.x + 4, r.y + 14, label, 'shape-badge'));
	}

	private renderGate(shape: GateShape, all: readonly Shape[], selected: boolean): void {
		const line = gateScreenLine(shape, this.adapter);
		const { a, b } = line;
		const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
		const fwd = gateForward(shape, this.adapter);
		const lineDir = { x: (b.x - a.x) / (Math.hypot(b.x - a.x, b.y - a.y) || 1), y: (b.y - a.y) / (Math.hypot(b.x - a.x, b.y - a.y) || 1) };

		// Fat transparent hit-line under the thin visible one: a 2px gate is near-impossible to tap,
		// so this widens the pointer target (it's pointer-active only in select mode — see CSS).
		const hit = document.createElementNS(SVG_NS, 'line');
		hit.setAttribute('x1', String(a.x));
		hit.setAttribute('y1', String(a.y));
		hit.setAttribute('x2', String(b.x));
		hit.setAttribute('y2', String(b.y));
		hit.setAttribute('data-id', shape.id);
		hit.setAttribute('class', 'shape-gate-hit');
		this.svg.appendChild(hit);

		const el = document.createElementNS(SVG_NS, 'line');
		el.setAttribute('x1', String(a.x));
		el.setAttribute('y1', String(a.y));
		el.setAttribute('x2', String(b.x));
		el.setAttribute('y2', String(b.y));
		el.setAttribute('data-id', shape.id);
		el.setAttribute('class', 'shape-gate-line' + (selected ? ' shape-gate-line--selected' : ''));
		this.svg.appendChild(el);

		for (const p of [a, b]) this.svg.appendChild(this.tick(p, fwd));

		if (shape.direction !== 'backward') this.svg.appendChild(this.arrow(mid, fwd, lineDir));
		if (shape.direction !== 'forward') this.svg.appendChild(this.arrow(mid, { x: -fwd.x, y: -fwd.y }, lineDir));

		const label = `${badgeLabel(all, shape.id)} ${roleIcon[shape.role]}`;
		this.svg.appendChild(this.text(mid.x - fwd.x * (ARROW_LENGTH + 6), mid.y - fwd.y * (ARROW_LENGTH + 6), label, 'shape-gate-badge'));
	}

	private tick(p: ScreenPoint, fwd: ScreenPoint): SVGLineElement {
		const t = document.createElementNS(SVG_NS, 'line');
		t.setAttribute('x1', String(p.x - (fwd.x * TICK_LENGTH) / 2));
		t.setAttribute('y1', String(p.y - (fwd.y * TICK_LENGTH) / 2));
		t.setAttribute('x2', String(p.x + (fwd.x * TICK_LENGTH) / 2));
		t.setAttribute('y2', String(p.y + (fwd.y * TICK_LENGTH) / 2));
		t.setAttribute('class', 'shape-gate-tick');
		return t;
	}

	/** Arrowhead triangle at the gate midpoint, tip pointing along `dir`, base along `lineDir`. */
	private arrow(mid: ScreenPoint, dir: ScreenPoint, lineDir: ScreenPoint): SVGPolygonElement {
		const tip = { x: mid.x + dir.x * ARROW_LENGTH, y: mid.y + dir.y * ARROW_LENGTH };
		const left = { x: mid.x + (lineDir.x * ARROW_WIDTH) / 2, y: mid.y + (lineDir.y * ARROW_WIDTH) / 2 };
		const right = { x: mid.x - (lineDir.x * ARROW_WIDTH) / 2, y: mid.y - (lineDir.y * ARROW_WIDTH) / 2 };
		const poly = document.createElementNS(SVG_NS, 'polygon');
		poly.setAttribute('points', `${tip.x},${tip.y} ${left.x},${left.y} ${right.x},${right.y}`);
		poly.setAttribute('class', 'shape-gate-arrow');
		return poly;
	}

	private text(x: number, y: number, content: string, className: string): SVGTextElement {
		const t = document.createElementNS(SVG_NS, 'text');
		t.setAttribute('x', String(x));
		t.setAttribute('y', String(y));
		t.setAttribute('class', className);
		t.textContent = content;
		return t;
	}

	private drawHandles(shape: Shape): void {
		const centers = handleCentersFor(shape, this.adapter);
		for (const id of Object.keys(centers) as HandleId[]) {
			const c = centers[id];
			if (!c) continue;
			const h = document.createElementNS(SVG_NS, 'rect');
			h.setAttribute('x', String(c.x - HANDLE_SIZE / 2));
			h.setAttribute('y', String(c.y - HANDLE_SIZE / 2));
			h.setAttribute('width', String(HANDLE_SIZE));
			h.setAttribute('height', String(HANDLE_SIZE));
			h.setAttribute('data-handle', id);
			h.setAttribute('data-id', shape.id);
			h.setAttribute('class', 'shape-handle');
			this.svg.appendChild(h);
		}
	}

	destroy(): void {
		for (const off of this.unsubscribers) off();
		while (this.svg.firstChild) this.svg.removeChild(this.svg.firstChild);
	}
}
