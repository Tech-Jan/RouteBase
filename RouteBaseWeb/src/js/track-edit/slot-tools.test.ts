// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from '../core/invariant';
import { slotToolsModel } from './slot-tools';
import { colorForSlot, ACTIVE_SLOT_COLOR } from './slot-overlay';
import type { ReplaySlot } from './apply-script';

const slot = (slotId: number): ReplaySlot =>
	({ slotId, origin: { activityId: 100 + slotId }, track: { count: 3, channels: {} } });

describe('slotToolsModel', () => {
	it('chips listed in slot-id order with the active one flagged (V8)', () => {
		const model = slotToolsModel([slot(2), slot(0), slot(1)], /*active*/ 1, 10);

		expect(model.chips.map((c) => c.slotId)).toEqual([0, 1, 2]);
		expect(model.chips.map((c) => c.active)).toEqual([false, true, false]);
		expect(must(model.chips[1], 'chip 1').color).toBe(ACTIVE_SLOT_COLOR); // active chip = red
		expect(must(model.chips[0], 'chip 0').color).toBe(colorForSlot(0));   // others = palette hue
	});

	it('add is disabled at maxSlots with a reason (V9)', () => {
		const full = Array.from({ length: 10 }, (_, i) => slot(i));
		const model = slotToolsModel(full, 0, 10);

		expect(model.addEnabled).toBe(false);
		expect(model.addHint).toBe('slot limit reached');
	});

	it('append enabled only with >= 2 live slots (V10)', () => {
		expect(slotToolsModel([slot(0)], 0, 10).appendEnabled).toBe(false);
		expect(slotToolsModel([slot(0), slot(1)], 0, 10).appendEnabled).toBe(true);
		expect(slotToolsModel([slot(0)], 0, 10).addEnabled).toBe(true); // add still available below the cap
	});
});
