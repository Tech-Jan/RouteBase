// @vitest-environment node
import { describe, it, expect } from 'vitest';
import type { GpsPoint, GpsTrackWire } from './core-types';
import { expandGpsTrack, trackSeconds } from './gps-data';
import { must } from './invariant';

describe('expandGpsTrack', () => {
	it('reconstructs one GpsPoint per index from parallel arrays', () => {
		const wire: GpsTrackWire = {
			gpsId: [1, 2],
			lat: [51.5, 51.6],
			lng: [-0.1, -0.2],
			altitude: [10, 20],
			speed: [5.4, 7.2],
			distance: [0, 0.1],
			t: [1700000000, 1700000060],
			heartRate: [120, 130],
			cadence: [80, 82],
		};

		const points = expandGpsTrack(wire, 0);

		expect(points).toHaveLength(2);
		expect(points[0]).toMatchObject({
			gpsId: 1, lat: 51.5, lng: -0.1, altitude: 10, speed: 5.4, distance: 0,
			heartRate: 120, cadence: 80,
		});
		expect(points[1]).toMatchObject({ gpsId: 2, lat: 51.6, distance: 0.1 });
	});

	it('renders time as local HH:mm:ss, applying the timezone offset to the epoch second', () => {
		const wire: GpsTrackWire = {
			gpsId: [1], lat: [0], lng: [0], altitude: [0], speed: [0], distance: [0],
			t: [1700000000], // 2023-11-14T22:13:20Z
			heartRate: [0], cadence: [0],
		};

		expect(must(expandGpsTrack(wire, 0)[0], 'one point').time).toBe('22:13:20');
		expect(must(expandGpsTrack(wire, 2)[0], 'one point').time).toBe('00:13:20'); // +2h wraps past midnight
		expect(must(expandGpsTrack(wire, -1)[0], 'one point').time).toBe('21:13:20');
	});

	it('renders an empty time string for a null timestamp', () => {
		const wire: GpsTrackWire = {
			gpsId: [1], lat: [0], lng: [0], altitude: [0], speed: [0], distance: [0],
			t: [null], heartRate: [0], cadence: [0],
		};

		expect(must(expandGpsTrack(wire, 0)[0], 'one point').time).toBe('');
	});

	it('keeps elapsed seconds as a NUMBER, not only as a display string', () => {
		// The epoch column was formatted to "HH:mm:ss" and discarded, which left the page with no time
		// axis and forced chart smoothing to count in samples.
		const wire: GpsTrackWire = {
			gpsId: [1, 2, 3], lat: [0, 0, 0], lng: [0, 0, 0], altitude: [0, 0, 0],
			speed: [0, 0, 0], distance: [0, 0, 0],
			t: [1700000000, 1700000004, 1700000030],
			heartRate: [0, 0, 0], cadence: [0, 0, 0],
		};

		const points = expandGpsTrack(wire, 0);
		expect(points.map((p) => p._cumulativeTime)).toEqual([0, 4, 30]);
		// The offset is a display concern only — elapsed seconds must not move with the timezone.
		expect(expandGpsTrack(wire, 5).map((p) => p._cumulativeTime)).toEqual([0, 4, 30]);
	});

	it('measures elapsed from the first REAL timestamp when the track starts with a hole', () => {
		const wire: GpsTrackWire = {
			gpsId: [1, 2, 3], lat: [0, 0, 0], lng: [0, 0, 0], altitude: [0, 0, 0],
			speed: [0, 0, 0], distance: [0, 0, 0],
			t: [null, 1700000000, 1700000010],
			heartRate: [0, 0, 0], cadence: [0, 0, 0],
		};

		expect(expandGpsTrack(wire, 0).map((p) => p._cumulativeTime)).toEqual([undefined, 0, 10]);
	});
});

describe('trackSeconds', () => {
	function pts(seconds: (number | undefined)[]): GpsPoint[] {
		return seconds.map((s) => ({ lat: 0, lng: 0, altitude: 0, speed: 0, distance: 0, _cumulativeTime: s }));
	}

	it('returns the elapsed axis when the track has one', () => {
		expect(trackSeconds(pts([0, 3, 9, 14]))).toEqual([0, 3, 9, 14]);
	});

	it('returns null when the track has no usable timestamps — the fallback trigger', () => {
		expect(trackSeconds(pts([undefined, undefined, undefined]))).toBeNull();
		expect(trackSeconds(pts([0, undefined]))).toBeNull(); // one real sample is not an axis
		expect(trackSeconds(pts([0]))).toBeNull();
		expect(trackSeconds([])).toBeNull();
	});

	it('returns null when time runs backwards — that is not a time axis either', () => {
		// A forward window walk would be wrong here, so fall back rather than smooth against garbage.
		expect(trackSeconds(pts([0, 10, 4, 20]))).toBeNull();
	});

	it('carries interior holes through as NaN rather than rejecting the whole axis', () => {
		const out = trackSeconds(pts([0, undefined, 20]));
		expect(out).not.toBeNull();
		expect(out?.[0]).toBe(0);
		expect(out?.[1]).toBeNaN();
		expect(out?.[2]).toBe(20);
	});
});
