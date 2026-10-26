/**
 * shape-filter/shape-model.ts — pure geometry + role model for zones and gates.
 *
 * No DOM, no map, no network. Mirrors the backend GeoRect/GeoLine so the same
 * normalisation/containment rules apply on both sides. State, rendering, input,
 * and networking all live in sibling modules and depend on this one.
 */

import type { CrossDirection } from '../../generated/contracts';
import type { ZoneHandleId } from './shape-layer';

/** UI role of a zone — translated to backend membership rules on save:
 *   'all'    → passesAll (track must touch every such zone)
 *   'any'    → passesAny per group (touch at least one of the group)
 *   'avoid'  → avoids (touching any such zone disqualifies)
 *   'within' → staysWithin (the effort never leaves the union of such zones)
 *   'start' / 'finish' / 'either' → endpointInZone (the effort's first/last/either
 *     sample lies inside the zone — the zone analogue of a start/finish gate). The
 *     role value doubles as the wire `endpoint` value, so no translation table.
 */
export type ZoneRole = 'all' | 'any' | 'avoid' | 'within' | 'start' | 'finish' | 'either';

/** UI role of a gate under the "Between gates" extent — a gate may open a lap
 * course by being both (start AND finish). Irrelevant while extent ≠ 'gates'. */
export type GateRole = 'start' | 'finish' | 'both';

/** Allowed crossing direction of a gate — the generated wire enum re-exported (K3). */
export type { CrossDirection };

/** Segment effort extent, one per document. */
export type MatchMode = 'whole' | 'clip' | 'gates';

export interface LatLng {
	lat: number;
	lng: number;
}

/** An axis-aligned rectangle in south/west/north/east bounds (matches the API DTO). */
export interface ZoneShape {
	id: string;
	kind: 'zone';
	role: ZoneRole;
	groupId: number;
	south: number;
	west: number;
	north: number;
	east: number;
}

/** A directed gate line between two points (matches the API DTO). */
export interface GateShape {
	id: string;
	kind: 'gate';
	role: GateRole;
	direction: CrossDirection;
	a: LatLng;
	b: LatLng;
}

export type Shape = ZoneShape | GateShape;

let _seq = 0;
/** Monotonic id — avoids Math.random, which is unavailable in some build sandboxes. */
export function nextShapeId(): string {
	_seq += 1;
	return 'shape-' + _seq;
}

/** Build a zone from two opposite corners, normalising the bounds. */
export function zoneFromCorners(a: LatLng, b: LatLng, role: ZoneRole = 'any', groupId = 0): ZoneShape {
	return {
		id: nextShapeId(),
		kind: 'zone',
		role,
		groupId,
		south: Math.min(a.lat, b.lat),
		north: Math.max(a.lat, b.lat),
		west: Math.min(a.lng, b.lng),
		east: Math.max(a.lng, b.lng),
	};
}

/** Bounds of a zone without identity — the shape of the edit/API payload. */
export interface RectBounds {
	south: number;
	west: number;
	north: number;
	east: number;
}

/** Build a zone from raw bounds (e.g. an existing segment loaded for editing), normalising. */
export function zoneFromBounds(b: RectBounds, role: ZoneRole = 'any', groupId = 0): ZoneShape {
	return normalizeZone({
		id: nextShapeId(),
		kind: 'zone',
		role,
		groupId,
		south: b.south,
		west: b.west,
		north: b.north,
		east: b.east,
	});
}

/** Corner opposite a zone resize handle — the point that must stay fixed for the whole drag,
 * even if the cursor crosses to the other side (see SG-01). */
export function zoneResizeAnchor(z: ZoneShape, handle: ZoneHandleId): LatLng {
	switch (handle) {
		case 'nw': return { lat: z.south, lng: z.east };
		case 'ne': return { lat: z.south, lng: z.west };
		case 'sw': return { lat: z.north, lng: z.east };
		case 'se': return { lat: z.north, lng: z.west };
	}
}

/** Full zone bounds from a fixed anchor corner + the live opposite corner — a wholesale
 * replace (not a per-field patch) so a drag that crosses the anchor never loses it. */
export function zoneBoundsFromAnchor(anchor: LatLng, point: LatLng): RectBounds {
	return {
		south: Math.min(anchor.lat, point.lat),
		north: Math.max(anchor.lat, point.lat),
		west: Math.min(anchor.lng, point.lng),
		east: Math.max(anchor.lng, point.lng),
	};
}

/** Re-normalise bounds after an edit so south≤north and west≤east always hold. */
export function normalizeZone(z: ZoneShape): ZoneShape {
	return {
		...z,
		south: Math.min(z.south, z.north),
		north: Math.max(z.south, z.north),
		west: Math.min(z.west, z.east),
		east: Math.max(z.west, z.east),
	};
}

/** Build a gate from two endpoints (e.g. the two taps of a gate draw gesture). */
export function gateFromPoints(a: LatLng, b: LatLng, role: GateRole, direction: CrossDirection = 'any'): GateShape {
	return { id: nextShapeId(), kind: 'gate', role, direction, a, b };
}

/** First gate drawn under "Between gates" becomes the start; later ones default to finish
 * (rebindable in the panel) — mirrors the plan's "start-if-none-else-finish" auto-assign. */
export function nextGateRole(existing: readonly Shape[]): GateRole {
	const hasStart = existing.some((s) => s.kind === 'gate' && (s.role === 'start' || s.role === 'both'));
	return hasStart ? 'finish' : 'start';
}

/** True when the shapes can carry a gates extent: ≥1 start and ≥1 finish role
 * (a 'both' gate satisfies either side). */
export function hasGatesExtentConfig(shapes: readonly Shape[]): boolean {
	const gates = shapes.filter((s): s is GateShape => s.kind === 'gate');
	return (
		gates.some((g) => g.role === 'start' || g.role === 'both') &&
		gates.some((g) => g.role === 'finish' || g.role === 'both')
	);
}

/** Per-kind badge label ("Z1", "G2", …) — the wire format's shape numbers are a single global
 * sequence, but the UI shows each kind counting from 1 for readability. */
export function badgeLabel(shapes: readonly Shape[], id: string): string {
	const shape = shapes.find((s) => s.id === id);
	if (!shape) return '';
	const prefix = shape.kind === 'zone' ? 'Z' : 'G';
	let n = 0;
	for (const s of shapes) {
		if (s.kind !== shape.kind) continue;
		n += 1;
		if (s.id === id) break;
	}
	return prefix + n;
}
