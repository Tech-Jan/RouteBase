// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from '../core/invariant';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
	applyScript, resumeScript, CHANNEL_NAMES, CHANNEL_SCALES,
	type ChannelName, type ReplayOutcome, type SlotTrack,
} from './apply-script';
import type { EditOp, EditScript } from './edit-ops';

/**
 * PR-03's safety net: `resumeScript` (one op on top of the previous outcome) must be BYTE-IDENTICAL to
 * `applyScript` (full replay from op 0) — at EVERY prefix of EVERY shared parity fixture, covering all five
 * op types, append's slot consumption, split's id minting, and all four error fixtures. A wrong editor loses
 * ride data, so this compares channel-for-channel (exact Float64 bits, NaN included), not just counts.
 *
 * The chain resumes from the PREVIOUS RESUMED outcome (that is how the editor actually uses it), so any drift
 * would compound and be caught, not masked by re-seeding from a fresh full replay each step.
 */
const fixtureDir = path.resolve(fileURLToPath(import.meta.url), '../../../../../RouteBaseWeb.Tests/TestData/TrackEdit');
const files = fs.readdirSync(fixtureDir).filter((f) => f.endsWith('.json')).sort();

interface Fixture {
	name: string;
	sources: Record<string, Record<string, (number | null)[]>>;
	script: EditScript;
}

function buildSources(fx: Fixture): Map<number, SlotTrack> {
	const map = new Map<number, SlotTrack>();
	for (const [id, ch] of Object.entries(fx.sources)) {
		const n = must(ch.time, 'fixture source has a time channel').length;
		const channels: Partial<Record<ChannelName, Float64Array>> = {};
		for (const name of CHANNEL_NAMES) {
			const vals = ch[name];
			if (!vals) continue;
			const arr = new Float64Array(n);
			for (let i = 0; i < n; i++) arr[i] = vals[i] === null ? NaN : (vals[i] as number) * CHANNEL_SCALES[name];
			channels[name] = arr;
		}
		map.set(Number(id), { count: n, channels });
	}
	return map;
}

/** Exact equality — same slots, same origins, same counts, same channel bits (NaN === NaN via Object.is). */
function expectIdentical(actual: ReplayOutcome, expected: ReplayOutcome, where: string): void {
	expect(actual.ok, `${where}: ok`).toBe(expected.ok);
	if (!expected.ok || !actual.ok) {
		if (!expected.ok && !actual.ok) {
			expect(actual.opIndex, `${where}: opIndex`).toBe(expected.opIndex);
			expect(actual.code, `${where}: code`).toBe(expected.code);
		}
		return;
	}
	expect(actual.slots.length, `${where}: slot count`).toBe(expected.slots.length);
	for (let s = 0; s < expected.slots.length; s++) {
		const e = must(expected.slots[s], 'expected slot (loop-bounded)');
		const a = must(actual.slots[s], 'actual slot (length asserted equal)');
		expect(a.slotId, `${where}: slotId[${s}]`).toBe(e.slotId);
		expect(a.origin.activityId, `${where}: origin.activityId[${s}]`).toBe(e.origin.activityId);
		expect(a.origin.splitFromSlotId, `${where}: origin.splitFromSlotId[${s}]`).toBe(e.origin.splitFromSlotId);
		expect(a.track.count, `${where}: count[${s}]`).toBe(e.track.count);
		for (const name of CHANNEL_NAMES) {
			const ec = e.track.channels[name];
			const ac = a.track.channels[name];
			if (!ec) {
				expect(ac, `${where}: slot ${e.slotId} ${name} must stay absent`).toBeUndefined();
				continue;
			}
			expect(ac, `${where}: slot ${e.slotId} ${name} must be present`).toBeDefined();
			const got = ac as Float64Array;
			expect(got.length, `${where}: slot ${e.slotId} ${name} length`).toBe(ec.length);
			for (let i = 0; i < ec.length; i++) {
				expect(
					Object.is(got[i], ec[i]),
					`${where}: slot ${e.slotId} ${name}[${i}] — resumed ${got[i]}, full ${ec[i]}`,
				).toBe(true);
			}
		}
	}
}

/** Deep snapshot of an outcome's channel arrays — proves a resume never writes into the outcome it seeds from. */
function snapshot(outcome: ReplayOutcome): Float64Array[] {
	if (!outcome.ok) return [];
	return outcome.slots.flatMap((s) =>
		CHANNEL_NAMES.map((n) => s.track.channels[n]).filter((a): a is Float64Array => !!a).map((a) => a.slice()));
}

function liveArrays(outcome: ReplayOutcome): Float64Array[] {
	if (!outcome.ok) return [];
	return outcome.slots.flatMap((s) =>
		CHANNEL_NAMES.map((n) => s.track.channels[n]).filter((a): a is Float64Array => !!a));
}

describe('resume-parity (resumeScript ≡ applyScript, PR-03)', () => {
	it('discovers all fixtures', () => {
		expect(files.length).toBeGreaterThanOrEqual(22);
	});

	for (const file of files) {
		it(`${file} — every prefix`, () => {
			const fx = JSON.parse(fs.readFileSync(path.join(fixtureDir, file), 'utf8')) as Fixture;
			const sources = buildSources(fx);
			const ops = fx.script.ops;

			let chained: Extract<ReplayOutcome, { ok: true }> = { ok: true, slots: [] }; // empty seed = zero-op replay
			for (let i = 0; i < ops.length; i++) {
				const prefix: EditOp[] = ops.slice(0, i);
				const full = applyScript(sources, { schemaVersion: 1, ops: ops.slice(0, i + 1) });

				const before = snapshot(chained);
				const live = liveArrays(chained);
				const resumed = resumeScript(chained, prefix, sources, must(ops[i], 'op i (loop-bounded)'));

				expectIdentical(resumed, full, `${file} op ${i}`);

				// The seeding outcome is still what the caller is rendering — it must be untouched.
				live.forEach((arr, k) => {
					for (let j = 0; j < arr.length; j++) {
						expect(Object.is(arr[j], must(before[k], 'snapshot channel k')[j]), `${file} op ${i}: prev channel mutated at [${j}]`).toBe(true);
					}
				});

				if (!full.ok || !resumed.ok) return; // fixture's expected error — the script stops here, as the engine does
				chained = resumed;
			}
		});
	}
});
