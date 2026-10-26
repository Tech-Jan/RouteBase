/**
 * edit-session-state.ts — pure preview state (A3 §2.1). No DOM, no fetch. The controller owns one
 * `EditorState` value and swaps it for the value these functions return; every mutation is proven by the
 * `applyScript` twin BEFORE it is accepted (invariant 4) — a server rejection therefore never means "bad op",
 * it means session-level divergence (multi-tab / stale), handled by resync in the write queue.
 *
 * Ordinals are always current-state (invariant 2): ops reference the live replay slots, never a back-translated
 * original index. `cursor` is the frozen A1 contract ([0..ops.length]); `seedCount` is a UI-only undo floor
 * (D-U4) so the initial `loadActivity` seed ops can't be undone into an empty session.
 */

import type { EditOp, EditScript, EditSessionState } from './edit-ops';
import { must } from '../core/invariant';
import { applyScript, resumeScript, type ReplayOutcome, type ReplaySlot, type SlotTrack } from './apply-script';
import { TRACK_EDIT_LIMITS, trackEditErrors } from './track-edit-errors';

export interface EditorState {
	sessionId: string;
	/** activityId → dequantized Float64Array channels (from the sources endpoint). */
	sources: Map<number, SlotTrack>;
	/** Full script incl. the redo tail (ops[cursor..]). */
	ops: EditOp[];
	cursor: number;
	/** Ops present at session create — the undo floor (D-U4). */
	seedCount: number;
	activeSlotId: number;
}

export interface ReplayError {
	opIndex: number;
	code: string;
}

export type AppendResult =
	| { ok: true; state: EditorState; outcome: ReplayOutcome }
	| { ok: false; error: ReplayError };

/** A new state paired with the replay outcome that produced it — returned by the cursor/slot/rebuild helpers
 * so the controller never re-replays what they already computed (R-08: undo/redo/switch/enter ran replay twice). */
export interface StateWithOutcome {
	state: EditorState;
	outcome: ReplayOutcome;
}

/** Leading run of `loadActivity` ops = the session seed (Flow A pre-seeds one per activity). UI floor only. */
export function countSeedOps(ops: readonly EditOp[]): number {
	let n = 0;
	while (n < ops.length && ops[n]?.op === 'loadActivity') n++;
	return n;
}

/**
 * Twin-validate `op` on top of the effective prefix, then accept it. On success the redo tail is truncated
 * (frozen §2.7/E26) and the cursor advances; on failure the state is untouched and the error surfaces.
 *
 * `prev` MUST be the outcome of `state`'s effective prefix (the controller holds exactly that, in lockstep
 * with `state`). Given it, the op is applied by `resumeScript` — byte-identical to the full replay, but O(1)
 * ops instead of O(K) (PR-03: edit #100 on a 44k-point track cost 48.6 ms of channel copying). Omit `prev`
 * (or pass a failed outcome) and the full replay runs, unchanged.
 */
export function appendOp(state: EditorState, op: EditOp, prev?: ReplayOutcome): AppendResult {
	if (state.cursor >= TRACK_EDIT_LIMITS.maxOps) {
		return { ok: false, error: { opIndex: -1, code: trackEditErrors.scriptTooLong } };
	}
	const prefix = state.ops.slice(0, state.cursor);
	const outcome = prev?.ok
		? resumeScript(prev, prefix, state.sources, op)
		: applyScript(state.sources, script([...prefix, op]));
	if (!outcome.ok) return { ok: false, error: { opIndex: outcome.opIndex, code: outcome.code } };
	const ops = [...prefix, op];

	const activeSlotId = nextActiveSlot(op, state.activeSlotId, outcome);
	return { ok: true, state: { ...state, ops, cursor: ops.length, activeSlotId }, outcome };
}

/** Move the cursor back one op, clamped to the seed floor (D-U4). Null when already at the floor (no replay). */
export function undo(state: EditorState): StateWithOutcome | null {
	const cursor = Math.max(state.seedCount, state.cursor - 1);
	return cursor === state.cursor ? null : withCursor(state, cursor);
}

