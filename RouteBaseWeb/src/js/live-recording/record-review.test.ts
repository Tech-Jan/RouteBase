import { describe, it, expect } from 'vitest';
import { selectedTrackIds, allSelected } from './record-review';

// The fetch wrappers moved to core/review-api.ts when the details-page review banner started calling them
// too — their LR-05 pins live in core/review-api.test.ts now.

describe('record-review selection logic', () => {
	it('collects only checked track ids, preserving order', () => {
		const rows = [
			{ checked: true, trackId: 10 },
			{ checked: false, trackId: 20 },
			{ checked: true, trackId: 30 },
		];
		expect(selectedTrackIds(rows)).toEqual([10, 30]);
	});

	it('returns an empty array when nothing is checked', () => {
		expect(selectedTrackIds([{ checked: false, trackId: 1 }])).toEqual([]);
	});

	it('allSelected is true only when every row is checked', () => {
		expect(allSelected([{ checked: true, trackId: 1 }, { checked: true, trackId: 2 }])).toBe(true);
		expect(allSelected([{ checked: true, trackId: 1 }, { checked: false, trackId: 2 }])).toBe(false);
	});

	it('allSelected is false for an empty set (nothing to select)', () => {
		expect(allSelected([])).toBe(false);
	});
});
