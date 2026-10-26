/**
 * apply-script.ts — pure preview twin of the C# EditReplayEngine (Services/TrackEdit/EditReplayEngine.cs).
 *
 * Same validation order, same error codes, same op switch, same slice/concat/shift mechanics — the two
 * switches are the parity surface proven by the shared JSON fixtures (invariant 4). Works on Float64Array
 * channels in DEQUANTIZED value space: time is absolute epoch seconds (no QBias — the server folds bias,
 * the preview doesn't need to), NaN = a missing sample, an absent key = a whole-NULL channel. No per-point
 * objects. This preview never writes data (invariant 1); the server replay does.
 */

import type { EditOp, EditScript, TimeMode } from './edit-ops';
import { must } from '../core/invariant';
import { TRACK_EDIT_LIMITS, trackEditErrors, type TrackEditErrorCode } from './track-edit-errors';

// Codec channel order (= presence-bit order) and scales — mirror of TrackBlobCodec.
export const CHANNEL_NAMES = [
	'time', 'lat', 'lng', 'altitude', 'distance', 'speed',
	'heartRate', 'gradeSmooth', 'cadence', 'power', 'pressure',
] as const;
export type ChannelName = (typeof CHANNEL_NAMES)[number];

export const CHANNEL_SCALES: Record<ChannelName, number> = {
	time: 1, lat: 1e-6, lng: 1e-6, altitude: 0.1, distance: 0.1, speed: 0.01,
	heartRate: 1, gradeSmooth: 0.1, cadence: 1, power: 1, pressure: 0.01,
};

export type ChannelMap = Partial<Record<ChannelName, Float64Array>>;

export interface SlotTrack {
	count: number;
	channels: ChannelMap;
}

export interface SlotOrigin {
	activityId?: number;
	splitFromSlotId?: number;
}

export interface ReplaySlot {
	slotId: number;
	origin: SlotOrigin;
	track: SlotTrack;
}

export type ReplayOutcome =
	| { ok: true; slots: ReplaySlot[] }
	| { ok: false; opIndex: number; code: string };

interface WorkingSlot {
	count: number;
	channels: ChannelMap;
	origin: SlotOrigin;
}

/** Round away-from-zero to match C# MidpointRounding.AwayFromZero (for re-quantizing preview output). */
export function roundAwayFromZero(x: number): number {
	return Math.sign(x) * Math.round(Math.abs(x));
}

/** Re-quantize a dequantized value onto the codec grid; NaN stays NaN. Parity comparison happens here. */
export function requantize(value: number, scale: number): number {
	return Number.isNaN(value) ? NaN : roundAwayFromZero(value / scale);
}

/** The replay's whole mutable state. Extracted so ONE op can be applied on top of a previous outcome
 * (`resumeScript`) with the exact same code path a full replay uses — same handlers, same order, same copies. */
interface ReplayState {
	live: Map<number, WorkingSlot>;
	loaded: Set<number>;
	nextId: number;
}

/** Apply one op to `st`, in place. Returns an error code (st is then abandoned by the caller) or null.
 * NOTE every handler copies-on-write: it never mutates a channel array it did not allocate, which is what
 * makes seeding from a previous outcome safe (that outcome's arrays stay intact and are shared, not cloned). */
