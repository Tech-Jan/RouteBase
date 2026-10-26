// edit-save-target.ts — where a save leaves you. Pure: the controller just obeys the answer.
//
// The rule is NOT "replace reloads, copy jumps". It is: land on an activity that still exists. Replace usually
// updates the ride you opened (crop, cut) and reloading the plain URL is right — but a MERGE in replace mode is
// Option B: the base is trashed and the merged ride is a NEW activity. Reloading the plain URL there lands on a
// trashed track, which Details 404s.

import type { SaveMode, SaveResponse } from './edit-session-api';

export type SaveTarget =
	/** The activity the editor was opened on survived the save — reload its page. */
	| { kind: 'reload' }
	/** It did not (copy; replace-after-merge) — go to the activity the edit produced. */
	| { kind: 'activity'; trackId: number }
	/** Nothing to land on (every produced slot failed to create) — the activities list is the only safe page. */
	| { kind: 'activities' };

export function saveTarget(
	mode: SaveMode,
	response: SaveResponse,
	currentActivityId: number | undefined,
): SaveTarget {
	const survived = response.slots.some((s) => !s.created && s.trackId === currentActivityId);
	if (mode === 'replace' && survived) return { kind: 'reload' };

	// Copy → the copy. Replace-after-merge → the merged ride. Both are the first created slot: slot ids are
	// ascending and slot 0 is the one the editor was opened on, so its output is what the user is looking for.
	const created = response.slots.find((s) => s.created) ?? response.slots[0];
	if (created) return { kind: 'activity', trackId: created.trackId };

	// No slot at all: a replace-after-merge whose creation failed trashed the ride this URL points at —
	// reloading it would 404, so fall back to the list.
	return response.failedSlots?.length ? { kind: 'activities' } : { kind: 'reload' };
}
