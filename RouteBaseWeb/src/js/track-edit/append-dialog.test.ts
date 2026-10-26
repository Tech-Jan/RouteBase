// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { buildAppendModel } from './append-dialog';
import { appendCloseGap, appendSynthesizeGap } from './time-gap';
import type { ReplaySlot, ChannelMap } from './apply-script';

const f64 = (v: number[]): Float64Array => Float64Array.from(v);

function slot(slotId: number, channels: ChannelMap, count: number): ReplaySlot {
	return { slotId, origin: { activityId: 100 + slotId }, track: { count, channels } };
}

describe('buildAppendModel', () => {
	it('real gap = first-present source time − last-present target time, NullQ-skipped (V13)', () => {
		const target = slot(0, { time: f64([100, 110, NaN]) }, 3); // last present 110
		const source = slot(1, { time: f64([NaN, 200, 210]) }, 3); // first present 200
		const model = buildAppendModel([target, source], 0, 1);
		expect(model.realGapSeconds).toBe(90);
	});

	it('negative gap is formatted with a sign (V14)', () => {
		const target = slot(0, { time: f64([100, 200]) }, 2);
		const source = slot(1, { time: f64([150, 160]) }, 2); // starts before target ends
		const model = buildAppendModel([target, source], 0, 1);
		expect(model.realGapSeconds).toBe(-50);
		expect(model.gapLabel.startsWith('−')).toBe(true); // typographic minus
	});

	it('close/synthesize seconds come verbatim from time-gap (V15)', () => {
		const target: ChannelMap = { time: f64([0, 10, 20]), distance: f64([0, 100, 200]), lat: f64([47, 47.001, 47.002]), lng: f64([8, 8, 8]) };
		const source: ChannelMap = { time: f64([100, 110]), distance: f64([0, 100]), lat: f64([47.01, 47.011]), lng: f64([8.01, 8.011]) };
		const model = buildAppendModel([slot(0, target, 3), slot(1, source, 2)], 0, 1);
		expect(model.closeGapSeconds).toBe(appendCloseGap(target.time!));
		expect(model.synthesizeGapSeconds).toBe(appendSynthesizeGap(target, source));
	});

	it('whole-NULL time disables close/synthesize, keeps keep (V16)', () => {
		const target = slot(0, { time: f64([NaN, NaN]) }, 2);
		const source = slot(1, { time: f64([100, 110]) }, 2);
		const model = buildAppendModel([target, source], 0, 1);
		expect(model.timeOptionsEnabled).toBe(false);
		expect(model.realGapSeconds).toBeNull();
		expect(model.gapLabel).toBe('no timestamps');
		expect(model.defaultTimeMode).toBe('keep');
	});

	it('default timeMode is keep and pickers exclude each other (V17)', () => {
		const slots = [slot(0, { time: f64([0, 1]) }, 2), slot(1, { time: f64([0, 1]) }, 2), slot(2, { time: f64([0, 1]) }, 2)];
		const model = buildAppendModel(slots, 0, 1);
		expect(model.defaultTimeMode).toBe('keep');
		expect(model.targetOptions).not.toContain(1); // target list excludes the source
		expect(model.sourceOptions).not.toContain(0); // source list excludes the target
	});
});
