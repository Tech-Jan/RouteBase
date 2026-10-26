// @vitest-environment node
import { describe, expect, it } from 'vitest';
import {
	appendCloseGap, appendSynthesizeGap, deleteRangeCloseGap, deleteRangeSynthesizeGap,
} from './time-gap';
import type { ChannelMap } from './apply-script';

// The §2.2 UI gap formulas — defined once, tested once.

const f64 = (v: number[]) => Float64Array.from(v);

describe('time-gap formulas', () => {
	it('deleteRange close = spacing of the first deleted point', () => {
		const time = f64([1000, 1010, 1025, 1040]);
		expect(deleteRangeCloseGap(time, 2)).toBe(15); // t[2] - t[1]
	});

	it('deleteRange close at head = 0', () => {
		expect(deleteRangeCloseGap(f64([1000, 1010, 1020]), 0)).toBe(0);
	});

	it('deleteRange synthesize uses the distance channel, falls back to close', () => {
		const time = f64([0, 10, 20, 30]);
		const distance = f64([0, 100, 200, 300]); // metres; avgSpeed = 300/30 = 10 m/s
		// junctionMeters = dist[to+1] - dist[from-1] = dist[3] - dist[0] = 300; 300 / 10 = 30
		expect(deleteRangeSynthesizeGap(time, distance, 1, 2)).toBe(30);
		// no distance channel -> fall back to close (spacing of first deleted point)
		expect(deleteRangeSynthesizeGap(time, undefined, 1, 2)).toBe(deleteRangeCloseGap(time, 1));
	});

	it('append close = target last inter-point delta, fallback 1s', () => {
		expect(appendCloseGap(f64([1000, 1010, 1030]))).toBe(20); // 1030 - 1010
		expect(appendCloseGap(f64([1000]))).toBe(1); // no prior point -> 1
		expect(appendCloseGap(f64([NaN, NaN]))).toBe(1); // no present points -> 1
	});

	it('append synthesize = haversine / avgSpeed rounded away-from-zero, fallback to close', () => {
		// No ChannelMap annotation: concrete channel types let noDist below reuse them without
		// re-optionalizing (eOPT rejects explicit undefined re-entry through Partial reads).
		const target = {
			time: f64([0, 10, 20]),
			distance: f64([0, 100, 200]), // avgSpeed = 200 / 20 = 10 m/s
			lat: f64([0, 0, 0]),
			lng: f64([0, 0.001, 0.002]),
		};
		const source: ChannelMap = { lat: f64([0]), lng: f64([0.01]) };

		const expected = Math.sign(1) * Math.round(haversine(0, 0.002, 0, 0.01) / 10);
		expect(appendSynthesizeGap(target, source)).toBe(expected);

		// missing target distance -> fall back to append close
		const noDist: ChannelMap = { time: target.time, lat: target.lat, lng: target.lng };
		expect(appendSynthesizeGap(noDist, source)).toBe(appendCloseGap(target.time));
	});
});

// Independent haversine to cross-check the wiring (avgSpeed + division + rounding).
function haversine(lat1: number, lng1: number, lat2: number, lng2: number): number {
	const r = 6371000;
	const toRad = Math.PI / 180;
	const dLat = (lat2 - lat1) * toRad;
	const dLng = (lng2 - lng1) * toRad;
	const a = Math.sin(dLat / 2) ** 2 + Math.cos(lat1 * toRad) * Math.cos(lat2 * toRad) * Math.sin(dLng / 2) ** 2;
	return 2 * r * Math.asin(Math.min(1, Math.sqrt(a)));
}
