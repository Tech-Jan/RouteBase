import { describe, it, expect, afterEach, vi } from 'vitest';
import { must } from './invariant';
import {
	createCameraChangeDetector,
	normalizeTrackDistances,
	safeCleanupLayers,
	mapFullscreen,
	withHandlersShielded,
	applyTerrainElevation,
} from './map-utils-shared';

// CM-04 characterization tests — pin behavior of the map-shared camera/track/cleanup helpers
// (named ESM exports; assertions unchanged since the CB-01 dissolution).
//
// The replay speed-ladder cases (roundReplaySpeed / formatDurationShort / buildReplaySpeedOptions) that
// lived here were REMOVED, not weakened: that activity-only ladder is retired in favour
// of the single core/speed-ladder.ts the transport wires for all three replays. The surviving ladder is
// pinned by core/speed-ladder.test.ts — intent gone, contract deliberately unified.

describe('createCameraChangeDetector — first true, sub-threshold false, bearing true, reset re-arms', () => {
	it('tracks camera changes with a ~1m² positional threshold', () => {
		const d = createCameraChangeDetector();
		expect(d.hasChanged(45, 9, 12, 0, 0)).toBe(true); // first call always true
		expect(d.hasChanged(45.000001, 9, 12, 0, 0)).toBe(false); // sub-threshold move
		expect(d.hasChanged(45.000001, 9, 12, 30, 0)).toBe(true); // bearing change
		d.reset();
		expect(d.hasChanged(45.000001, 9, 12, 30, 0)).toBe(true); // re-armed after reset
	});
});

describe('normalizeTrackDistances — metres→km and non-monotonic repair', () => {
	it('converts metre distances to km', () => {
		const track = [{ lat: 45, lng: 9, altitude: 0, speed: 0, distance: 1500 }];
		normalizeTrackDistances(track);
		expect(must(track[0], 'fixture point 0').distance).toBe(1.5);
	});

	it('recalculates cumulative distances when the first points are non-monotonic', () => {
		// Distances already in km (<100, no metre conversion), but non-monotonic → recalc.
		const track = [
			{ lat: 45.0, lng: 9.0, altitude: 0, speed: 0, distance: 5 },
			{ lat: 45.01, lng: 9.0, altitude: 0, speed: 0, distance: 5 }, // not > previous → triggers recalc
		];
		normalizeTrackDistances(track);
		expect(must(track[0], 'fixture point 0').distance).toBe(0); // recalc always zeroes the first point
		expect(must(track[1], 'fixture point 1').distance).toBeGreaterThan(0); // real haversine over ~1.1km
	});

	// MS-05: whole-track unit decision + full monotonic scan (per-point >100 heuristic corrupted
	// >100 km tracks and missed sub-100 m metre values).
	it('keeps km distances intact past 100 km', () => {
		// ~1° latitude hops ≈ 111 km each → geometric total ~222 km, matching the km reading.
		const track = [
			{ lat: 45, lng: 9, altitude: 0, speed: 0, distance: 0 },
			{ lat: 46, lng: 9, altitude: 0, speed: 0, distance: 111 },
			{ lat: 47, lng: 9, altitude: 0, speed: 0, distance: 222 },
		];
		normalizeTrackDistances(track);
		expect(track.map((p) => p.distance)).toEqual([0, 111, 222]);
	});

	it('converts an all-metres track to km including values under 100', () => {
		// ~0.1° hops ≈ 11 km each; distances are in metres → whole track divided by 1000.
		const track = [
			{ lat: 45, lng: 9, altitude: 0, speed: 0, distance: 0 },
			{ lat: 45.1, lng: 9, altitude: 0, speed: 0, distance: 11000 },
			{ lat: 45.2, lng: 9, altitude: 0, speed: 0, distance: 22000 },
		];
		normalizeTrackDistances(track);
		expect(must(track[0], 'fixture point 0').distance).toBeCloseTo(0, 6);
		expect(must(track[1], 'fixture point 1').distance).toBeCloseTo(11, 6);
		expect(must(track[2], 'fixture point 2').distance).toBeCloseTo(22, 6);
	});

	it('recalculates when distances are non-monotonic beyond the first 10 points', () => {
		// 13 points (~0.01° apart ≈ 1.1 km hops), km-scale distances monotonic through index 10,
		// then a dip at index 11 — the old first-10 scan missed it; the full scan repairs it.
		const track = Array.from({ length: 13 }, (_, i) => ({
			lat: 45 + i * 0.01, lng: 9, altitude: 0, speed: 0, distance: i,
		}));
		must(track[11], 'fixture point 11').distance = 9; // dip below track[10] (=10)
		normalizeTrackDistances(track);
		for (let i = 1; i < track.length; i++) {
			expect(must(track[i], 'i < length (loop-bounded)').distance)
				.toBeGreaterThan(must(track[i - 1], 'i-1 >= 0 (loop-bounded)').distance);
		}
	});
});

