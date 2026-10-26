// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { createActiveElapsed } from './live-elapsed';

// FE-08/FE-12: the elapsed clock reflects ACTIVE riding time — it ticks while recording, freezes while
// paused, and holds a final value once ended. Both live views share this accumulator so they never disagree.

describe('createActiveElapsed', () => {
	it('advances while recording', () => {
		const e = createActiveElapsed(0);
		expect(e.sample('recording', 1_000)).toBe(1);
		expect(e.sample('recording', 65_000)).toBe(65);
	});

	it('freezes while paused (does not tick through the pause)', () => {
		const e = createActiveElapsed(0);
		expect(e.sample('recording', 10_000)).toBe(10);
		expect(e.sample('paused', 10_000)).toBe(10);
		// wall clock keeps moving, but the paused clock does not
		expect(e.sample('paused', 20_000)).toBe(10);
		expect(e.sample('paused', 40_000)).toBe(10);
	});

	it('resumes without counting the paused gap (active riding time)', () => {
		const e = createActiveElapsed(0);
		e.sample('recording', 10_000);          // 10 s ridden
		e.sample('paused', 10_000);             // freeze at 10
		e.sample('paused', 30_000);             // 20 s of pause elapses on the wall clock
		expect(e.sample('recording', 30_000)).toBe(10); // resume: no jump for the pause
		expect(e.sample('recording', 35_000)).toBe(15); // +5 s ridden, pause excluded
	});

	it('holds a final duration once ended, ignoring later samples', () => {
		const e = createActiveElapsed(0);
		e.sample('recording', 40_000);
		expect(e.sample('stopped', 40_000)).toBe(40);
		// terminal: a stray later sample (any status/time) cannot move it
		expect(e.sample('recording', 999_999_999)).toBe(40);
		expect(e.sample('paused', 999_999_999)).toBe(40);
	});

	it('treats every ENDED status as terminal', () => {
		for (const ended of ['stopped', 'finalized', 'abandoned', 'finalize_failed']) {
			const e = createActiveElapsed(0);
			e.sample('recording', 12_000);
			expect(e.sample(ended, 12_000)).toBe(12);
			expect(e.sample('recording', 50_000)).toBe(12);
		}
	});

	it('ends from a paused state at the frozen active time (pause excluded)', () => {
		const e = createActiveElapsed(0);
		e.sample('recording', 10_000);
		e.sample('paused', 10_000);
		// stop lands during the pause: final duration is the 10 s ridden, not 25 s wall
		expect(e.sample('finalized', 25_000)).toBe(10);
	});

	it('counts the pre-observation span as active for a late opener (best effort)', () => {
		// A viewer who opens 100 s in cannot know about pauses from before they tuned in.
		const e = createActiveElapsed(0);
		expect(e.sample('recording', 100_000)).toBe(100);
	});

	it('freezes a late opener who finds the ride already ended at the stop time', () => {
		const e = createActiveElapsed(0);
		// terminal on the first sample, fed the real stop time → recorded duration (stopped − started)
		expect(e.sample('finalized', 3_600_000)).toBe(3_600);
		expect(e.sample('finalized', 9_999_999)).toBe(3_600);
	});

	it('never returns negative under clock skew (now before start)', () => {
		const e = createActiveElapsed(5_000);
		expect(e.sample('recording', 0)).toBe(0);
	});
});
