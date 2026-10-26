// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { must } from '../core/invariant';
import {
	versionSubtitle, versionStats, canRestore, canUndoSave, canResetToOriginal, hasHistory, versionWhen,
	collapseVersions, parkLabel, headMoveRefusal, type VersionItem,
} from './version-history-model';

function v(over: Partial<VersionItem> = {}): VersionItem {
	return {
		versionId: 10,
		seq: 2,
		isHead: false,
		createdAt: '2026-07-12T09:00:00Z',
		pointCount: 1200,
		distanceMeters: 42_195,
		ascentMeters: 380.4,
		durationSeconds: 7200,
		provenance: 'Edited',
		parentNames: [],
		sameAsParent: false,
		commitId: null,
		...over,
	};
}

describe('versionSubtitle', () => {
	it('names the rides a merge came from', () => {
		expect(versionSubtitle(v({ provenance: 'Merged from 2 rides', parentNames: ['Base', 'Leg 2'] })))
			.toBe('Merged from 2 rides — Base, Leg 2');
	});

	it('says nothing extra when there is nothing to name', () => {
		expect(versionSubtitle(v({ provenance: 'Imported' }))).toBe('Imported');
	});

	// The parent links dangle on purpose when an input is purged; the copy has to admit that.
	it('admits when the rides it came from are gone', () => {
		expect(versionSubtitle(v({
			provenance: 'Merged — the rides it came from have been deleted',
			parentNames: [],
		}))).toBe('Merged — the rides it came from have been deleted');
	});
});

describe('versionStats', () => {
	it('reads distance, ascent and point count', () => {
		expect(versionStats(v())).toBe('42.2 km · 380 m · 1200 pts');
	});

	it('drops empty measures rather than printing zeroes', () => {
		expect(versionStats(v({ distanceMeters: null, ascentMeters: 0 }))).toBe('1200 pts');
	});
});

describe('canRestore', () => {
	it('does not offer to restore the version you are already on', () => {
		expect(canRestore(v({ isHead: true }))).toBe(false);
		expect(canRestore(v({ isHead: false }))).toBe(true);
	});
});

describe('versionWhen', () => {
	// The server sends a UTC instant with a Z; the row shows it in the reader's own timezone.
	it('reads the UTC instant the server sent', () => {
		const when = versionWhen(v({ createdAt: '2026-07-12T09:00:00Z' }));
		expect(when).toContain('2026');
		expect(when).toMatch(/\d{1,2}:\d{2}/); // a wall-clock time, whatever the runner's locale/zone
	});

	// A version row must still render if the timestamp is junk — a missing date is not worth a broken dialog.
	it('says nothing rather than "Invalid Date"', () => {
		expect(versionWhen(v({ createdAt: 'not-a-date' }))).toBe('');
	});
});

describe('collapseVersions', () => {
	// Newest-first, as the server sends them.
	const chain = (n: number, sameAsParent = false): VersionItem[] =>
		Array.from({ length: n }, (_, i) => v({
			versionId: n - i, seq: n - i, isHead: i === 0, sameAsParent: sameAsParent && i !== 0,
		}));

	it('shows everything when the history is short', () => {
		const { visible, hidden } = collapseVersions(chain(3));
		expect(visible).toHaveLength(3);
		expect(hidden).toHaveLength(0);
	});

	it('folds the tail of a long history but never the current version', () => {
		const { visible, hidden } = collapseVersions(chain(12));
		expect(visible).toHaveLength(5);
		expect(must(visible[0], 'head version').isHead).toBe(true);
		expect(hidden).toHaveLength(7);
	});

	// A restore re-points at content already in the list, so it is the row that carries the least information.
	it('folds restores that changed nothing before it folds real edits', () => {
		const items = [
			v({ versionId: 4, seq: 4, isHead: true }),
			v({ versionId: 3, seq: 3, sameAsParent: true }),   // restore — no new content
			v({ versionId: 2, seq: 2, sameAsParent: true }),   // restore — no new content
			v({ versionId: 1, seq: 1 }),
		];
		const { visible, hidden, noOpCount } = collapseVersions(items, 3);
		expect(visible.map((x) => x.versionId)).toEqual([4, 1]); // the two real states, restores folded away
		expect(hidden.map((x) => x.versionId)).toEqual([3, 2]);
		expect(noOpCount).toBe(2);
	});
});

