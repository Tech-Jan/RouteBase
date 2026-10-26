import { describe, it, expect, beforeEach } from 'vitest';
import { ShapeInteractions, type Tool } from './shape-interactions';
import { ShapeStore } from './shape-store';
import { zoneFromBounds } from './shape-model';
import type { MapAdapter } from './map-adapter';

// SG-01 regression: a zone resize that drags the cursor across the anchor edge (shrink then keep
// growing past it) must keep the fixed corner put. The old two-field patch lost the anchor on the
// second move after a crossing; this drives the real ShapeInteractions state machine through the
// exact pointerdown→move→move→move sequence and asserts the anchor survives. A single-step test
// can't reproduce this — only the second move past the edge does (analysis-shapefilter-geom.md).

// North-up linear adapter: project(lat,lng) = {x:lng, y:-lat}; unproject inverts it. Handle-hit and
// cursor→geo both flow through these, so pixel coords below map cleanly to the geo trace in the spec.
function fakeAdapter(): MapAdapter {
	const container = document.createElement('div');
	container.getBoundingClientRect = () => ({ left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0, toJSON() {} });
	return {
		project: (lat, lng) => ({ x: lng, y: -lat }),
		unproject: (x, y) => ({ lat: -y, lng: x }),
		onViewChange: () => () => {},
		getContainer: () => container,
		setInteractive: () => {},
	};
}

function pointerEvent(type: string, clientX: number, clientY: number): Event {
	const ev = new Event(type, { bubbles: true });
	Object.assign(ev, { clientX, clientY, pointerId: 1 });
	return ev;
}

describe('ShapeInteractions zone resize (SG-01)', () => {
	let store: ShapeStore;
	let overlay: HTMLElement;
	let interactions: ShapeInteractions;
	let selectedId: string;

	beforeEach(() => {
		store = new ShapeStore();
		const zone = zoneFromBounds({ south: 10, west: 30, north: 20, east: 40 });
		store.add(zone);
		selectedId = zone.id;

		overlay = document.createElement('div');
		const deps = {
			getTool: (): Tool => 'select',
			getSelectedId: () => selectedId,
			onSelect: () => {},
			onDrawFinished: () => {},
			getDefaultZoneRole: () => 'any' as const,
			getDefaultGateRole: () => 'start' as const,
			getDefaultGateDirection: () => 'any' as const,
		};
		interactions = new ShapeInteractions(overlay, fakeAdapter(), store, deps);
		interactions.enable();
	});

	// Grabbing 'nw' (screen top-left = geo north/west corner) fixes the SE corner (south:10,east:40).
	it('keeps the anchor corner fixed across an edge crossing', () => {
		// Down on the nw handle: screen (30,-20) = project(north:20, west:30).
		overlay.dispatchEvent(pointerEvent('pointerdown', 30, -20));
		// 1. shrink toward centre — no crossing yet.
		overlay.dispatchEvent(pointerEvent('pointermove', 35, -15)); // geo (15,35)
		expect(store.get(selectedId)).toMatchObject({ south: 10, north: 15, west: 35, east: 40 });
		// 2. cross the south edge — anchor lat 10 now lives in `north`.
		overlay.dispatchEvent(pointerEvent('pointermove', 25, -5)); // geo (5,25)
		expect(store.get(selectedId)).toMatchObject({ south: 5, north: 10, west: 25, east: 40 });
		// 3. the move that used to discard the anchor: north must stay 10, east must stay 40.
		overlay.dispatchEvent(pointerEvent('pointermove', 20, -2)); // geo (2,20)
		expect(store.get(selectedId)).toMatchObject({ south: 2, north: 10, west: 20, east: 40 });

		overlay.dispatchEvent(pointerEvent('pointerup', 20, -2));
	});

	// The common case (no crossing) must be byte-identical to the old behaviour — a plain shrink.
	it('resizes correctly when the cursor never crosses the anchor', () => {
		overlay.dispatchEvent(pointerEvent('pointerdown', 30, -20)); // nw handle
		overlay.dispatchEvent(pointerEvent('pointermove', 33, -18)); // geo (18,33)
		expect(store.get(selectedId)).toMatchObject({ south: 10, north: 18, west: 33, east: 40 });
		overlay.dispatchEvent(pointerEvent('pointerup', 33, -18));
	});
});
