// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from '../core/invariant';
import { channelsToGpsPoints } from './edit-preview-render';
import type { SlotTrack } from './apply-script';

const f64 = (v: number[]): Float64Array => Float64Array.from(v);

describe('edit-preview-render channelsToGpsPoints', () => {
	it('converts units like the Razor island', () => {
		const slot: SlotTrack = {
			count: 2,
			channels: {
				lat: f64([47.0, 47.5]),
				lng: f64([8.0, 8.5]),
				altitude: f64([100, 150]),   // metres, passthrough
				distance: f64([0, 2000]),    // metres → km
				speed: f64([5, 10]),         // m/s → km/h
			},
		};
		const points = channelsToGpsPoints(slot, 0);
		expect(must(points[1], 'point 1').lat).toBe(47.5);
		expect(must(points[1], 'point 1').altitude).toBe(150);
		expect(must(points[1], 'point 1').distance).toBe(2);   // 2000 m → 2 km
		expect(must(points[1], 'point 1').speed).toBe(36);     // 10 m/s → 36 km/h
	});

	it('formats time as HH:mm:ss with tzOffsetHours applied', () => {
		const slot: SlotTrack = { count: 2, channels: { time: f64([0, NaN]) } };
		const points = channelsToGpsPoints(slot, 2); // 1970-01-01T00:00:00Z + 2h → 02:00:00
		expect(must(points[0], 'point 0').time).toBe('02:00:00');
		expect(must(points[1], 'point 1').time).toBe(''); // NaN sample → empty string
	});

	it('NaN samples and absent channels become 0-valued fields', () => {
		const slot: SlotTrack = {
			count: 2,
			channels: { lat: f64([47, 47]), lng: f64([8, 8]), altitude: f64([100, NaN]) },
		};
		const points = channelsToGpsPoints(slot, 0);
		expect(must(points[0], 'point 0').distance).toBe(0);   // absent channel → 0
		expect(must(points[0], 'point 0').speed).toBe(0);      // absent channel → 0
		expect(must(points[0], 'point 0').heartRate).toBe(0);
		expect(must(points[0], 'point 0').cadence).toBe(0);
		expect(must(points[1], 'point 1').altitude).toBe(0);   // NaN sample → 0
	});

	it('returns a fresh array identity per call', () => {
		const slot: SlotTrack = { count: 1, channels: { lat: f64([47]), lng: f64([8]) } };
		const a = channelsToGpsPoints(slot, 0);
		const b = channelsToGpsPoints(slot, 0);
		expect(a).not.toBe(b);       // MS-08: fresh array invalidates coord/color caches
		expect(a[0]).not.toBe(b[0]); // fresh point objects too
	});
});
