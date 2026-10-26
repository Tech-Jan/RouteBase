// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { createCadenceTracker, cadenceBadge } from './live-cadence-state';

describe('createCadenceTracker', () => {
	it('is "connecting" before any update (recording, no points yet)', () => {
		const t = createCadenceTracker();
		expect(t.state('recording', 1000)).toBe('connecting');
	});

	it('a single update is "warming", never "live" — one point cannot prove the cadence', () => {
		// THE property this whole module exists for: tuning in just as one idle upload lands must not read
		// as live, because you cannot tell that from a genuinely fast stream off a single arrival.
		const t = createCadenceTracker();
		t.onUpdate(1000);
		expect(t.state('recording', 1000)).toBe('warming');
		expect(t.state('recording', 1200)).toBe('warming'); // still, moments later
	});

	it('two updates close together prove the fast cadence → "live"', () => {
		const t = createCadenceTracker({ fastMs: 15_000 });
		t.onUpdate(1000);
		t.onUpdate(6000); // 5 s apart
		expect(t.state('recording', 6000)).toBe('live');
	});

	it('two updates far apart stay "warming" (idle cadence, not sped up yet)', () => {
		const t = createCadenceTracker({ fastMs: 15_000 });
		t.onUpdate(1000);
		t.onUpdate(61_000); // 60 s apart — idle upload rhythm
		expect(t.state('recording', 61_000)).toBe('warming');
	});

	it('"warming" never becomes "waiting" on a long gap — only a confirmed-live stream can stall', () => {
		const t = createCadenceTracker({ fastMs: 15_000, staleMs: 30_000 });
		t.onUpdate(1000); // one sample, cadence unknown
		expect(t.state('recording', 1_000_000)).toBe('warming'); // huge gap, still just warming
	});

	it('a confirmed-live stream that goes quiet past staleMs → "waiting", then "live" again on the next point', () => {
		const t = createCadenceTracker({ fastMs: 15_000, staleMs: 30_000 });
		t.onUpdate(1000);
		t.onUpdate(6000); // → live
		expect(t.state('recording', 6000)).toBe('live');
		expect(t.state('recording', 40_000)).toBe('waiting'); // 34 s since the last update
		t.onUpdate(41_000);
		expect(t.state('recording', 41_000)).toBe('live'); // fresh again; fast-confirmed is sticky
	});

	it('reports terminal + paused states from the status, ignoring update history', () => {
		const t = createCadenceTracker();
		t.onUpdate(1000);
		t.onUpdate(2000); // would otherwise be "live"
		expect(t.state('paused', 2000)).toBe('paused');
		for (const s of ['stopped', 'finalized', 'abandoned', 'finalize_failed'])
			expect(t.state(s, 2000)).toBe('ended');
	});
});

describe('cadenceBadge', () => {
	it('only "live" earns the pulsing dot', () => {
		expect(cadenceBadge('live').live).toBe(true);
		for (const s of ['connecting', 'warming', 'waiting', 'paused', 'ended'] as const)
			expect(cadenceBadge(s).live).toBe(false);
	});

	it('maps connecting + warming to the shared "warming" class so they style identically', () => {
		expect(cadenceBadge('connecting').cls).toBe('warming');
		expect(cadenceBadge('warming').cls).toBe('warming');
	});

	it('labels both pre-live states "CONNECTING" (no jargon like "WARMING UP")', () => {
		expect(cadenceBadge('connecting').text).toBe('CONNECTING');
		expect(cadenceBadge('warming').text).toBe('CONNECTING');
	});

	it('flags only the pre-live ramp as ramping — drives the visible helper line', () => {
		expect(cadenceBadge('connecting').ramping).toBe(true);
		expect(cadenceBadge('warming').ramping).toBe(true);
		for (const s of ['live', 'waiting', 'paused', 'ended'] as const)
			expect(cadenceBadge(s).ramping).toBe(false);
	});

	it('warming carries an explanatory tooltip; live does not', () => {
		expect(cadenceBadge('warming').title).not.toBe('');
		expect(cadenceBadge('live').title).toBe('');
	});
});
