// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { saveTarget } from './edit-save-target';
import type { SaveResponse, SavedSlot } from './edit-session-api';

function response(...slots: SavedSlot[]): SaveResponse {
	return { commitId: 1, slots, deriveQueued: false, failedSlots: null };
}

const BASE = 10; // the activity the editor was opened on

describe('saveTarget', () => {
	it('reloads the page when Replace updated the ride in place (crop / cut)', () => {
		const res = response({ slotId: 0, trackId: BASE, name: 'Live Recording', created: false });
		expect(saveTarget('replace', res, BASE)).toEqual({ kind: 'reload' });
	});

	// The bug this file exists for: Option B trashes the base and creates the merged ride, so reloading the
	// plain URL landed on a trashed track and Details 404'd.
	it('goes to the merged ride when Replace merged (the base was trashed)', () => {
		const res = response({ slotId: 0, trackId: 77, name: 'Live Recording', created: true });
		expect(saveTarget('replace', res, BASE)).toEqual({ kind: 'activity', trackId: 77 });
	});

	it('goes to the copy on Save as copy', () => {
		const res = response(
			{ slotId: 0, trackId: 78, name: 'Live Recording (edited)', created: true },
			{ slotId: 1, trackId: 79, name: 'Live Recording (1)', created: true },
		);
		expect(saveTarget('copy', res, BASE)).toEqual({ kind: 'activity', trackId: 78 });
	});

	it('still reloads when Replace split: the base survives, the split-offs are extra', () => {
		const res = response(
			{ slotId: 0, trackId: BASE, name: 'Live Recording', created: false },
			{ slotId: 1, trackId: 80, name: 'Live Recording (1)', created: true },
		);
		expect(saveTarget('replace', res, BASE)).toEqual({ kind: 'reload' });
	});

	it('falls back to a reload when the response names no slot at all', () => {
		expect(saveTarget('replace', response(), BASE)).toEqual({ kind: 'reload' });
	});

	// A merge whose phase-2 creation failed: the base is trashed (phase 1 committed) and nothing was created,
	// so reloading the plain URL would 404 on the trashed ride — the list is the only safe landing.
	it('goes to the activities list when every produced slot failed to create', () => {
		const res: SaveResponse = {
			commitId: 1,
			slots: [],
			deriveQueued: false,
			failedSlots: [{ slotId: 0, name: 'Live Recording' }],
		};
		expect(saveTarget('replace', res, BASE)).toEqual({ kind: 'activities' });
	});
});
