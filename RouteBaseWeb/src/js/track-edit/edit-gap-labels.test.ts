// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { formatGap, cutGapLabels } from './edit-gap-labels';
import { deleteRangeCloseGap, deleteRangeSynthesizeGap } from './time-gap';
import type { ChannelMap } from './apply-script';

const f64 = (v: number[]): Float64Array => Float64Array.from(v);

describe('edit-gap-labels', () => {
	it('formats gap durations (+42 min / +3 s / −5 s / +1.2 h)', () => {
		expect(formatGap(2520)).toBe('+42 min');
		expect(formatGap(3)).toBe('+3 s');
		expect(formatGap(-5)).toBe('−5 s'); // typographic minus preserves non-monotonic truth (E21)
		expect(formatGap(4320)).toBe('+1.2 h');
	});

	it('cut popover labels: keep shows real gap; close/synthesize show time-gap results', () => {
		const channels: ChannelMap = { time: f64([0, 10, 20, 30, 40]), distance: f64([0, 100, 200, 300, 400]) };
		const labels = cutGapLabels(channels, 1, 2, 5);

		// keep: real gap = time[to+1] − time[from−1] = time[3] − time[0]
		expect(labels.realGapSeconds).toBe(30);
		expect(labels.realGapLabel).toBe('+30 s');

		// close / synthesize taken verbatim from the invariant-9 formulas (never re-derived)
		expect(labels.closeGapSeconds).toBe(deleteRangeCloseGap(channels.time as Float64Array, 1));
		expect(labels.synthesizeGapSeconds).toBe(
			deleteRangeSynthesizeGap(channels.time as Float64Array, channels.distance, 1, 2),
		);
		expect(labels.closeLabel).toBe('10 s');
		expect(labels.synthesizeLabel).toBe('30 s');
	});
});
