// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { bucketStartMs } from './statistics-bars';

// ST-06: bucket-grid math — pure and independently testable.
describe('bucketStartMs', () => {
	it('snaps every weekday to the Monday of its ISO week', () => {
		const monday = Date.UTC(2024, 0, 1); // 2024-01-01 is a Monday
		for (let i = 0; i < 7; i++) {
			expect(bucketStartMs(Date.UTC(2024, 0, 1 + i), 'week')).toBe(monday);
		}
		// The next Monday starts a new bucket.
		expect(bucketStartMs(Date.UTC(2024, 0, 8), 'week')).toBe(Date.UTC(2024, 0, 8));
	});

	it('snaps any day to the first of its month for monthly buckets', () => {
		expect(bucketStartMs(Date.UTC(2024, 5, 15), 'month')).toBe(Date.UTC(2024, 5, 1));
		expect(bucketStartMs(Date.UTC(2024, 5, 30), 'month')).toBe(Date.UTC(2024, 5, 1));
	});
});
