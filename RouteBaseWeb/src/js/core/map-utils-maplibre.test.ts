import { must } from './invariant';
import type { GpsPoint } from './core-types';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { MapLibreAdapter } from './map-utils-maplibre';
import { mapDispatcher } from './map-dispatcher';
import { setGpsData } from './gps-data';

// The adapter constructs maplibregl.Marker at runtime; the module only references the
// global inside methods, so we stub it per test rather than at import time.
class ThrowingMarker {
	setLngLat(): this {
		return this;
	}
	addTo(): this {
		throw new Error('map disposed');
	}
	remove(): void {}
	getLngLat(): { lng: number; lat: number } {
		return { lng: 0, lat: 0 };
	}
	getElement(): HTMLElement {
		return document.createElement('div');
	}
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const AdapterCtor = MapLibreAdapter as any;

beforeEach(() => {
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(globalThis as any).maplibregl = { Marker: ThrowingMarker };
});

afterEach(() => {
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	delete (globalThis as any).maplibregl;
	setGpsData([]);
	mapDispatcher.unregister();
});

describe('MapLibreAdapter.showHighlightMarker — CM-03 recursion guard', () => {
	it('gives up after one rebuild attempt instead of recursing forever', () => {
		const adapter = new AdapterCtor({}); // truthy map; Marker.addTo always throws
		// Would otherwise recurse until RangeError: Maximum call stack size exceeded.
		expect(() => adapter.showHighlightMarker(10, 20)).not.toThrow();
		expect(adapter.highlightMarker).toBeNull();
	});
});

describe('MapLibreAdapter.destroy — CM-03 listener leak', () => {
	it('detaches the proximity-hover mousemove and canvas mouseleave listeners', () => {
		const onCalls: Array<[string, unknown]> = [];
		const offCalls: Array<[string, unknown]> = [];
		const canvas = {
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
		};
		const map = {
			on: (ev: string, fn: unknown) => onCalls.push([ev, fn]),
			off: (ev: string, fn: unknown) => offCalls.push([ev, fn]),
			getCanvas: () => canvas,
			getLayer: () => null,
			getSource: () => null,
		};
		setGpsData([
			{ lat: 1, lng: 1 } as GpsPoint,
			{ lat: 2, lng: 2 } as GpsPoint,
		]);

		const adapter = new AdapterCtor(map);
		adapter.setupProximityHover({ onHighlight: vi.fn(), onHideHighlight: vi.fn() });

		expect(onCalls).toHaveLength(1);
		const [moveEv, moveFn] = must(onCalls[0], 'one on() call');
		expect(moveEv).toBe('mousemove');
		expect(canvas.addEventListener).toHaveBeenCalledTimes(1);
		const leaveFn = must(canvas.addEventListener.mock.calls[0], 'one addEventListener call')[1];

		adapter.destroy();

		// Same function references must be detached (not just any handler).
		expect(offCalls).toContainEqual(['mousemove', moveFn]);
		expect(canvas.removeEventListener).toHaveBeenCalledWith('mouseleave', leaveFn);
	});
});

describe('MapLibreAdapter.showHighlightMarker — MS-09 hidden on create', () => {
	class FakeMarker {
		constructor(private opts: { element: HTMLElement }) {}
		setLngLat(): this { return this; }
		addTo(): this { return this; }
		getElement(): HTMLElement { return this.opts.element; }
		getLngLat(): { lng: number; lat: number } { return { lng: 0, lat: 0 }; }
		remove(): void {}
	}

	it('creates the highlight marker hidden when blue-marker visibility is off', () => {
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		(globalThis as any).maplibregl = { Marker: FakeMarker };
		const adapter = new AdapterCtor({}, { isBlueMarkerVisible: () => false });

		adapter.showHighlightMarker(1, 2);

		const el = adapter.highlightMarker.getElement() as HTMLElement;
		expect(el.style.display).toBe('none');
	});
});

describe('MapLibreAdapter.updateTrackColors — CM-06 injected seam', () => {
	it('recolors via the injected callback', () => {
		const injected = vi.fn();
		const adapter = new AdapterCtor({}, { onUpdateTrackColors: injected });
		adapter.updateTrackColors('speed', []);

		expect(injected).toHaveBeenCalledWith('speed', []);
	});

	it('no-ops when no callback is injected', () => {
		// ID-08 removed the legacy window.updateMaplibreTrackColors fallback (core can't import
		// the maplibre track layer without a cycle); maplibre-terrain now always injects the seam.
		const adapter = new AdapterCtor({});
		expect(() => adapter.updateTrackColors('elevation', [])).not.toThrow();
	});
});