function applyOne(
	st: ReplayState, sources: Map<number, SlotTrack>, next: EditOp,
): TrackEditErrorCode | null {
	const { live, loaded } = st;

	const getLive = (slotId: number): WorkingSlot | TrackEditErrorCode => {
		const slot = live.get(slotId);
		if (slot) return slot;
		return slotId >= 0 && slotId < st.nextId ? trackEditErrors.slotConsumed : trackEditErrors.slotNotFound;
	};

	const applyLoad = (op: Extract<EditOp, { op: 'loadActivity' }>): TrackEditErrorCode | null => {
		const source = sources.get(op.activityId);
		if (!source) return trackEditErrors.sourceMissing;
		if (loaded.has(op.activityId)) return trackEditErrors.duplicateLoad;
		if (live.size >= TRACK_EDIT_LIMITS.maxSlots) return trackEditErrors.slotCapExceeded;
		loaded.add(op.activityId);
		live.set(st.nextId++, {
			count: source.count,
			// Reference the source arrays — never clone. Every mutating path (subtractPresent/rebaseDistance)
			// runs only on fresh arrays from slice/without/concat, so this replay never mutates an array it did
			// not allocate (mirrors the C# EditSlotBuffer.FromDecodedTrack, which references RawQ for the same
			// reason). Pinned by the no-mutate test.
			channels: source.channels,
			origin: { activityId: op.activityId },
		});
		return null;
	};

	const applyCrop = (op: Extract<EditOp, { op: 'cropTo' }>): TrackEditErrorCode | null => {
		const slot = getLive(op.slot);
		if (typeof slot === 'string') return slot;
		const range = validateRange(op.from, op.to, slot.count);
		if (range) return range;
		if (op.to - op.from + 1 < TRACK_EDIT_LIMITS.minSlotPoints) return trackEditErrors.resultTooSmall;

		const count = op.to - op.from + 1;
		const channels = sliceChannels(slot.channels, op.from, op.to);
		if (op.from > 0) rebaseDistance(channels, 0, count, true); // new-head → 0
		live.set(op.slot, { count, channels, origin: slot.origin });
		return null;
	};

	const applyDelete = (op: Extract<EditOp, { op: 'deleteRange' }>): TrackEditErrorCode | null => {
		const slot = getLive(op.slot);
		if (typeof slot === 'string') return slot;
		const range = validateRange(op.from, op.to, slot.count);
		if (range) return range;
		if (slot.count - (op.to - op.from + 1) < TRACK_EDIT_LIMITS.minSlotPoints) return trackEditErrors.resultTooSmall;
		const gapError = validateTimeMode(op.timeMode, op.gapSeconds);
		if (gapError) return gapError;

		const tailExists = op.to < slot.count - 1;
		const newCount = slot.count - (op.to - op.from + 1);
		const channels = withoutRangeChannels(slot.channels, op.from, op.to, slot.count);
		const tailStart = op.from; // tail run occupies [from..newCount) in the result

		if (op.timeMode !== 'keep' && tailExists) {
			const t = slot.channels.time;
			const anchorIdx = op.from > 0 ? op.from - 1 : 0;
			// ?? NaN: an out-of-bounds read is undefined and used to SLIP PAST the isNaN anchor guard
			// below (shift went NaN and corrupted the tail); NaN routes it into timeAnchorMissing.
			const anchor = t ? t[anchorIdx] ?? NaN : NaN;
			const next = t ? t[op.to + 1] ?? NaN : NaN;
			if (Number.isNaN(anchor) || Number.isNaN(next)) return trackEditErrors.timeAnchorMissing;
			const shift = next - anchor - (op.gapSeconds as number);
			subtractPresent(channels.time, tailStart, newCount, shift);
		}
		if (tailExists) rebaseDistance(channels, tailStart, newCount, op.from === 0);
		live.set(op.slot, { count: newCount, channels, origin: slot.origin });
		return null;
	};

	const applySplit = (op: Extract<EditOp, { op: 'splitAt' }>): TrackEditErrorCode | null => {
		const slot = getLive(op.slot);
		if (typeof slot === 'string') return slot;
		if (op.index < 1 || op.index > slot.count - 2) return trackEditErrors.splitIndexOutOfBounds;
		if (live.size >= TRACK_EDIT_LIMITS.maxSlots) return trackEditErrors.slotCapExceeded; // +1 net slot

		const headCount = op.index + 1; // shared boundary point
		const tailCount = slot.count - op.index;
		const head = sliceChannels(slot.channels, 0, op.index);
		const tail = sliceChannels(slot.channels, op.index, slot.count - 1);
		rebaseDistance(tail, 0, tailCount, true); // tail → 0; head untouched
		live.set(op.slot, { count: headCount, channels: head, origin: slot.origin });
		live.set(st.nextId++, { count: tailCount, channels: tail, origin: { splitFromSlotId: op.slot } });
		return null;
	};

	const applyAppend = (op: Extract<EditOp, { op: 'append' }>): TrackEditErrorCode | null => {
		const target = getLive(op.target);
		if (typeof target === 'string') return target;
		const source = getLive(op.source);
		if (typeof source === 'string') return source;
		if (op.target === op.source) return trackEditErrors.selfAppend;
		const gapError = validateTimeMode(op.timeMode, op.gapSeconds);
		if (gapError) return gapError;

		const total = target.count + source.count;
		const srcStart = target.count;
		const channels = concatChannels(target.channels, source.channels, target.count, source.count);

		if (op.timeMode !== 'keep') {
			const anchor = lastPresent(target.channels.time, 0, target.count);
			const next = firstPresent(source.channels.time, 0, source.count);
			if (anchor === null || next === null) return trackEditErrors.timeAnchorMissing;
			const shift = next - anchor - (op.gapSeconds as number);
			subtractPresent(channels.time, srcStart, total, shift);
		}
		rebaseDistance(channels, srcStart, total, false); // junction against target's last present
		live.set(op.target, { count: total, channels, origin: target.origin });
		live.delete(op.source); // id stays < nextId ⇒ future ops see slotConsumed
		return null;
	};

	switch (next.op) {
		case 'loadActivity': return applyLoad(next);
		case 'cropTo': return applyCrop(next);
		case 'deleteRange': return applyDelete(next);
		case 'splitAt': return applySplit(next);
		case 'append': return applyAppend(next);
		default: return trackEditErrors.unknownOp; // TS has no serializer gate — the switch default IS it
	}
}

