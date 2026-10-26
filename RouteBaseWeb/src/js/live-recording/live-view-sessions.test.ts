// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { cameraTarget, createSessionStore, pickInitialFollow, RIDER_COLORS, MAX_WATCHABLE, type LiveSessionMeta } from './live-view-sessions';
import type { PointPayload } from './live-recording-shared';

/**
 * LV-01: the dedup key is the dense staging `seq` (it used to key on fake 't1'…'t9' timestamp strings —
 * which is exactly why the suite could not see the bug). The wire no longer carries `ts` at all
 * (dropped 2026-07-22, perf-11), so the old string-compared dedup bug is now structurally impossible;
 * `seq` is the sole ordering key, and these fixtures pin that.
 */
const pt = (lng: number, seq: number): PointPayload => ({ lat: 40, lng, seq });

const meta = (sessionId: string, over: Partial<LiveSessionMeta> = {}): LiveSessionMeta => ({
	sessionId,
	displayName: sessionId,
	isSelf: false,
	activityType: 'Ride',
	startedAt: '2026-07-12T10:00:00Z',
	status: 'recording',
	trackId: 1,
	pointCount: 0,
	...over,
});

describe('session store — point routing (LR-07)', () => {
	it('queues live points until the catch-up settles, then merges without double-counting', () => {
		const store = createSessionStore();
		store.add(meta('s1'));

		// Same points arrive live while the catch-up REST call is still in flight.
		expect(store.receive('s1', [pt(2, 2), pt(3, 3)])).toBe(false); // queued, not painted

		store.settleCatchup('s1', [pt(1, 1), pt(2, 2), pt(3, 3)]);

		const s = store.get('s1')!;
		expect(s.count).toBe(3);
		expect(s.coords).toHaveLength(3);
		expect(s.lastSeq).toBe(3);
	});

	it('appends queued points strictly newer than the catch-up tail', () => {
		const store = createSessionStore();
		store.add(meta('s1'));
		store.receive('s1', [pt(2, 2), pt(9, 5)]); // seq 2 dup, seq 5 genuinely new

		store.settleCatchup('s1', [pt(1, 1), pt(2, 2)]);

		const s = store.get('s1')!;
		expect(s.count).toBe(3);
		expect(s.coords[2]).toEqual([9, 40]);
		expect(s.lastSeq).toBe(5);
	});

	it('never regresses lastSeq', () => {
		const store = createSessionStore();
		store.add(meta('s1'));
		store.receive('s1', [pt(2, 3)]);
		store.receive('s1', [pt(3, 5)]);

		store.settleCatchup('s1', [pt(1, 9)]);

		const s = store.get('s1')!;
		expect(s.lastSeq).toBe(9); // both queued points predate the catch-up tail → skipped
		expect(s.count).toBe(1);
	});

	it('paints live points once the catch-up has settled', () => {
		const store = createSessionStore();
		store.add(meta('s1'));
		store.settleCatchup('s1', [pt(1, 1)]);

		expect(store.receive('s1', [pt(2, 2)])).toBe(true);
		expect(store.get('s1')!.count).toBe(2);
	});

	it('routes each batch to its own session', () => {
		const store = createSessionStore();
		store.add(meta('a'));
		store.add(meta('b'));
		store.settleCatchup('a', []);
		store.settleCatchup('b', []);

		store.receive('a', [pt(1, 1)]);
		store.receive('b', [pt(2, 1), pt(3, 2)]);

		expect(store.get('a')!.count).toBe(1);
		expect(store.get('b')!.count).toBe(2);
	});

	it('ignores points for a session that is not being watched', () => {
		const store = createSessionStore();
		expect(store.receive('unknown', [pt(1, 1)])).toBe(false);
	});

	// LV-01 — the bug the old string-compare dedup caused, and the two encodings it compared.

	it('keeps every distinct-seq point of one truncated second', () => {
		const store = createSessionStore();
		store.add(meta('s1'));

		// Three fixes inside one wall-clock second — staging truncates ts_utc to whole seconds, so the old
		// string-compared timestamp guard silently dropped two of them (from the line AND the counter) on
		// every LiveView load. Deduping on the distinct dense `seq` keeps all three.
		store.settleCatchup('s1', [pt(1, 0), pt(2, 1), pt(3, 2)]);

		const s = store.get('s1')!;
		expect(s.coords).toHaveLength(3);
		expect(s.count).toBe(3);
		expect(s.lastSeq).toBe(2);
	});

	it('does not double-draw a point the catch-up and the live batch both carry', () => {
		const store = createSessionStore();
		store.add(meta('s1'));

		// The old guard compared timestamp strings lexicographically and mis-ordered them; seq-dedup counts
		// the catch-up/live overlap (seq 3, 4) exactly once regardless of arrival order.
		store.receive('s1', [pt(3, 3), pt(4, 4), pt(5, 5), pt(6, 6)]);

		store.settleCatchup('s1', [pt(0, 0), pt(1, 1), pt(2, 2), pt(3, 3), pt(4, 4)]);

		const s = store.get('s1')!;
		expect(s.coords).toHaveLength(7); // seq 0..6, the overlap counted once
		expect(s.lastSeq).toBe(6);
	});

	it('drops reconnect-gap points already held', () => {
		const store = createSessionStore();
		store.add(meta('s1'));
		store.settleCatchup('s1', [pt(1, 1), pt(2, 2)]);

		expect(store.appendAfterReconnect('s1', [pt(2, 2)])).toBe(false); // duplicate
		expect(store.appendAfterReconnect('s1', [pt(3, 3)])).toBe(true);
		expect(store.get('s1')!.count).toBe(3);
	});
});

