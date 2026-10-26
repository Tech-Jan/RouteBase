// @vitest-environment node
import { describe, it, expect, vi } from 'vitest';
import { createMapLibreAdapter } from './map-adapter';

// SG-03: setInteractive must lock EVERY camera handler while armed, not just a subset. The overlay's
// pointer/touch events (and the wheel) bubble to the map container where MapLibre's handlers live, so
// any of the eight could move the camera mid-draw and shift the live unproject() the shape is built
// from. Earlier this pinned only five; scrollZoom / boxZoom / doubleClickZoom were reachable and are
// now locked too (scrollZoom is the realistic one — a wheel tick while dragging out a zone). Handler
// names verified against the installed maplibre-gl .d.ts.

const HANDLERS = [
	'dragPan', 'scrollZoom', 'boxZoom', 'doubleClickZoom',
	'dragRotate', 'touchZoomRotate', 'touchPitch', 'keyboard',
] as const;

function fakeMap() {
	const handlers = Object.fromEntries(HANDLERS.map((h) => [h, { enable: vi.fn(), disable: vi.fn() }]));
	return handlers as Record<(typeof HANDLERS)[number], { enable: ReturnType<typeof vi.fn>; disable: ReturnType<typeof vi.fn> }>;
}

describe('createMapLibreAdapter.setInteractive (SG-03)', () => {
	it('disables then re-enables every camera handler exactly once each', () => {
		const map = fakeMap();
		const adapter = createMapLibreAdapter(map as unknown as maplibregl.Map);

		adapter.setInteractive(false);
		for (const h of HANDLERS) {
			expect(map[h].disable).toHaveBeenCalledTimes(1);
			expect(map[h].enable).not.toHaveBeenCalled();
		}

		adapter.setInteractive(true);
		for (const h of HANDLERS) {
			expect(map[h].enable).toHaveBeenCalledTimes(1);
			expect(map[h].disable).toHaveBeenCalledTimes(1);
		}
	});

	// The wheel is the gesture a user actually reaches mid-draw — pin it by name so a future
	// "trim the list back to pan+pinch" can't drop it silently.
	it('locks scrollZoom specifically (wheel zoom while drawing)', () => {
		const map = fakeMap();
		const adapter = createMapLibreAdapter(map as unknown as maplibregl.Map);
		adapter.setInteractive(false);
		expect(map.scrollZoom.disable).toHaveBeenCalledTimes(1);
	});
});