function slotsOf(st: ReplayState): ReplaySlot[] {
	return [...st.live.keys()].sort((a, b) => a - b).map((slotId) => {
		const s = st.live.get(slotId) as WorkingSlot;
		return { slotId, origin: s.origin, track: { count: s.count, channels: s.channels } };
	});
}

/** Full replay from op 0 — the authority the fixtures pin (invariant 4). Unchanged semantics. */
export function applyScript(sources: Map<number, SlotTrack>, script: EditScript): ReplayOutcome {
	if (script.schemaVersion !== 1) return err(-1, trackEditErrors.badSchemaVersion);
	if (script.ops.length > TRACK_EDIT_LIMITS.maxOps) return err(-1, trackEditErrors.scriptTooLong);

	const st: ReplayState = { live: new Map(), loaded: new Set(), nextId: 0 };
	for (let i = 0; i < script.ops.length; i++) {
		const code = applyOne(st, sources, must(script.ops[i], 'i < ops.length (loop-bounded)'));
		if (code) return err(i, code);
	}
	return { ok: true, slots: slotsOf(st) };
}

/**
 * Apply ONE op on top of the outcome `prevOps` already produced — byte-identical to
 * `applyScript(sources, [...prevOps, op])` but O(1) ops instead of O(K) (PR-03: a full replay copies all 11
 * channels per op, so edit #100 on a 44k-point track cost 48.6 ms).
 *
 * The seed is exact: `live` comes straight from `prev.slots`, and the two scalars a slot list cannot carry —
 * `nextId` and the `loaded` activity-id set — are derived from `prevOps`, which is sound because every op in
 * an accepted prefix succeeded: `nextId` = #loadActivity + #splitAt (the only two id-minting ops), and a
 * successful load is exactly what marks its activity loaded. Deriving `nextId` from the live slot ids instead
 * would be WRONG — an append-consumed or never-reused id must still read as `slotConsumed`, not `slotNotFound`.
 */
export function resumeScript(
	prev: { slots: ReplaySlot[] },
	prevOps: readonly EditOp[],
	sources: Map<number, SlotTrack>,
	op: EditOp,
): ReplayOutcome {
	if (prevOps.length + 1 > TRACK_EDIT_LIMITS.maxOps) return err(-1, trackEditErrors.scriptTooLong);

	const st = seedState(prev, prevOps);
	const code = applyOne(st, sources, op);
	if (code) return err(prevOps.length, code); // the new op's index in the full script
	return { ok: true, slots: slotsOf(st) };
}

function seedState(prev: { slots: ReplaySlot[] }, prevOps: readonly EditOp[]): ReplayState {
	const live = new Map<number, WorkingSlot>();
	for (const s of prev.slots) {
		live.set(s.slotId, { count: s.track.count, channels: s.track.channels, origin: s.origin });
	}
	const loaded = new Set<number>();
	let nextId = 0;
	for (const op of prevOps) {
		if (op.op === 'loadActivity') { loaded.add(op.activityId); nextId++; }
		else if (op.op === 'splitAt') nextId++;
	}
	return { live, loaded, nextId };
}

