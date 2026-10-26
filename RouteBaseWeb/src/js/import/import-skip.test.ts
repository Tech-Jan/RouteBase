// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { isSkip, skipBadge } from './import-skip';

// The dedupe message. Blocking the re-import is always right — re-importing a ride you merged away would
// double-count its distance — but "Duplicate" about a ride sitting in your trash is a lie that leaves you
// with no way to find it, which is exactly the dead end the merge incident produced.
describe('import skip outcomes', () => {
	it.each(['skipped', 'in-trash', 'in-work'])('%s counts as a skip', (outcome) => {
		expect(isSkip(outcome)).toBe(true);
	});

	it.each(['imported', 'failed', 'importing'])('%s is not a skip', (outcome) => {
		expect(isSkip(outcome)).toBe(false);
	});

	it('tells a trashed ride apart from a plain duplicate', () => {
		expect(skipBadge('in-trash')).toBe('In trash — skipped');
		expect(skipBadge('in-work')).toBe('Awaiting review — skipped');
		expect(skipBadge('skipped')).toBe('Duplicate — skipped');
	});
});
