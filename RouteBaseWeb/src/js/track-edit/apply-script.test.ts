// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { applyScript, type ChannelName, type SlotTrack } from './apply-script';
import type { EditOp, EditScript } from './edit-ops';

// Twin unit pins (A1 §5.3 #3-11) — mirror the C# EditReplayEngineTests error paths that are TS-reachable.

function toF64(v: (number | null)[]): Float64Array {
	const a = new Float64Array(v.length);
	for (let i = 0; i < v.length; i++) a[i] = v[i] === null ? NaN : (v[i] as number);
	return a;
}

function track(channels: Partial<Record<ChannelName, (number | null)[]>>): SlotTrack {
	const out: Partial<Record<ChannelName, Float64Array>> = {};
	let n = 0;
	for (const [k, v] of Object.entries(channels)) {
		n = v!.length;
		out[k as ChannelName] = toF64(v!);
	}
	return { count: n, channels: out };
}

function line(n: number): SlotTrack {
	const time: number[] = [];
	const lat: number[] = [];
	const lng: number[] = [];
	for (let i = 0; i < n; i++) {
		time.push(1000 + i * 10);
		lat.push(47 + i);
		lng.push(8 + i);
	}
	return track({ time, lat, lng });
}

function run(sources: Map<number, SlotTrack>, ...ops: EditOp[]) {
	return applyScript(sources, { schemaVersion: 1, ops });
}

function sources(...entries: [number, SlotTrack][]): Map<number, SlotTrack> {
	return new Map(entries);
}

describe('apply-script (C# replay twin)', () => {
	it('slotNotFound on a never-created slot', () => {
		const r = run(sources([101, line(4)]), { op: 'loadActivity', activityId: 101 }, { op: 'cropTo', slot: 5, from: 0, to: 1 });
		expect(r).toEqual({ ok: false, opIndex: 1, code: 'slotNotFound' });
	});

	it('slotConsumed after a slot was appended as source', () => {
		const r = run(
			sources([201, line(3)], [202, line(2)]),
			{ op: 'loadActivity', activityId: 201 },
			{ op: 'loadActivity', activityId: 202 },
			{ op: 'append', target: 0, source: 1, timeMode: 'keep' },
			{ op: 'cropTo', slot: 1, from: 0, to: 1 },
		);
		expect(r).toEqual({ ok: false, opIndex: 3, code: 'slotConsumed' });
	});

	it('selfAppend when target === source', () => {
		const r = run(sources([101, line(4)]), { op: 'loadActivity', activityId: 101 }, { op: 'append', target: 0, source: 0, timeMode: 'keep' });
		expect(r).toEqual({ ok: false, opIndex: 1, code: 'selfAppend' });
	});

	it('resultTooSmall on whole-track delete', () => {
		const r = run(sources([101, line(3)]), { op: 'loadActivity', activityId: 101 }, { op: 'deleteRange', slot: 0, from: 0, to: 2, timeMode: 'keep' });
		expect(r).toEqual({ ok: false, opIndex: 1, code: 'resultTooSmall' });
	});

	it('gapSecondsMissing when close lacks a gap', () => {
		const r = run(sources([101, line(4)]), { op: 'loadActivity', activityId: 101 }, { op: 'deleteRange', slot: 0, from: 1, to: 2, timeMode: 'close' });
		expect(r).toEqual({ ok: false, opIndex: 1, code: 'gapSecondsMissing' });
	});

	it('timeAnchorMissing when the junction anchor is NaN', () => {
		const t = track({ time: [null, 1010, 1020, 1030], lat: [47, 48, 49, 50], lng: [8, 9, 10, 11] });
		const r = run(sources([101, t]), { op: 'loadActivity', activityId: 101 }, { op: 'deleteRange', slot: 0, from: 1, to: 2, timeMode: 'close', gapSeconds: 5 });
		expect(r).toEqual({ ok: false, opIndex: 1, code: 'timeAnchorMissing' });
	});

	it('error carries the failing opIndex', () => {
		const r = run(
			sources([101, line(4)]),
			{ op: 'loadActivity', activityId: 101 },
			{ op: 'cropTo', slot: 0, from: 0, to: 1 },
			{ op: 'cropTo', slot: 0, from: 0, to: 9 },
		);
		expect(r).toEqual({ ok: false, opIndex: 2, code: 'rangeOutOfBounds' });
	});

	it('does not mutate the input source arrays', () => {
		const src = line(4);
		const latBefore = Array.from(src.channels.lat as Float64Array);
		run(sources([101, src]), { op: 'loadActivity', activityId: 101 }, { op: 'deleteRange', slot: 0, from: 1, to: 1, timeMode: 'keep' });
		expect(Array.from(src.channels.lat as Float64Array)).toEqual(latBefore);
	});

	// R-09: loadActivity references the source arrays (no clone), so the in-place-mutating paths
	// (subtractPresent/rebaseDistance under close-mode delete + append) must still only touch the fresh
	// slice/without/concat arrays — never the sources. Pins the twin symmetry with C# EditSlotBuffer.
	it('does not mutate sources through in-place ops (close-mode delete + append)', () => {
		const a = track({
			time: [1000, 1010, 1020, 1030, 1040], lat: [47, 48, 49, 50, 51],
			lng: [8, 9, 10, 11, 12], distance: [0, 100, 200, 300, 400],
		});
		const b = track({ time: [1000, 1010, 1020], lat: [40, 41, 42], lng: [1, 2, 3], distance: [0, 50, 100] });
		const aTime = Array.from(a.channels.time as Float64Array);
		const aDist = Array.from(a.channels.distance as Float64Array);
		const bTime = Array.from(b.channels.time as Float64Array);
		const bDist = Array.from(b.channels.distance as Float64Array);

		const r = run(
			sources([101, a], [102, b]),
			{ op: 'loadActivity', activityId: 101 },
			{ op: 'loadActivity', activityId: 102 },
			{ op: 'deleteRange', slot: 0, from: 1, to: 2, timeMode: 'close', gapSeconds: 5 },
			{ op: 'append', target: 0, source: 1, timeMode: 'close', gapSeconds: 3 },
		);

		expect(r.ok).toBe(true);
		expect(Array.from(a.channels.time as Float64Array)).toEqual(aTime);
		expect(Array.from(a.channels.distance as Float64Array)).toEqual(aDist);
		expect(Array.from(b.channels.time as Float64Array)).toEqual(bTime);
		expect(Array.from(b.channels.distance as Float64Array)).toEqual(bDist);
	});

	it('is deterministic across two runs of the same script', () => {
		const ops: EditOp[] = [
			{ op: 'loadActivity', activityId: 101 },
			{ op: 'splitAt', slot: 0, index: 2 },
			{ op: 'cropTo', slot: 1, from: 0, to: 1 },
		];
		const a = applyScript(sources([101, line(5)]), { schemaVersion: 1, ops });
		const b = applyScript(sources([101, line(5)]), { schemaVersion: 1, ops });
		expect(a).toEqual(b);
	});

	it('empty script -> zero slots', () => {
		const r = applyScript(new Map(), { schemaVersion: 1, ops: [] });
		expect(r).toEqual({ ok: true, slots: [] });
	});

	it('unknown op -> unknownOp (the switch default is the gate)', () => {
		const op = { op: 'teleport' } as unknown as EditOp;
		const r = applyScript(new Map(), { schemaVersion: 1, ops: [op] } as EditScript);
		expect(r).toEqual({ ok: false, opIndex: 0, code: 'unknownOp' });
	});
});