describe('hasHistory', () => {
	// The version a ride is imported with is not "an edit" — offering to restore it would be a lie.
	it('does not count the import-time version as history', () => {
		expect(hasHistory([v({ seq: 1, isHead: true })])).toBe(false);
		expect(hasHistory([v({ seq: 1 }), v({ seq: 2, isHead: true })])).toBe(true);
	});

	// A merge in Replace mode CREATES the merged ride (SaveMapper "Option B"), so it has exactly ONE version —
	// and that version is the merge. Counting versions alone called it "never edited" AND hid the list, which
	// hid the only door that can un-merge it: you had to make an unrelated second edit before it appeared.
	it('counts the single version of a ride BORN from a save', () => {
		expect(hasHistory([v({ seq: 1, isHead: true, commitId: 55, provenance: 'Merged from 2 rides' })])).toBe(true);
	});
});

describe('canResetToOriginal', () => {
	// Deliberately NOT the same question as hasHistory: a freshly merged ride HAS a history (it can be
	// un-merged) and yet nothing to reset — it is already at its first version, so Reset would be inert.
	it('has nothing to do on a ride that only has its first version', () => {
		expect(canResetToOriginal([v({ seq: 1, isHead: true, commitId: 55 })])).toBe(false);
		expect(canResetToOriginal([v({ seq: 1, isHead: true })])).toBe(false);
	});

	it('has work to do once a later version exists', () => {
		expect(canResetToOriginal([v({ seq: 1 }), v({ seq: 2, isHead: true })])).toBe(true);
	});
});

describe('parkLabel', () => {
	it.each([
		['released', 'Park (hide from stats)'],
		['in-work', 'Put back into your activities'],
	])('%s → %s', (lifecycle, expected) => {
		expect(parkLabel(lifecycle)).toBe(expected);
	});

	// A trashed ride is restored from the trash, not parked from here.
	it('offers nothing for a trashed ride', () => {
		expect(parkLabel('trashed')).toBeNull();
	});
});

describe('canUndoSave', () => {
	// The un-merge door. It used to hang off Activity.edited_commit_id, which reset and revert both clear —
	// so a merged-then-edited-then-reset ride became permanently merged. It lives on the row now, and a merge
	// product's seq-1 version (written BY the merge commit) always carries it.
	it('offers the creating commit on seq 1, however much the ride was edited later', () => {
		expect(canUndoSave(v({ seq: 1, commitId: 77, isHead: false }))).toBe(true);
	});

	it('offers the head, whose commit wrote the content the ride currently has', () => {
		expect(canUndoSave(v({ seq: 3, commitId: 90, isHead: true }))).toBe(true);
	});

	// A chained revert must go newest → oldest (G20): a middle version's commit is superseded, and offering it
	// would render a door that can only 409.
	it('does not offer a superseded middle version', () => {
		expect(canUndoSave(v({ seq: 2, commitId: 88, isHead: false }))).toBe(false);
	});

	// An import, a recording, a restore or an elevation recompute — no save to undo.
	it('offers nothing where no commit wrote the version', () => {
		expect(canUndoSave(v({ seq: 1, commitId: null }))).toBe(false);
		expect(canUndoSave(v({ seq: 4, commitId: null, isHead: true }))).toBe(false);
	});
});

describe('headMoveRefusal', () => {
	// Both refusals arrive as a 409. Telling them apart is the whole job: they send the user to different
	// places, and the panel used to call every 409 a live recording.
	it('names the editor holding the ride, not a recording', () => {
		expect(headMoveRefusal('alreadyCheckedOut')).toContain('edit open');
		expect(headMoveRefusal('stillRecording')).toContain('recording');
	});

	// The client reads `code` off the error body; a controller answering in some other shape yields undefined.
	// That must fall through to the caller's own wording, never to a confidently wrong refusal.
	it('has no opinion on a code it does not know', () => {
		expect(headMoveRefusal(undefined)).toBeNull();
		expect(headMoveRefusal('httpError')).toBeNull();
	});
});