describe('session store — rider colours', () => {
	it('assigns the palette in fixed order', () => {
		const store = createSessionStore();
		expect(store.add(meta('a'))!.color).toBe(RIDER_COLORS[0]);
		expect(store.add(meta('b'))!.color).toBe(RIDER_COLORS[1]);
	});

	it('never repaints an existing rider when another joins later', () => {
		const store = createSessionStore();
		const a = store.add(meta('a'))!;
		store.add(meta('b'));
		store.add(meta('c')); // late joiner takes the next slot

		expect(store.get('a')!.color).toBe(a.color); // colour follows the entity, not its rank
		expect(store.get('c')!.color).toBe(RIDER_COLORS[2]);
	});

	it('refuses to watch more than the palette allows', () => {
		const store = createSessionStore();
		for (let i = 0; i < MAX_WATCHABLE; i++) expect(store.add(meta(`s${i}`))).not.toBeNull();

		expect(store.add(meta('one-too-many'))).toBeNull();
	});

	it('re-adding a watched session keeps its colour and track', () => {
		const store = createSessionStore();
		const first = store.add(meta('a'))!;
		store.settleCatchup('a', [pt(1, 1)]);

		const again = store.add(meta('a'))!;
		expect(again.color).toBe(first.color);
		expect(again.count).toBe(1);
	});
});

describe('cameraTarget — the camera only centres a rider it can actually see', () => {
	it('refuses a rider who has no fix yet, so the camera stays free instead of pinned to the world view', () => {
		const store = createSessionStore();
		store.add(meta('fresh')); // just hit start — zero points

		expect(cameraTarget('fresh', store)).toBeNull();
	});

	it('engages once that rider has a first point', () => {
		const store = createSessionStore();
		store.add(meta('fresh'));
		store.settleCatchup('fresh', [pt(1, 1)]);

		expect(cameraTarget('fresh', store)).toBe('fresh');
	});

	it('refuses a rider who is hidden from the map', () => {
		const store = createSessionStore();
		store.add(meta('a'));
		store.settleCatchup('a', [pt(1, 1)]);
		store.setVisible('a', false);

		expect(cameraTarget('a', store)).toBeNull();
	});

	it('is null in free-camera mode and for an unknown rider', () => {
		const store = createSessionStore();
		expect(cameraTarget(null, store)).toBeNull();
		expect(cameraTarget('nobody', store)).toBeNull();
	});
});

describe('pickInitialFollow', () => {
	it('follows you when your own session is in the selection', () => {
		const target = pickInitialFollow([
			meta('friend', { startedAt: '2026-07-12T09:00:00Z' }),
			meta('mine', { isSelf: true, startedAt: '2026-07-12T11:00:00Z' }),
		]);
		expect(target).toBe('mine'); // even though the friend started earlier
	});

	it('otherwise follows the earliest-started rider', () => {
		const target = pickInitialFollow([
			meta('late', { startedAt: '2026-07-12T11:00:00Z' }),
			meta('early', { startedAt: '2026-07-12T09:00:00Z' }),
		]);
		expect(target).toBe('early');
	});

	it('returns null when nothing is watched', () => {
		expect(pickInitialFollow([])).toBeNull();
	});
});
