import { describe, it, expect, vi } from 'vitest';
import { must } from '../core/invariant';
import { sanitizePoint, createUploader, type PostResult, type GpsPoint, type PostFn } from './live-recording-uploader';

const NOW = Date.UTC(2026, 6, 10, 12, 0, 0);

function raw(over: Partial<Parameters<typeof sanitizePoint>[0]> = {}) {
	return { lat: 40, lng: -70, altitude: 100, speed: 5, timestamp: NOW, ...over };
}

function makePoints(n: number): GpsPoint[] {
	return Array.from({ length: n }, (_, i) => ({ lat: 40 + i * 1e-5, lng: -70, timestampUtc: new Date(NOW + i).toISOString() }));
}

const noWait = () => Promise.resolve();

describe('sanitizePoint (LR-01)', () => {
	it('drops non-finite and out-of-range coords', () => {
		expect(sanitizePoint(raw({ lat: 91 }), NOW)).toBeNull();
		expect(sanitizePoint(raw({ lng: NaN }), NOW)).toBeNull();
		expect(sanitizePoint(raw({ lat: -95 }), NOW)).toBeNull();
	});

	it('nulls invalid altitude/speed but keeps the point', () => {
		const p = sanitizePoint(raw({ speed: 200, altitude: 99999 }), NOW)!;
		expect(p).not.toBeNull();
		expect(p.speed).toBeUndefined();
		expect(p.altitude).toBeUndefined();
		expect(p.lat).toBe(40);
	});

	it('clamps future timestamps to now', () => {
		const p = sanitizePoint(raw({ timestamp: NOW + 15 * 60_000 }), NOW)!;
		expect(p.timestampUtc).toBe(new Date(NOW).toISOString());
	});
});

describe('createUploader flush chunking + dedup (LR-01/LR-03)', () => {
	it('flush sends at most 500 points and keeps the rest queued', async () => {
		const calls: { seq: number; count: number }[] = [];
		const post: PostFn = (seq, points) => {
			calls.push({ seq, count: points.length });
			return Promise.resolve({ ok: true, status: 200, inserted: points.length, lastSequence: seq });
		};
		const up = createUploader({ post });
		makePoints(700).forEach((p) => up.enqueue(p));
		await up.flush();
		expect(calls).toHaveLength(1);
		expect(must(calls[0], 'one post call').count).toBe(500);
		expect(up.bufferedCount()).toBe(200);
	});

	it('retry after timeout resends the identical frozen batch with the same seq', async () => {
		const bodies: GpsPoint[][] = [];
		let first = true;
		const post: PostFn = (_seq, points) => {
			bodies.push(points);
			if (first) { first = false; return Promise.reject(new Error('net')); }
			return Promise.resolve({ ok: true, status: 200, inserted: points.length, lastSequence: 1 });
		};
		const up = createUploader({ post });
		makePoints(3).forEach((p) => up.enqueue(p));
		await up.flush(); // rejects, keeps frozen
		await up.flush(); // resends identical
		expect(bodies).toHaveLength(2);
		expect(bodies[0]).toEqual(bodies[1]);
	});

	it('points arriving during flight are not folded into the retried batch', async () => {
		const bodies: GpsPoint[][] = [];
		let first = true;
		const post: PostFn = (_seq, points) => {
			bodies.push(points.slice());
			if (first) { first = false; return Promise.reject(new Error('net')); }
			return Promise.resolve({ ok: true, status: 200, inserted: points.length, lastSequence: 5 });
		};
		const up = createUploader({ post });
		makePoints(2).forEach((p) => up.enqueue(p));
		await up.flush(); // fails, freezes 2 points
		up.enqueue(must(makePoints(1)[0], 'one fixture point')); // arrives "during flight"
		await up.flush(); // resends the frozen 2, not 3
		expect(bodies[1]).toHaveLength(2);
		expect(bodies[1]).toEqual(bodies[0]);
	});

	it('inserted:0 resyncs clientSequence to server lastSequence', async () => {
		const seqs: number[] = [];
		const post: PostFn = (seq, points) => {
			seqs.push(seq);
			// first batch dedup-eaten, server is ahead at 41
			if (seqs.length === 1) return Promise.resolve({ ok: true, status: 200, inserted: 0, lastSequence: 41 });
			return Promise.resolve({ ok: true, status: 200, inserted: points.length, lastSequence: seq });
		};
		const status = vi.fn();
		const up = createUploader({ post, onStatus: status, initialSequence: 6 });
		up.enqueue(must(makePoints(1)[0], 'one fixture point'));
		await up.flush(); // seq 7 → inserted:0, lastSequence 41
		up.enqueue(must(makePoints(1)[0], 'one fixture point'));
		await up.flush(); // next seq must be 42
		expect(seqs).toEqual([7, 42]);
		// The conflict must surface with the exact recorder-collision copy, not just "some status".
		expect(status).toHaveBeenCalledWith('Sync conflict — another recorder active?');
	});
});

