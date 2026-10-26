// @vitest-environment node
import type { TrackPoint } from '../core/core-types';
import type { SegmentEffort } from './segment-stats-effort-types';
import { describe, it, expect } from 'vitest';
import { buildEffortFeatures } from './segment-stats-map-features';
import { must } from '../core/invariant';

// SS-features pins for the pure GPS→GeoJSON effort builder extracted from the MapLibre engine.
// segment-stats-map.test.ts pins which layers each view asks for; nothing pinned the GeoJSON the
// combined effort source is fed. These lock: coordinate ORDER ([lng,lat] — the classic silent
// lat/lng swap), the before/inside/after segmentation around startIdx/endIdx, and per-feature props.

// gps[i].lat = 10+i, gps[i].lng = 20+i → toCoord = [lng,lat] = [20+i, 10+i]
const GPS: TrackPoint[] = [
	{ lat: 10, lng: 20 }, { lat: 11, lng: 21 }, { lat: 12, lng: 22 }, { lat: 13, lng: 23 }, { lat: 14, lng: 24 },
] as TrackPoint[];

function effort(over: Partial<SegmentEffort> = {}): SegmentEffort {
	return {
		trackId: 7, name: 'Foo', date: '2026-01-02', time: 123, entryId: 0, exitId: 0,
		rank: 3, colorIndex: 1, color: '#abcdef', ...over,
	};
}

const coordsOf = (fs: GeoJSON.Feature[]) =>
	fs.map((f) => (f.geometry as GeoJSON.LineString).coordinates);
const onSegOf = (fs: GeoJSON.Feature[]) =>
	fs.map((f) => (f.properties as Record<string, unknown>)._onSeg);

describe('buildEffortFeatures — segmentation + coordinate order', () => {
	it('mid-track split yields pre/on/post with correct slice boundaries and [lng,lat] order', () => {
		const fs = buildEffortFeatures(GPS, effort(), 4, { startIdx: 1, endIdx: 3 });
		expect(fs).toHaveLength(3);
		// _onSeg: pre=0, on=1, post=0
		expect(onSegOf(fs)).toEqual([0, 1, 0]);
		// pre = gps.slice(0, startIdx+1) = idx 0,1 ; on = slice(startIdx, endIdx+1) = 1,2,3 ; post = slice(endIdx) = 3,4
		expect(coordsOf(fs)).toEqual([
			[[20, 10], [21, 11]],
			[[21, 11], [22, 12], [23, 13]],
			[[23, 13], [24, 14]],
		]);
	});

	it('full-track split (0..len-1) yields ONLY the on-segment feature — no pre, no post', () => {
		const fs = buildEffortFeatures(GPS, effort(), 0, { startIdx: 0, endIdx: 4 });
		expect(fs).toHaveLength(1);
		expect(onSegOf(fs)).toEqual([1]);
		expect(coordsOf(fs)[0]).toEqual([[20, 10], [21, 11], [22, 12], [23, 13], [24, 14]]);
	});

	it('startIdx=0 but endIdx<len-1 yields on + post (no pre)', () => {
		const fs = buildEffortFeatures(GPS, effort(), 0, { startIdx: 0, endIdx: 2 });
		expect(onSegOf(fs)).toEqual([1, 0]);
		expect(coordsOf(fs)).toEqual([
			[[20, 10], [21, 11], [22, 12]],
			[[22, 12], [23, 13], [24, 14]],
		]);
	});

	it('startIdx>0 but endIdx=len-1 yields pre + on (no post)', () => {
		const fs = buildEffortFeatures(GPS, effort(), 0, { startIdx: 2, endIdx: 4 });
		expect(onSegOf(fs)).toEqual([0, 1]);
		expect(coordsOf(fs)).toEqual([
			[[20, 10], [21, 11], [22, 12]],
			[[22, 12], [23, 13], [24, 14]],
		]);
	});
});

describe('buildEffortFeatures — feature properties', () => {
	it('every feature carries color, _effortIndex, name, date, rank + its own _onSeg', () => {
		const fs = buildEffortFeatures(GPS, effort({ color: '#ff0000', name: 'Ride', date: '2026-03-04', rank: 9 }), 5, { startIdx: 1, endIdx: 3 });
		for (const f of fs) {
			const p = f.properties as Record<string, unknown>;
			expect(p.color).toBe('#ff0000');
			expect(p._effortIndex).toBe(5);
			expect(p.name).toBe('Ride');
			expect(p.date).toBe('2026-03-04');
			expect(p.rank).toBe(9);
		}
		expect(fs.map((f) => (f.properties as Record<string, unknown>)._onSeg)).toEqual([0, 1, 0]);
	});

	it('missing name/date default to "" and missing rank to 0', () => {
		const fs = buildEffortFeatures(GPS, effort({ name: '', date: '', rank: 0, color: undefined }), 2, { startIdx: 0, endIdx: 4 });
		const p = must(fs[0], 'one feature').properties as Record<string, unknown>;
		expect(p.name).toBe('');
		expect(p.date).toBe('');
		expect(p.rank).toBe(0);
		expect(p.color).toBeUndefined();
		expect(p._effortIndex).toBe(2);
	});
});
