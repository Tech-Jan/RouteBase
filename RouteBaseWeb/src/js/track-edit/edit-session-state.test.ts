// @vitest-environment node
import { describe, expect, it } from 'vitest';
import {
	appendOp, undo, redo, replay, activeSlot, setActiveSlot, canUndo, canRedo, opCount, fromSession, countSeedOps,
	type EditorState,
} from './edit-session-state';
import type { ReplayOutcome, SlotTrack } from './apply-script';
import type { EditOp, EditSessionState } from './edit-ops';

function f64(v: number[]): Float64Array {
	return Float64Array.from(v);
}

function line(n: number, id = 0): SlotTrack {
	const time: number[] = [];
	const lat: number[] = [];
	const lng: number[] = [];
	const distance: number[] = [];
	for (let i = 0; i < n; i++) {
		time.push(1000 + i * 10);
		lat.push(47 + i * 0.001 + id);
		lng.push(8 + i * 0.001);
		distance.push(i * 100);
	}
	return { count: n, channels: { time: f64(time), lat: f64(lat), lng: f64(lng), distance: f64(distance) } };
}

function session(ops: EditOp[], cursor: number): EditSessionState {
	return { sessionId: 's1', script: { schemaVersion: 1, ops }, cursor, stamps: [], status: 'active' };
}

const load = (id: number): EditOp => ({ op: 'loadActivity', activityId: id });
const crop = (slot: number, from: number, to: number): EditOp => ({ op: 'cropTo', slot, from, to });

/** One source (id 101, `n` points), seeded with a single loadActivity op at cursor 1. */
function base(n = 6): EditorState {
	const sources = new Map<number, SlotTrack>([[101, line(n)]]);
	return fromSession(session([load(101)], 1), sources, 1).state;
}

