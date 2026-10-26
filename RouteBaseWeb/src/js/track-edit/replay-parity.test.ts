// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from '../core/invariant';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
	applyScript, requantize, CHANNEL_NAMES, CHANNEL_SCALES,
	type ChannelName, type SlotTrack,
} from './apply-script';
import type { EditScript } from './edit-ops';

// The vitest half of the parity proof (invariant 4): same fixtures the C# ReplayParityTests consume.
const fixtureDir = path.resolve(fileURLToPath(import.meta.url), '../../../../../RouteBaseWeb.Tests/TestData/TrackEdit');
const files = fs.readdirSync(fixtureDir).filter((f) => f.endsWith('.json')).sort();

interface FixtureSlot {
	slotId: number;
	origin: { activityId?: number; splitFromSlotId?: number };
	channels: Record<string, (number | null)[]>;
}

interface Fixture {
	name: string;
	sources: Record<string, Record<string, (number | null)[]>>;
	script: EditScript;
	expect?: { slots: FixtureSlot[] };
	expectError?: { opIndex: number; code: string };
}

function loadFixture(file: string): Fixture {
	return JSON.parse(fs.readFileSync(path.join(fixtureDir, file), 'utf8')) as Fixture;
}

function buildSources(fx: Fixture): Map<number, SlotTrack> {
	const map = new Map<number, SlotTrack>();
	for (const [id, ch] of Object.entries(fx.sources)) {
		const n = must(ch.time, 'fixture source has a time channel').length;
		const channels: Partial<Record<ChannelName, Float64Array>> = {};
		for (const name of CHANNEL_NAMES) {
			const vals = ch[name];
			if (!vals) continue;
			const scale = CHANNEL_SCALES[name];
			const arr = new Float64Array(n);
			for (let i = 0; i < n; i++) arr[i] = vals[i] === null ? NaN : (vals[i] as number) * scale;
			channels[name] = arr;
		}
		map.set(Number(id), { count: n, channels });
	}
	return map;
}

function assertChannels(expected: Record<string, (number | null)[]>, track: SlotTrack): void {
	for (const name of CHANNEL_NAMES) {
		const actual = track.channels[name];
		const expVals = expected[name];
		if (expVals) {
			expect(actual, `${name} expected present`).toBeDefined();
			expect(expVals.length).toBe(track.count);
			const scale = CHANNEL_SCALES[name];
			for (let i = 0; i < expVals.length; i++) {
				const q = requantize((actual as Float64Array)[i] ?? NaN, scale);
				if (expVals[i] === null) expect(Number.isNaN(q), `${name}[${i}] expected null`).toBe(true);
				else expect(q, `${name}[${i}]`).toBe(expVals[i]);
			}
		} else {
			expect(actual, `${name} expected absent (whole-NULL)`).toBeUndefined();
		}
	}
}

describe('replay-parity (C# EditReplayEngine twin)', () => {
	it('discovers all fixtures', () => {
		expect(files.length).toBeGreaterThanOrEqual(22);
	});

	for (const file of files) {
		it(file, () => {
			const fx = loadFixture(file);
			const outcome = applyScript(buildSources(fx), fx.script);

			if (fx.expectError) {
				expect(outcome.ok).toBe(false);
				if (!outcome.ok) {
					expect(outcome.opIndex).toBe(fx.expectError.opIndex);
					expect(outcome.code).toBe(fx.expectError.code);
				}
				return;
			}

			expect(outcome.ok).toBe(true);
			if (!outcome.ok) return;
			const exp = fx.expect!.slots;
			expect(outcome.slots.length).toBe(exp.length);
			for (let s = 0; s < exp.length; s++) {
				const es = must(exp[s], 'expected slot (loop-bounded)');
				const as = must(outcome.slots[s], 'actual slot (length asserted equal)');
				expect(as.slotId).toBe(es.slotId);
				expect(as.origin.activityId).toBe(es.origin.activityId);
				expect(as.origin.splitFromSlotId).toBe(es.origin.splitFromSlotId);
				assertChannels(es.channels, as.track);
			}
		});
	}
});
