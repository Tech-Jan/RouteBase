// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { dequantizeSources } from './edit-api';
import { CHANNEL_SCALES } from './apply-script';
import type { SessionSourcesResponse } from './edit-session-api';

/**
 * dequantizeSources/dequantizeSource: the ingest-side twin of apply-script's `requantize` — wire-quantized
 * codec ints back to Float64Array preview space. Every edit session load goes through this; a wrong scale or
 * dropped channel silently corrupts the values a user then edits.
 */

describe('dequantizeSources', () => {
	it('scales every present channel by its CHANNEL_SCALES factor', () => {
		const response: SessionSourcesResponse = {
			sources: [{
				activityId: 1, name: 'Morning Ride', tzOffsetHours: 2, count: 3,
				channels: {
					time: [1_000, 1_010, 1_020],
					lat: [52_500_000, 52_500_100, 52_500_200],
					altitude: [1005, 1010, 1015],
					heartRate: [140, 142, 144],
				},
			}],
		};

		const { slots } = dequantizeSources(response);
		const track = slots.get(1)!;

		expect(Array.from(track.channels.time!)).toEqual([1000, 1010, 1020]);
		expect(track.channels.lat![0]).toBeCloseTo(52.5, 6);
		expect(track.channels.lat![1]).toBeCloseTo(52.5001, 6);
		expect(track.channels.altitude![0]).toBeCloseTo(100.5, 6);
		expect(track.channels.heartRate![0]).toBe(140); // scale 1 — identity
	});

	it('maps a null sample to NaN, not zero — the missing-point sentinel apply-script expects', () => {
		const response: SessionSourcesResponse = {
			sources: [{
				activityId: 2, name: 'Gap Ride', tzOffsetHours: 0, count: 3,
				channels: { altitude: [1000, null, 1020] },
			}],
		};

		const track = dequantizeSources(response).slots.get(2)!;

		expect(track.channels.altitude![0]).toBeCloseTo(100, 6);
		expect(Number.isNaN(track.channels.altitude![1])).toBe(true);
		expect(track.channels.altitude![2]).toBeCloseTo(102, 6);
	});

	it('drops an absent channel key entirely — a whole-NULL channel is absent, not an all-NaN array', () => {
		const response: SessionSourcesResponse = {
			sources: [{ activityId: 3, name: 'No HR', tzOffsetHours: 0, count: 2, channels: { time: [0, 10] } }],
		};

		const track = dequantizeSources(response).slots.get(3)!;

		expect(track.channels.heartRate).toBeUndefined();
		expect('heartRate' in track.channels).toBe(false);
	});

	it('carries count through independent of channel lengths', () => {
		const response: SessionSourcesResponse = {
			sources: [{ activityId: 4, name: 'X', tzOffsetHours: 0, count: 5, channels: {} }],
		};

		expect(dequantizeSources(response).slots.get(4)!.count).toBe(5);
	});

	it('round-trips against requantize (apply-script twin) for every channel scale', () => {
		const raw = { time: 5_000, lat: 41_123_456, distance: 12_345, speed: 278, power: 250 } as const;
		const response: SessionSourcesResponse = {
			sources: [{
				activityId: 5, name: 'Round Trip', tzOffsetHours: 0, count: 1,
				channels: Object.fromEntries(Object.entries(raw).map(([k, v]) => [k, [v]])),
			}],
		};

		const track = dequantizeSources(response).slots.get(5)!;
		for (const [name, quantized] of Object.entries(raw)) {
			const scale = CHANNEL_SCALES[name as keyof typeof CHANNEL_SCALES];
			const dequantized = track.channels[name as keyof typeof track.channels]![0];
			expect(dequantized).toBeCloseTo(quantized * scale, 9);
		}
	});

	it('builds independent tzByActivity and nameByActivity maps keyed by activityId, for every source', () => {
		const response: SessionSourcesResponse = {
			sources: [
				{ activityId: 10, name: 'First', tzOffsetHours: 2, count: 0, channels: {} },
				{ activityId: 11, name: 'Second', tzOffsetHours: -5, count: 0, channels: {} },
			],
		};

		const { tzByActivity, nameByActivity, slots } = dequantizeSources(response);

		expect(tzByActivity.get(10)).toBe(2);
		expect(tzByActivity.get(11)).toBe(-5);
		expect(nameByActivity.get(10)).toBe('First');
		expect(nameByActivity.get(11)).toBe('Second');
		expect(slots.size).toBe(2);
	});

	it('an empty sources list dequantizes to empty maps', () => {
		const result = dequantizeSources({ sources: [] });

		expect(result.slots.size).toBe(0);
		expect(result.tzByActivity.size).toBe(0);
		expect(result.nameByActivity.size).toBe(0);
	});
});
