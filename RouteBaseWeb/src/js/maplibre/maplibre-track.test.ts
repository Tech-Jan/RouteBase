// @vitest-environment node
import type { GpsPoint } from '../core/core-types';
import { must } from '../core/invariant';
import { describe, it, expect, afterEach } from 'vitest';
import { api as TRK } from './maplibre-track';
import { getColorForValue } from '../core/colors';

// MS-08: getCachedCoords must key its cache on the gps array identity — a different array
// (e.g. a lazy full-res upgrade) has to rebuild, else the cached [lng,lat] pairs and the
// index-aligned color features silently mismatch the new data. No map needed for this path.

describe('maplibre-track getCachedCoords — MS-08 identity cache', () => {
	it('rebuilds the coord cache when a different gps array is passed', () => {
		const first = TRK.getCachedCoords([{ lat: 1, lng: 2 } as GpsPoint]);
		expect(first).toEqual([[2, 1]]); // coords are [lng, lat]

		const second = TRK.getCachedCoords([
			{ lat: 3, lng: 4 },
			{ lat: 5, lng: 6 },
		] as GpsPoint[]);
		expect(second).toEqual([[4, 3], [6, 5]]);
	});
});

// A fake map recording only what updateTrackColors touches: layer/source add + remove, so the
// built colour FeatureCollection can be inspected.
function fakeMap() {
	const sources: Record<string, { data: unknown }> = {};
	const layers: Record<string, unknown> = {};
	const map = {
		getLayer: (id: string) => layers[id],
		getSource: (id: string) => sources[id],
		removeLayer: (id: string) => { delete layers[id]; },
		removeSource: (id: string) => { delete sources[id]; },
		addSource: (id: string, opts: { data: unknown }) => { sources[id] = { data: opts.data }; },
		addLayer: (opts: { id: string }) => { layers[opts.id] = opts; },
	};
	return { map, sources };
}

// updateTrackColors builds per-segment colour features. The values must stay 1:1 with the
// NaN-filtered coords — a bug (already shipped once) derived them from the RAW gps array, so a
// single invalid point shifted every later segment's colour and value by one.
describe('maplibre-track updateTrackColors — NaN-alignment + colour scale', () => {
	afterEach(() => TRK.destroy());

	it('keeps segment values aligned to the valid points when a mid-track point is invalid', () => {
		const { map, sources } = fakeMap();
		TRK.bind(map as unknown as Parameters<typeof TRK.bind>[0], new WeakMap());

		// The middle point has a NaN lat → dropped from BOTH coords and values.
		const gps = [
			{ lat: 40.0, lng: -70.0, altitude: 100 },
			{ lat: 40.1, lng: -70.0, altitude: 200 },
			{ lat: NaN, lng: -70.0, altitude: 999 },
			{ lat: 40.2, lng: -70.0, altitude: 300 },
			{ lat: 40.3, lng: -70.0, altitude: 400 },
		] as GpsPoint[];

		TRK.updateTrackColors('elevation', gps);

		const fc = sources['track']!.data as GeoJSON.FeatureCollection;
		// 4 valid points → 3 segments; each carries the altitude of its valid start point, NOT 999.
		expect(fc.features.map((f) => f.properties!.value)).toEqual([100, 200, 300]);

		// min/max are computed from the VALID values only (100..400) — the dropped point's 999 never
		// widened the scale, so the first segment gets the min-of-100 colour.
		expect(must(fc.features[0], 'feature 0').properties!.color).toBe(getColorForValue(100, 100, 400, 'elevation'));
		// distinct values map to distinct colours (the scale actually varies across the track).
		expect(must(fc.features[2], 'feature 2').properties!.color).not.toBe(must(fc.features[0], 'feature 0').properties!.color);
	});
});
