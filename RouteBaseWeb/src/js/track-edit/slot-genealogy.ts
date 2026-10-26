/**
 * slot-genealogy.ts — pure client walk of a script's slot-id bookkeeping (A4 §2.2), the client twin of the
 * server SaveMapper's slot→root walk. UI-only (chip tooltips, per-slot tz D-A4-7, split-off labels) — never
 * persisted, never parity-bound; it is drift-pinned against `applyScript`'s live slot ids/origins in vitest.
 *
 * Walks ONLY the slot-id effects, exactly like `applyScript`: `loadActivity` mints the next sequential id
 * (root = its activity), `splitAt` mints a tail whose root = the parent's root, `append` consumes its source
 * id (target root unchanged). Every other op — including a future op the union doesn't know — default-cases to
 * no slot effect (recipe-(a) compliance). Pass the EFFECTIVE prefix (ops[0..cursor)) so ids match the replay.
 */

import type { EditOp } from './edit-ops';

export interface SlotGenealogy {
	/** live slotId → root activityId (the activity the slot ultimately derives from). */
	rootActivityId: Map<number, number>;
	/** live slotIds that are split tails (labelled "… (split)"). */
	splitOff: Set<number>;
}

export function slotGenealogy(ops: readonly EditOp[]): SlotGenealogy {
	const rootAll = new Map<number, number>(); // includes consumed ids, for parent lookups
	const live = new Set<number>();
	const splitOff = new Set<number>();
	let nextId = 0;

	for (const op of ops) {
		switch (op.op) {
			case 'loadActivity': {
				rootAll.set(nextId, op.activityId);
				live.add(nextId);
				nextId++;
				break;
			}
			case 'splitAt': {
				const tailId = nextId++;
				const parentRoot = rootAll.get(op.slot);
				if (parentRoot !== undefined) rootAll.set(tailId, parentRoot);
				live.add(tailId);
				splitOff.add(tailId);
				break;
			}
			case 'append': {
				live.delete(op.source); // consumed; target keeps its id + root
				break;
			}
			default:
				break; // cropTo / deleteRange / any future op: no slot-id effect
		}
	}

	const rootActivityId = new Map<number, number>();
	for (const id of live) {
		const root = rootAll.get(id);
		if (root !== undefined) rootActivityId.set(id, root);
	}
	for (const id of [...splitOff]) if (!live.has(id)) splitOff.delete(id);
	return { rootActivityId, splitOff };
}
