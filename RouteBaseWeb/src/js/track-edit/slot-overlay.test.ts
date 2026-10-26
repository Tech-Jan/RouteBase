// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';
import { must } from '../core/invariant';

const h = vi.hoisted(() => ({ map: null as unknown }));
vi.mock('../core/map-state', () => ({ getMaplibreMap: () => h.map }));

import {
	buildSlotFeatures, colorForSlot, SLOT_COLORS, renderSlotOverlay, clearSlotOverlay, initSlotOverlay,
} from './slot-overlay';
import type { ReplaySlot, SlotTrack } from './apply-script';

const f64 = (v: number[]): Float64Array => Float64Array.from(v);

function slot(slotId: number, lat: number[], lng: number[]): ReplaySlot {
	const track: SlotTrack = { count: lat.length, channels: { lat: f64(lat), lng: f64(lng) } };
	return { slotId, origin: { activityId: 100 + slotId }, track };
}

describe('slot-overlay builders', () => {
	it('buildSlotFeatures excludes the active slot and carries slotId+color (V5)', () => {
		const slots = [slot(0, [47, 47.1], [8, 8.1]), slot(1, [46, 46.1], [7, 7.1])];
		const features = buildSlotFeatures(slots, /*active*/ 0);

		expect(features).toHaveLength(1); // slot 0 (active) drawn by the main pipeline, not the overlay
		const f0 = must(features[0], 'one feature');
		expect(f0.properties).toEqual({ slotId: 1, color: colorForSlot(1) });
		expect((f0.geometry as GeoJSON.LineString).coordinates).toEqual([[7, 46], [7.1, 46.1]]);
	});

	it('colorForSlot is stable per id and cycles mod 7 (V6)', () => {
		expect(colorForSlot(3)).toBe(colorForSlot(3));       // deterministic
		expect(colorForSlot(3)).toBe(SLOT_COLORS[3]);
		expect(colorForSlot(0)).toBe(colorForSlot(7));       // mod-7 collision documented (M12)
		expect(SLOT_COLORS).toHaveLength(7);
	});

	it('NaN coordinate pairs are skipped and sub-2-coord slots dropped (V7)', () => {
		const withGap = slot(1, [47, NaN, 47.2], [8, NaN, 8.2]);
		const feature = must(buildSlotFeatures([withGap], /*active*/ 0)[0], 'one feature');
		expect((feature.geometry as GeoJSON.LineString).coordinates).toEqual([[8, 47], [8.2, 47.2]]);

		const oneCoord = slot(2, [47, NaN], [8, NaN]); // only one valid pair → dropped
		expect(buildSlotFeatures([oneCoord], /*active*/ 0)).toHaveLength(0);
	});
});

// TE-04: the teardown path is now reachable (exitEditMode calls it) — prove it removes everything it added.
describe('clearSlotOverlay tears the overlay down', () => {
	it('removes the layer, the source and all three map handlers', () => {
		const off = vi.fn();
		const removeLayer = vi.fn();
		const removeSource = vi.fn();
		const mapStub = {
			isStyleLoaded: () => true,
			getSource: () => ({}),
			getLayer: () => ({}),
			on: vi.fn(),
			off,
			once: () => {},
			addSource: () => {},
			addLayer: () => {},
			removeLayer,
			removeSource,
		};
		h.map = mapStub;

		initSlotOverlay({ setActiveSlot: () => {}, isToolArmed: () => false });
		expect(mapStub.on).toHaveBeenCalledTimes(3); // click + mouseenter + mouseleave

		clearSlotOverlay();

		expect(removeLayer).toHaveBeenCalledWith('track-edit-slots-line');
		expect(removeSource).toHaveBeenCalledWith('track-edit-slots');
		expect(off).toHaveBeenCalledTimes(3);
		expect(off.mock.calls.map((c) => c[0])).toEqual(['click', 'mouseenter', 'mouseleave']);
		// Handler identity must match what `on` registered, or MapLibre would not detach them.
		expect(off.mock.calls.map((c) => c[2])).toEqual(mapStub.on.mock.calls.map((c) => c[2]));

		clearSlotOverlay(); // idempotent — nothing left to unwire
		expect(off).toHaveBeenCalledTimes(3);
	});
});

// R-06: isStyleLoaded() lies while tiles/DEM are in flight — a deferred add must self-heal on 'idle', not wait
// for the next user action (memory maplibre-isstyleloaded-lies; b12cbd1 recipe).
describe('renderSlotOverlay deferred add self-heals on idle', () => {
	it('defers the add while the style is not loaded, then replays the last args on idle', () => {
		let idleCb: (() => void) | null = null;
		const calls = { addSource: 0, addLayer: 0 };
		let styleLoaded = false;
		const mapStub = {
			isStyleLoaded: () => styleLoaded,
			getSource: () => undefined,
			getLayer: () => undefined,
			once: (ev: string, cb: () => void) => { if (ev === 'idle') idleCb = cb; },
			addSource: () => { calls.addSource++; },
			addLayer: () => { calls.addLayer++; },
			removeLayer: () => {},
			removeSource: () => {},
		};
		h.map = mapStub;
		clearSlotOverlay(); // reset module retry state (lastArgs / pendingIdleRetry)

		renderSlotOverlay([slot(0, [47, 47.1], [8, 8.1]), slot(1, [46, 46.1], [7, 7.1])], /*active*/ 0);
		expect(calls.addSource).toBe(0);   // style not ready → deferred, nothing added
		expect(idleCb).not.toBeNull();     // a one-shot idle retry is armed

		styleLoaded = true;
		idleCb!();                         // style + sources settled → replay the last args
		expect(calls.addSource).toBe(1);
		expect(calls.addLayer).toBe(1);
	});
});
