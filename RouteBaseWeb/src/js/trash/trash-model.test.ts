// @vitest-environment node
import { describe, it, expect } from 'vitest';
import {
	reasonLabel,
	reasonCategory,
	restoreLabel,
	purgeDestroysAMerge,
	purgeConfirmText,
	whyLine,
} from './trash-model';
import { reasonOptions } from './trash-grid';
import { formatDateDMY } from '../core/grid/grid-format';
import type { TrashedActivity } from '../generated/contracts';

function item(over: Partial<TrashedActivity> = {}): TrashedActivity {
	return {
		trackId: 1,
		name: 'Morning Ride',
		activityType: 'Ride',
		startedIso: '2026-07-12T06:00:00',
		distanceMeters: 12_500,
		ascentMeters: 300,
		reason: 'user',
		trashedAt: '2026-07-12T09:00:00Z',
		restoresTo: 'released',
		mergedIntoId: null,
		mergedIntoName: null,
		...over,
	};
}

describe('reasonLabel', () => {
	it('names the ride a merge swallowed it into', () => {
		expect(reasonLabel(item({ reason: 'edit-merge', mergedIntoName: 'Big Ride' })))
			.toBe('Merged into “Big Ride”');
	});

	it('still explains a merge whose product is gone', () => {
		expect(reasonLabel(item({ reason: 'edit-merge', mergedIntoName: null })))
			.toBe('Merged into another ride');
	});

	it.each([
		['review-rejected', 'Rejected in review'],
		['abandoned', 'Recording abandoned'],
		['user', 'Deleted'],
		['something-new', 'Deleted'],
	])('%s → %s', (reason, expected) => {
		expect(reasonLabel(item({ reason }))).toBe(expected);
	});
});

describe('restoreLabel', () => {
	it('sends a rejected auto ride back to the review queue, not into circulation', () => {
		expect(restoreLabel(item({ restoresTo: 'in-work' }))).toBe('Restores to review queue');
	});

	it('sends a deleted ride back to your activities', () => {
		expect(restoreLabel(item({ restoresTo: 'released' }))).toBe('Restores to your activities');
	});
});

describe('purgeDestroysAMerge', () => {
	const plain = item({ trackId: 1 });
	const source = item({ trackId: 2, reason: 'edit-merge', mergedIntoId: 99, mergedIntoName: 'Big Ride' });

	it('is true when a chosen ride still feeds a live merge', () => {
		expect(purgeDestroysAMerge([plain, source], [2])).toBe(true);
	});

	it('is false when only ordinary trash is chosen', () => {
		expect(purgeDestroysAMerge([plain, source], [1])).toBe(false);
	});

	it('ignores merge sources that were not selected', () => {
		expect(purgeDestroysAMerge([plain, source], [])).toBe(false);
	});

	// The FK nulls superseded_by when the merge product is purged: nothing is merged into anything any more,
	// so there is no un-merge left to lose and no warning to give.
	it('is false once the merge product itself is gone', () => {
		const orphaned = item({ trackId: 2, reason: 'edit-merge', mergedIntoId: null });
		expect(purgeDestroysAMerge([orphaned], [2])).toBe(false);
	});
});

describe('purgeConfirmText', () => {
	it('names the consequence when a still-undoable merge is at stake', () => {
		const source = item({ trackId: 2, reason: 'edit-merge', mergedIntoId: 99 });
		const text = purgeConfirmText([source], [2]);
		expect(text).toContain('cannot be undone');
		expect(text).toContain('that merge can never be undone');
	});

	it('does not cry wolf for ordinary trash', () => {
		const text = purgeConfirmText([item()], [1]);
		expect(text).toContain('Permanently delete this ride?');
		expect(text).not.toContain('merge');
	});

	it('counts the rides', () => {
		expect(purgeConfirmText([item({ trackId: 1 }), item({ trackId: 2 })], [1, 2]))
			.toContain('these 2 rides');
	});
});

// The trash must read like the rest of the app: dd.mm.yyyy, same as the Activities grid.
describe('date format', () => {
	it('matches the shared dd.mm.yyyy formatter', () => {
		expect(formatDateDMY('2026-07-11T06:00:00Z')).toBe('11.07.2026');
	});
});

// The card's why-line and the confirm dialog read from the same functions — a merge source must warn in both.
describe('whyLine', () => {
	it('warns on the card itself when a purge would cost an un-merge', () => {
		const source = item({ reason: 'edit-merge', mergedIntoId: 99, mergedIntoName: 'Big Ride' });
		expect(whyLine(source)).toBe(
			'Merged into “Big Ride” — deleting it permanently would make that merge un-undoable',
		);
	});

	it('otherwise says why it is here and what Restore does', () => {
		expect(whyLine(item({ restoresTo: 'in-work', reason: 'review-rejected' })))
			.toBe('Rejected in review · Restores to review queue');
	});
});

// The Reason filter needs buckets, not one value per merged-into ride — that is the whole point of the split
// between reasonCategory (filter) and reasonLabel (card).
describe('reasonCategory', () => {
	it('drops the merge product name that reasonLabel carries', () => {
		const a = item({ trackId: 1, reason: 'edit-merge', mergedIntoName: 'Big Ride' });
		const b = item({ trackId: 2, reason: 'edit-merge', mergedIntoName: 'Other Ride' });
		expect(reasonCategory(a)).toBe('Merged');
		expect(reasonCategory(a)).toBe(reasonCategory(b));
		expect(reasonOptions([a, b])).toEqual(['Merged']);
	});

	it('lists one option per bucket present', () => {
		expect(reasonOptions([
			item({ trackId: 1, reason: 'user' }),
			item({ trackId: 2, reason: 'abandoned' }),
			item({ trackId: 3, reason: 'user' }),
		])).toEqual(['Deleted', 'Recording abandoned']);
	});
});