describe('edit-session-state', () => {
	it('appendOp advances cursor and truncates the redo tail', () => {
		const s0 = base(6);
		const s1 = expectOk(appendOp(s0, crop(0, 0, 4))); // 5 pts
		const s2 = expectOk(appendOp(s1, crop(0, 0, 3))); // 4 pts — the tail we will discard
		expect(s2.cursor).toBe(3);

		const undone = undo(s2)!.state; // cursor 2, redo tail still holds crop(0,0,3)
		const s3 = expectOk(appendOp(undone, crop(0, 1, 4))); // replaces the tail
		expect(s3.ops).toHaveLength(3);
		expect(s3.ops[2]).toEqual(crop(0, 1, 4));
		expect(s3.cursor).toBe(3);
		expect(canRedo(s3)).toBe(false);
	});

	it('appendOp rejects a twin-invalid op without mutating state', () => {
		const s0 = base(6);
		const result = appendOp(s0, crop(0, 0, 99));
		expect(result.ok).toBe(false);
		if (!result.ok) expect(result.error.code).toBe('rangeOutOfBounds');
		expect(s0.ops).toHaveLength(1); // untouched
		expect(s0.cursor).toBe(1);
	});

	it('undo and redo clamp to [seedCount, ops.length]', () => {
		const s0 = base(6);
		expect(undo(s0)).toBeNull(); // at the seed floor — clamped (no replay)
		expect(redo(s0)).toBeNull(); // at the ceiling — clamped (no replay)

		const s1 = expectOk(appendOp(s0, crop(0, 0, 4)));
		const back = undo(s1)!.state;
		expect(back.cursor).toBe(1);
		expect(undo(back)).toBeNull(); // floor again
		const fwd = redo(back)!.state;
		expect(fwd.cursor).toBe(2);
		expect(redo(fwd)).toBeNull(); // ceiling again
	});

	it('replay consumes only the effective prefix', () => {
		const s0 = base(6);
		const split = expectOk(appendOp(s0, { op: 'splitAt', slot: 0, index: 2 })); // 2 slots
		const twoSlots = replay(split);
		expect(twoSlots.ok && twoSlots.slots).toHaveLength(2);

		const undone = undo(split)!.state; // effective = [load] only
		const oneSlot = replay(undone);
		expect(oneSlot.ok && oneSlot.slots).toHaveLength(1);
	});

	it('activeSlot follows split head', () => {
		const s0 = base(6);
		const split = expectOk(appendOp(s0, { op: 'splitAt', slot: 0, index: 2 }));
		const outcome = replay(split);
		expect(outcome.ok && outcome.slots).toHaveLength(2);
		expect(activeSlot(outcome, split)!.slotId).toBe(0); // head keeps the original id
	});

	it('activeSlot falls back when append consumes it', () => {
		const sources = new Map<number, SlotTrack>([[101, line(4, 0)], [102, line(3, 1)]]);
		const seeded = fromSession(session([load(101), load(102)], 2), sources, 2).state;
		const active2nd: EditorState = { ...seeded, activeSlotId: 1 }; // editing slot 1 (activity 102)

		const appended = expectOk(appendOp(active2nd, { op: 'append', target: 0, source: 1, timeMode: 'keep' }));
		const outcome = replay(appended);
		expect(activeSlot(outcome, appended)!.slotId).toBe(0); // source consumed → active is the target
	});

	it('fromSession rebuilds state equal to live-built state', () => {
		const sources = new Map<number, SlotTrack>([[101, line(6)]]);
		const live0 = fromSession(session([load(101)], 1), sources, 1).state;
		const live1 = expectOk(appendOp(live0, { op: 'splitAt', slot: 0, index: 2 }));
		const live2 = expectOk(appendOp(live1, crop(0, 0, 1)));

		const rebuilt = fromSession(session(live2.ops, live2.cursor), sources, countSeedOps(live2.ops));
		expect(rebuilt.state.cursor).toBe(live2.cursor);
		expect(rebuilt.outcome).toEqual(replay(live2)); // fromSession returns the outcome it already computed (R-08)
	});

	it('appendOp refused at maxOps', () => {
		const filler = new Array<EditOp>(500).fill(crop(0, 0, 1));
		const state: EditorState = {
			sessionId: 's1', sources: new Map(), ops: filler, cursor: 500, seedCount: 1, activeSlotId: 0,
		};
		const result = appendOp(state, crop(0, 0, 1));
		expect(result.ok).toBe(false);
		if (!result.ok) expect(result.error.code).toBe('scriptTooLong');
		expect(state.ops).toHaveLength(500); // unchanged
		expect(state.cursor).toBe(500);
	});

	it('canUndo/canRedo reflect cursor position', () => {
		const s0 = base(6);
		expect([canUndo(s0), canRedo(s0), opCount(s0)]).toEqual([false, false, 0]); // seed floor

		const s1 = expectOk(appendOp(s0, crop(0, 0, 4)));
		expect([canUndo(s1), canRedo(s1), opCount(s1)]).toEqual([true, false, 1]); // one edit, at tip

		const back = undo(s1)!.state;
		expect([canUndo(back), canRedo(back)]).toEqual([false, true]); // floor, tail available
	});

	// A4 §2.4 — setActiveSlot is view-state, not an op (V11/V12, additive per MultiTrack §7.1).
	it('setActiveSlot switches to a live slot and no-ops on dead ids (V11)', () => {
		const s0 = base(6);
		const split = expectOk(appendOp(s0, { op: 'splitAt', slot: 0, index: 2 })); // slots 0 + 1
		expect(setActiveSlot(split, 1)!.state.activeSlotId).toBe(1);
		expect(setActiveSlot(split, 99)).toBeNull(); // dead id → null (no switch)
	});

	it('switching the active slot changes no ops/cursor (V12)', () => {
		const s0 = base(6);
		const split = expectOk(appendOp(s0, { op: 'splitAt', slot: 0, index: 2 }));
		const switched = setActiveSlot(split, 1)!.state;
		expect(switched.ops).toBe(split.ops);       // same script reference — not an op
		expect(switched.cursor).toBe(split.cursor); // cursor untouched (no write-through)
	});

	// PR-03 — appendOp resumes from the caller's outcome instead of replaying the whole prefix. The seam must be
	// byte-identical to the full replay or the editor silently corrupts the ride.
	it('appendOp with a prev outcome equals appendOp without one, after every op', () => {
		const script: EditOp[] = [
			load(102),                                              // slot 1
			{ op: 'splitAt', slot: 0, index: 4 },                   // slot 0 (head) + slot 2 (tail)
			crop(2, 1, 6),
			{ op: 'deleteRange', slot: 0, from: 1, to: 2, timeMode: 'close', gapSeconds: 5 },
			{ op: 'append', target: 0, source: 2, timeMode: 'synthesize', gapSeconds: 3 }, // consumes slot 2
			{ op: 'deleteRange', slot: 1, from: 0, to: 1, timeMode: 'keep' },
			{ op: 'splitAt', slot: 1, index: 2 },                   // mints slot 3 AFTER a consumption
			{ op: 'append', target: 1, source: 3, timeMode: 'keep' },
			crop(0, 0, 5),
		];

		let full = base(12);          // no prev → full replay every op
		let fast = base(12);          // prev threaded through → resumeScript every op
		full.sources.set(102, line(9, 1));
		fast.sources.set(102, line(9, 1));
		let fastOutcome = replay(fast);

		for (const op of script) {
			const a = appendOp(full, op);
			const b = appendOp(fast, op, fastOutcome);
			expect(b.ok, `op ${op.op}`).toBe(a.ok);
			if (!a.ok || !b.ok) throw new Error('fixture script must be valid');
			full = a.state;
			fast = b.state;
			fastOutcome = b.outcome;
			expect(fast.cursor).toBe(full.cursor);
			expect(fast.activeSlotId).toBe(full.activeSlotId);
			expect(channelDump(b.outcome)).toEqual(channelDump(a.outcome)); // channel-for-channel
		}
	});

	it('appendOp with a prev outcome reports the same error and leaves the state untouched', () => {
		const s0 = base(6);
		const split = expectOk(appendOp(s0, { op: 'splitAt', slot: 0, index: 2 }));
		const outcome = replay(split);
		const consumed = expectOk(appendOp(split, { op: 'append', target: 0, source: 1, timeMode: 'keep' }));
		const afterAppend = replay(consumed);

		const bad: [EditOp, string][] = [
			[crop(0, 0, 99), 'rangeOutOfBounds'],
			[crop(0, 2, 1), 'rangeInverted'],
			[crop(0, 0, 0), 'resultTooSmall'],
			[crop(7, 0, 1), 'slotNotFound'],
			[{ op: 'append', target: 0, source: 0, timeMode: 'keep' }, 'selfAppend'],
			[{ op: 'deleteRange', slot: 1, from: 0, to: 1, timeMode: 'close' }, 'gapSecondsMissing'],
			[load(101), 'duplicateLoad'], // the `loaded` set is derived from the prefix ops, not from the live slots
			[load(999), 'sourceMissing'],
		];
		for (const [op, code] of bad) {
			const fast = appendOp(split, op, outcome);
			const slow = appendOp(split, op);
			expect(fast.ok, `${code}`).toBe(false);
			if (fast.ok || slow.ok) throw new Error('expected both to fail');
			expect(fast.error).toEqual(slow.error);
			expect(fast.error.code).toBe(code);
		}

		// A consumed slot id must still read as slotConsumed (not slotNotFound) — the resumed nextId proves it.
		const consumedRef = appendOp(consumed, crop(1, 0, 1), afterAppend);
		expect(consumedRef.ok).toBe(false);
		if (!consumedRef.ok) expect(consumedRef.error.code).toBe('slotConsumed');
		expect(split.ops).toHaveLength(2); // untouched throughout
	});

	it('setActiveSlot reuses a provided outcome (no re-replay)', () => {
		const s0 = base(6);
		const split = expectOk(appendOp(s0, { op: 'splitAt', slot: 0, index: 2 }));
		const outcome = replay(split);
		const switched = setActiveSlot(split, 1, outcome);
		expect(switched).not.toBeNull();
		expect(switched!.outcome).toBe(outcome); // same reference — reused, not recomputed
	});
});

function expectOk(result: ReturnType<typeof appendOp>): EditorState {
	expect(result.ok).toBe(true);
	if (!result.ok) throw new Error(result.error.code);
	return result.state;
}

/** Every slot's ids, origin, count and raw channel values — the byte-level comparison surface for PR-03. */
function channelDump(outcome: ReplayOutcome): unknown {
	if (!outcome.ok) return outcome;
	return outcome.slots.map((s) => ({
		slotId: s.slotId,
		origin: s.origin,
		count: s.track.count,
		channels: Object.fromEntries(
			Object.entries(s.track.channels).map(([name, arr]) => [name, Array.from(arr as Float64Array)]),
		),
	}));
}