// ---- channel primitives (typed-array copies / single passes; no per-point objects) ----

function sliceChannels(ch: ChannelMap, from: number, to: number): ChannelMap {
	const out: ChannelMap = {};
	for (const name of CHANNEL_NAMES) { const a = ch[name]; if (a) out[name] = a.slice(from, to + 1); }
	return out;
}

function withoutRangeChannels(ch: ChannelMap, from: number, to: number, count: number): ChannelMap {
	const tailLen = count - to - 1;
	const newCount = from + tailLen;
	const out: ChannelMap = {};
	for (const name of CHANNEL_NAMES) {
		const a = ch[name];
		if (!a) continue;
		const dst = new Float64Array(newCount);
		dst.set(a.subarray(0, from), 0);
		dst.set(a.subarray(to + 1), from);
		out[name] = dst;
	}
	return out;
}

function concatChannels(a: ChannelMap, b: ChannelMap, aCount: number, bCount: number): ChannelMap {
	const total = aCount + bCount;
	const out: ChannelMap = {};
	for (const name of CHANNEL_NAMES) {
		const ca = a[name];
		const cb = b[name];
		if (!ca && !cb) continue;
		const dst = new Float64Array(total);
		if (ca) dst.set(ca, 0); else dst.fill(NaN, 0, aCount);
		if (cb) dst.set(cb, aCount); else dst.fill(NaN, aCount, total);
		out[name] = dst;
	}
	return out;
}

function subtractPresent(arr: Float64Array | undefined, start: number, end: number, amount: number): void {
	if (!arr || amount === 0) return;
	for (let j = start; j < end; j++) {
		const v = arr[j] ?? NaN;
		if (!Number.isNaN(v)) arr[j] = v - amount;
	}
}

/** First present (non-NaN) sample in [start, end), or null. Shared low-level scan (R-11: append-dialog and
 * time-gap re-implemented it whole-array; `firstPresent(x)` ≡ their firstFinite). */
export function firstPresent(arr: Float64Array | undefined, start = 0, end = arr?.length ?? 0): number | null {
	if (arr) for (let j = start; j < end; j++) {
		const v = arr[j] ?? NaN;
		if (!Number.isNaN(v)) return v;
	}
	return null;
}

/** Last present (non-NaN) sample in [start, end), or null. */
export function lastPresent(arr: Float64Array | undefined, start = 0, end = arr?.length ?? 0): number | null {
	if (arr) for (let j = end - 1; j >= start; j--) {
		const v = arr[j] ?? NaN;
		if (!Number.isNaN(v)) return v;
	}
	return null;
}

// New head: rebase the run so its first present distance = 0. Junction: shift so the delta across the
// junction is 0 (against the preceding run's last present). Missing side ⇒ no rebase (§2.3).
function rebaseDistance(ch: ChannelMap, start: number, end: number, newHead: boolean): void {
	const d = ch.distance;
	if (!d) return;
	if (newHead) {
		const first = firstPresent(d, start, end);
		if (first !== null) subtractPresent(d, start, end, first);
		return;
	}
	const firstNext = firstPresent(d, start, end);
	if (firstNext === null) return;
	const lastPrev = lastPresent(d, 0, start);
	if (lastPrev === null) return;
	subtractPresent(d, start, end, firstNext - lastPrev);
}

function validateRange(from: number, to: number, count: number): TrackEditErrorCode | null {
	if (from < 0 || from >= count || to < 0 || to >= count) return trackEditErrors.rangeOutOfBounds;
	if (from > to) return trackEditErrors.rangeInverted;
	return null;
}

function validateTimeMode(mode: TimeMode, gapSeconds: number | null | undefined): TrackEditErrorCode | null {
	const m = mode as string;
	if (m !== 'keep' && m !== 'close' && m !== 'synthesize') return trackEditErrors.timeModeInvalid;
	if (m === 'keep') return null;
	if (gapSeconds === undefined || gapSeconds === null) return trackEditErrors.gapSecondsMissing;
	if (gapSeconds < 0 || gapSeconds > TRACK_EDIT_LIMITS.maxGapSeconds) return trackEditErrors.gapSecondsOutOfRange;
	return null;
}

function err(opIndex: number, code: string): ReplayOutcome {
	return { ok: false, opIndex, code };
}