describe('mapFullscreen — MS-10 wire-once guard', () => {
	afterEach(() => { document.body.innerHTML = ''; });

	it('wiring the same fullscreen button twice toggles once per click', () => {
		document.body.innerHTML = '<div id="fsCard"></div><button id="fsBtn"></button>';
		mapFullscreen({ btnId: 'fsBtn', cardId: 'fsCard' });
		mapFullscreen({ btnId: 'fsBtn', cardId: 'fsCard' }); // second wiring must be a no-op

		document.getElementById('fsBtn')!.click();

		// A double-wired button would toggle twice (net off); the guard keeps it a single toggle.
		expect(document.getElementById('fsCard')!.classList.contains('map-fullscreen')).toBe(true);
	});
});

describe('withHandlersShielded — a camera write MapLibre\'s handlers cannot see', () => {
	// Every camera setter ends in jumpTo → stop() → handlers.stop() → reset() on every handler, which
	// deletes a forming drag's _lastPoint and kills it. HandlerManager.stop() early-returns while
	// _updatingCamera is set, so borrowing that flag makes our write invisible to the handlers.
	function fakeMap(): { handlers: { _updatingCamera: boolean } } {
		return { handlers: { _updatingCamera: false } };
	}

	it('raises the flag for the write and hands it straight back', () => {
		const map = fakeMap();
		const seen: boolean[] = [];

		withHandlersShielded(map as never, () => seen.push(map.handlers._updatingCamera));

		expect(seen).toEqual([true]);
		expect(map.handlers._updatingCamera).toBe(false);
	});

	it('restores a flag that was ALREADY raised, so a nested write cannot strip the outer shield', () => {
		const map = fakeMap();
		map.handlers._updatingCamera = true;

		withHandlersShielded(map as never, () => {});

		expect(map.handlers._updatingCamera).toBe(true);
	});

	it('restores the flag when the write throws — a wedged shield would silence real gestures', () => {
		const map = fakeMap();

		expect(() => withHandlersShielded(map as never, () => { throw new Error('disposed'); }))
			.toThrow('disposed');
		expect(map.handlers._updatingCamera).toBe(false);
	});

	it('still runs the write on a map with no handlers at all', () => {
		const write = vi.fn();

		withHandlersShielded({} as never, write);

		expect(write).toHaveBeenCalledTimes(1);
	});
});

describe('applyTerrainElevation — the 3D re-clamp must not reset a forming gesture', () => {
	/** A tilted 3D map: terrain on, pitch above the gate, elevation ready. */
	function terrainMap(): Record<string, unknown> & { handlers: { _updatingCamera: boolean } } {
		return {
			handlers: { _updatingCamera: false },
			getTerrain: () => ({}),
			queryTerrainElevation: () => 1200,
			getPitch: () => 45,
			setCenterElevation: vi.fn(),
		} as never;
	}

	it('writes the elevation shielded — setCenterElevation IS a jumpTo, at ~5×/s', () => {
		const map = terrainMap();
		const shieldedDuringWrite: boolean[] = [];
		(map.setCenterElevation as ReturnType<typeof vi.fn>).mockImplementation(() => {
			shieldedDuringWrite.push(map.handlers._updatingCamera);
		});

		applyTerrainElevation(map as never, 8.1, 47.9, { lastTime: 0 });

		expect(map.setCenterElevation).toHaveBeenCalledWith(1200);
		expect(shieldedDuringWrite).toEqual([true]);
		expect(map.handlers._updatingCamera).toBe(false);
	});

	it('leaves a 2D map alone — no terrain, no camera write and no pitch read', () => {
		const map = terrainMap();
		map.getTerrain = () => null;
		map.getPitch = vi.fn();

		applyTerrainElevation(map as never, 8.1, 47.9, { lastTime: 0 });

		expect(map.setCenterElevation).not.toHaveBeenCalled();
		expect(map.getPitch).not.toHaveBeenCalled();
	});

	it('skips at or below minPitch, where the offset is not worth a camera write', () => {
		const map = terrainMap();
		map.getPitch = () => 5;

		applyTerrainElevation(map as never, 8.1, 47.9, { lastTime: 0 });      // default minPitch 10
		expect(map.setCenterElevation).not.toHaveBeenCalled();

		applyTerrainElevation(map as never, 8.1, 47.9, { lastTime: 0 }, undefined, 1);
		expect(map.setCenterElevation).toHaveBeenCalledTimes(1);
	});
});

describe('safeCleanupLayers — removes all layers before any source', () => {
	it('removes layers first, then sources, in order', () => {
		const calls: string[] = [];
		const map = {
			getLayer: () => ({}) as unknown,
			removeLayer: (id: string) => calls.push('layer:' + id),
			getSource: () => ({}) as unknown,
			removeSource: (id: string) => calls.push('source:' + id),
			// eslint-disable-next-line @typescript-eslint/no-explicit-any
		} as any;
		safeCleanupLayers(map, ['l1', 'l2'], ['s1', 's2']);
		expect(calls).toEqual(['layer:l1', 'layer:l2', 'source:s1', 'source:s2']);
	});
});
