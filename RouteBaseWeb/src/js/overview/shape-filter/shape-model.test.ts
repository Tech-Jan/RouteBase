// @vitest-environment node
import { describe, it, expect } from 'vitest';
import {
	gateFromPoints,
	hasGatesExtentConfig,
	nextGateRole,
	zoneBoundsFromAnchor,
	zoneFromBounds,
	zoneResizeAnchor,
	type GateRole,
} from './shape-model';

const gate = (role: GateRole) => gateFromPoints({ lat: 0, lng: 0 }, { lat: 0, lng: 1 }, role);
const zone = () => zoneFromBounds({ south: 0, west: 0, north: 1, east: 1 });

// SG-01: the anchor helpers back the zone-resize fix — the grabbed handle's opposite corner is
// captured once and the whole rect is rebuilt from it, so a drag that crosses the anchor edge
// can never overwrite the fixed corner (see analysis-shapefilter-geom.md SG-01).

describe('zoneResizeAnchor', () => {
	const z = zoneFromBounds({ south: 10, west: 30, north: 20, east: 40 });

	it('returns the corner opposite each handle', () => {
		expect(zoneResizeAnchor(z, 'nw')).toEqual({ lat: 10, lng: 40 }); // opposite = se
		expect(zoneResizeAnchor(z, 'ne')).toEqual({ lat: 10, lng: 30 }); // opposite = sw
		expect(zoneResizeAnchor(z, 'sw')).toEqual({ lat: 20, lng: 40 }); // opposite = ne
		expect(zoneResizeAnchor(z, 'se')).toEqual({ lat: 20, lng: 30 }); // opposite = nw
	});
});

describe('zoneBoundsFromAnchor', () => {
	it('normalises regardless of anchor/point ordering on the lat axis', () => {
		expect(zoneBoundsFromAnchor({ lat: 10, lng: 40 }, { lat: 20, lng: 40 })).toEqual({
			south: 10, north: 20, west: 40, east: 40,
		});
		expect(zoneBoundsFromAnchor({ lat: 20, lng: 40 }, { lat: 10, lng: 40 })).toEqual({
			south: 10, north: 20, west: 40, east: 40,
		});
	});

	it('normalises regardless of anchor/point ordering on the lng axis', () => {
		expect(zoneBoundsFromAnchor({ lat: 10, lng: 40 }, { lat: 10, lng: 20 })).toEqual({
			south: 10, north: 10, west: 20, east: 40,
		});
		expect(zoneBoundsFromAnchor({ lat: 10, lng: 20 }, { lat: 10, lng: 40 })).toEqual({
			south: 10, north: 10, west: 20, east: 40,
		});
	});

	it('is immune to the cross-edge sequence that corrupted the old two-field patch', () => {
		// nw drag from {south:10,north:20,west:30,east:40}; anchor stays the SE corner (10,40).
		const anchor = { lat: 10, lng: 40 };
		// The exact SG-01 replay: shrink past the south edge, then keep dragging further south.
		expect(zoneBoundsFromAnchor(anchor, { lat: 15, lng: 35 })).toEqual({ south: 10, north: 15, west: 35, east: 40 });
		expect(zoneBoundsFromAnchor(anchor, { lat: 5, lng: 25 })).toEqual({ south: 5, north: 10, west: 25, east: 40 });
		// The move that used to discard the anchor (north became 5) now keeps it at 10.
		expect(zoneBoundsFromAnchor(anchor, { lat: 2, lng: 20 })).toEqual({ south: 2, north: 10, west: 20, east: 40 });
	});
});

// SP-07 auto-assign: the first gate drawn under "Between gates" is the start, later ones default to finish.
describe('nextGateRole', () => {
	it('assigns start when no start-side gate exists yet', () => {
		expect(nextGateRole([])).toBe('start');
		expect(nextGateRole([zone()])).toBe('start');        // zones never count as gates
		expect(nextGateRole([gate('finish')])).toBe('start'); // a lone finish still needs a start
	});

	it('assigns finish once a start (or both) gate exists', () => {
		expect(nextGateRole([gate('start')])).toBe('finish');
		expect(nextGateRole([gate('both')])).toBe('finish'); // a lap gate already covers the start side
	});
});

describe('hasGatesExtentConfig', () => {
	it('is true only with at least one start-side and one finish-side gate', () => {
		expect(hasGatesExtentConfig([gate('start'), gate('finish')])).toBe(true);
		expect(hasGatesExtentConfig([gate('both')])).toBe(true); // one both-gate satisfies each side (lap course)
	});

	it('is false when a side is missing or there are no gates', () => {
		expect(hasGatesExtentConfig([])).toBe(false);
		expect(hasGatesExtentConfig([gate('start')])).toBe(false);
		expect(hasGatesExtentConfig([gate('finish')])).toBe(false);
		expect(hasGatesExtentConfig([gate('start'), gate('start')])).toBe(false);
		expect(hasGatesExtentConfig([zone()])).toBe(false); // zones can't back a gates extent
	});
});
