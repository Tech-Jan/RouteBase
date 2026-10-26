// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { slotGenealogy } from './slot-genealogy';
import { applyScript, type SlotTrack } from './apply-script';
import type { EditOp, EditScript } from './edit-ops';

const f64 = (v: number[]): Float64Array => Float64Array.from(v);

function track(n: number): SlotTrack {
	const time: number[] = [], lat: number[] = [], lng: number[] = [], distance: number[] = [];
	for (let i = 0; i < n; i++) { time.push(1000 + i * 10); lat.push(47 + i * 0.001); lng.push(8 + i * 0.001); distance.push(i * 100); }
	return { count: n, channels: { time: f64(time), lat: f64(lat), lng: f64(lng), distance: f64(distance) } };
}

const script = (ops: EditOp[]): EditScript => ({ schemaVersion: 1, ops });
const sources = new Map<number, SlotTrack>([[101, track(6)], [102, track(6)]]);

describe('slot-genealogy', () => {
	it('slot roots match applyScript origins on a mixed script (V1)', () => {
		const ops: EditOp[] = [
			{ op: 'loadActivity', activityId: 101 },
			{ op: 'loadActivity', activityId: 102 },
			{ op: 'splitAt', slot: 0, index: 2 },
		];
		const outcome = applyScript(sources, script(ops));
		expect(outcome.ok).toBe(true);
		if (!outcome.ok) return;

		const gen = slotGenealogy(ops);
		// live ids match the replay
		expect([...gen.rootActivityId.keys()].sort((a, b) => a - b))
			.toEqual(outcome.slots.map((s) => s.slotId).sort((a, b) => a - b));
		// each replay slot's origin resolves to the same root
		for (const slot of outcome.slots) {
			if (slot.origin.activityId !== undefined) {
				expect(gen.rootActivityId.get(slot.slotId)).toBe(slot.origin.activityId);
			} else {
				const parent = slot.origin.splitFromSlotId!;
				expect(gen.rootActivityId.get(slot.slotId)).toBe(gen.rootActivityId.get(parent));
			}
		}
	});

	it('split tail inherits the parent root (V2)', () => {
		const gen = slotGenealogy([
			{ op: 'loadActivity', activityId: 101 },
			{ op: 'loadActivity', activityId: 102 },
			{ op: 'splitAt', slot: 0, index: 2 },
		]);
		expect(gen.splitOff.has(2)).toBe(true);
		expect(gen.rootActivityId.get(2)).toBe(101); // parent slot 0's root
	});

	it('append consumes the source id, target root unchanged (V3)', () => {
		const gen = slotGenealogy([
			{ op: 'loadActivity', activityId: 101 },
			{ op: 'loadActivity', activityId: 102 },
			{ op: 'append', target: 0, source: 1, timeMode: 'keep' },
		]);
		expect([...gen.rootActivityId.keys()]).toEqual([0]); // slot 1 consumed
		expect(gen.rootActivityId.get(0)).toBe(101);
		expect(gen.rootActivityId.has(1)).toBe(false);
	});

	it('an unknown future op is a no-slot-effect (V4)', () => {
		const future = { op: 'reverse', slot: 0 } as unknown as EditOp; // an op the union does not know
		const gen = slotGenealogy([
			{ op: 'loadActivity', activityId: 101 },
			future,
			{ op: 'loadActivity', activityId: 102 },
		]);
		expect([...gen.rootActivityId.entries()].sort((a, b) => a[0] - b[0]))
			.toEqual([[0, 101], [1, 102]]); // future op did not mint or consume an id
	});
});