describe('createUploader failure handling (LR-01)', () => {
	it('three consecutive 400s drop the batch and report', async () => {
		const post: PostFn = () => Promise.resolve({ ok: false, status: 400, errorText: 'implausible timestamp' } as PostResult);
		const status = vi.fn();
		const up = createUploader({ post, onStatus: status });
		makePoints(2).forEach((p) => up.enqueue(p));
		await up.flush();
		await up.flush();
		expect(up.hasUnsent()).toBe(true);
		await up.flush(); // 3rd 400 → drop + report
		expect(up.hasUnsent()).toBe(false);
		expect(status).toHaveBeenCalledWith(expect.stringContaining('dropped'));
	});

	it('404 triggers session-status callback and stops retrying', async () => {
		const post = vi.fn<PostFn>(() => Promise.resolve({ ok: false, status: 404 } as PostResult));
		const inactive = vi.fn();
		const up = createUploader({ post, onSessionInactive: inactive });
		up.enqueue(must(makePoints(1)[0], 'one fixture point'));
		await up.flush();
		expect(inactive).toHaveBeenCalledOnce();
		await up.flush(); // halted → no-op
		expect(post).toHaveBeenCalledOnce();
	});
});

describe('drainForStop (LR-02)', () => {
	it('retries a 429 after the limiter window', async () => {
		let n = 0;
		const post: PostFn = (_seq, points) => {
			n++;
			if (n === 1) return Promise.resolve({ ok: false, status: 429 } as PostResult);
			return Promise.resolve({ ok: true, status: 200, inserted: points.length, lastSequence: 1 });
		};
		const up = createUploader({ post });
		makePoints(3).forEach((p) => up.enqueue(p));
		const ok = await up.drainForStop(5, 1100, noWait);
		expect(ok).toBe(true);
		expect(up.hasUnsent()).toBe(false);
		expect(n).toBe(2);
	});

	it('gives up after maxAttempts and reports leftovers', async () => {
		const post: PostFn = () => Promise.resolve({ ok: false, status: 429 } as PostResult);
		const up = createUploader({ post });
		makePoints(3).forEach((p) => up.enqueue(p));
		const ok = await up.drainForStop(3, 1100, noWait);
		expect(ok).toBe(false);
		expect(up.hasUnsent()).toBe(true);
	});

	it('waits for an in-flight send instead of no-opping', async () => {
		let resolveFirst!: (r: PostResult) => void;
		const post = vi.fn<PostFn>((_seq, points) => {
			if (post.mock.calls.length === 1) return new Promise<PostResult>((r) => { resolveFirst = r; });
			return Promise.resolve({ ok: true, status: 200, inserted: points.length, lastSequence: 2 });
		});
		const up = createUploader({ post });
		makePoints(600).forEach((p) => up.enqueue(p)); // 500 freeze, 100 remain
		const inflight = up.flush(); // starts, stays pending
		const waits: number[] = [];
		const wait = (ms: number) => { waits.push(ms); return Promise.resolve(); };
		const drainP = up.drainForStop(5, 1100, wait);
		// let drain observe sendInFlight and wait at least once
		await Promise.resolve();
		resolveFirst({ ok: true, status: 200, inserted: 500, lastSequence: 1 });
		await inflight;
		const ok = await drainP;
		expect(ok).toBe(true);
		expect(waits.length).toBeGreaterThanOrEqual(1); // it waited rather than no-opping
		expect(up.hasUnsent()).toBe(false);
	});
});

describe('snapshotForBeacon (LR-04)', () => {
	it('returns the frozen batch + its seq when one is in flight', async () => {
		let resolveP!: (r: PostResult) => void;
		const post: PostFn = () => new Promise<PostResult>((r) => { resolveP = r; });
		const up = createUploader({ post, initialSequence: 10 });
		makePoints(3).forEach((p) => up.enqueue(p));
		const p = up.flush();
		const snap = up.snapshotForBeacon();
		expect(snap).not.toBeNull();
		expect(snap!.clientSequence).toBe(11);
		expect(snap!.points).toHaveLength(3);
		resolveP({ ok: true, status: 200, inserted: 3, lastSequence: 11 });
		await p;
	});

	it('caps the beacon batch and uses next seq for buffered-only points', () => {
		const up = createUploader({ post: () => Promise.resolve({ ok: true, status: 200, inserted: 0 }), initialSequence: 4 });
		makePoints(500).forEach((p) => up.enqueue(p));
		const snap = up.snapshotForBeacon(400)!;
		expect(snap.clientSequence).toBe(5);
		expect(snap.points).toHaveLength(400);
	});

	it('returns null when nothing is unsent', () => {
		const up = createUploader({ post: () => Promise.resolve({ ok: true, status: 200, inserted: 0 }) });
		expect(up.snapshotForBeacon()).toBeNull();
	});
});