/** Move the cursor forward one op, clamped to the script length. Null when already at the ceiling (no replay). */
export function redo(state: EditorState): StateWithOutcome | null {
	const cursor = Math.min(state.ops.length, state.cursor + 1);
	return cursor === state.cursor ? null : withCursor(state, cursor);
}

/** Replay the effective prefix (ops[0..cursor)) — always succeeds for a twin-validated history. */
export function replay(state: EditorState): ReplayOutcome {
	return applyScript(state.sources, script(state.ops.slice(0, state.cursor)));
}

/** The currently-edited slot from a replay outcome; falls back to the lowest live slot when the active id
 * is no longer present (resume default, or an append that consumed it). */
export function activeSlot(outcome: ReplayOutcome, state: EditorState): ReplaySlot | null {
	if (!outcome.ok || outcome.slots.length === 0) return null;
	return outcome.slots.find((s) => s.slotId === state.activeSlotId) ?? must(outcome.slots[0], 'slots non-empty (guarded above)');
}

/** Switch the active (edited) slot — pure view-state, NEVER an op (A4 §2.4): no script/cursor change, no
 * write-through, no undo entry. Null when `slotId` isn't live in the current replay (dead id) or is already
 * active. A switch never changes geometry, so the caller may pass the current `outcome` to skip the replay
 * entirely; it is reused for the liveness check and returned. The controller re-renders to move the
 * profile/charts/red-track to the new slot. */
export function setActiveSlot(
	state: EditorState,
	slotId: number,
	outcome?: ReplayOutcome,
): StateWithOutcome | null {
	if (slotId === state.activeSlotId) return null;
	const resolved = outcome ?? replay(state);
	if (!resolved.ok || !resolved.slots.some((s) => s.slotId === slotId)) return null;
	return { state: { ...state, activeSlotId: slotId }, outcome: resolved };
}

export const canUndo = (state: EditorState): boolean => state.cursor > state.seedCount;
export const canRedo = (state: EditorState): boolean => state.cursor < state.ops.length;
/** User edit ops beyond the seed (the "N edits" the banners/toolbar show). */
export const opCount = (state: EditorState): number => state.cursor - state.seedCount;

/**
 * Rebuild an `EditorState` from a fetched session (create or resume). The active slot defaults to the lowest
 * live slot; `seedCount` comes from the caller (create → all ops; resume → leading loadActivity run).
 */
export function fromSession(
	session: EditSessionState,
	sources: Map<number, SlotTrack>,
	seedCount: number,
): StateWithOutcome {
	const ops = session.script.ops.slice();
	const outcome = applyScript(sources, script(ops.slice(0, session.cursor)));
	const activeSlotId = outcome.ok && outcome.slots.length > 0 ? must(outcome.slots[0], 'non-empty (guarded)').slotId : 0;
	return {
		state: { sessionId: session.sessionId, sources, ops, cursor: session.cursor, seedCount, activeSlotId },
		outcome,
	};
}

// ---- internals ----

function script(ops: EditOp[]): EditScript {
	return { schemaVersion: 1, ops };
}

function withCursor(state: EditorState, cursor: number): StateWithOutcome {
	const outcome = applyScript(state.sources, script(state.ops.slice(0, cursor)));
	return { state: { ...state, cursor, activeSlotId: resolveActive(outcome, state.activeSlotId) }, outcome };
}

/** An append whose source is the active slot moves editing to the target; otherwise keep the active id if it
 * survives (split keeps the head id), else fall to the lowest live slot. */
function nextActiveSlot(op: EditOp, active: number, outcome: ReplayOutcome): number {
	if (op.op === 'append' && op.source === active) return op.target;
	return resolveActive(outcome, active);
}

function resolveActive(outcome: ReplayOutcome, active: number): number {
	if (!outcome.ok || outcome.slots.length === 0) return active;
	return outcome.slots.some((s) => s.slotId === active) ? active : must(outcome.slots[0], 'non-empty (guarded above)').slotId;
}
