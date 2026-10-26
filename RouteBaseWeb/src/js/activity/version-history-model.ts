// version-history-model.ts — what a version row says it is. Pure: no DOM, no fetch.
//
// Shared by the two surfaces that show a ride's history: the Details page panel (version-history.ts) and the
// editor's "Edit history" dialog (track-edit/edit-history-dialog.ts). One formatter, so a version cannot read
// one way in view mode and another way inside the editor.

import { TRACK_EDIT_API_ERRORS } from '../track-edit/edit-session-api';
import type { TrackVersionView } from '../generated/contracts';

/** One version row — generated from C# TrackVersionView. `sameAsParent` = points at content a parent
 *  already had (every restore is one); `commitId` = the save that wrote this version, null for an
 *  import / recording / restore / recompute. */
export type VersionItem = TrackVersionView;

/** How many versions the list shows before it asks you to expand. */
export const VERSIONS_SHOWN = 5;

export interface CollapsedVersions {
	visible: VersionItem[];
	hidden: VersionItem[];
	/** Of the hidden ones, how many introduced no new content (restores). Named in the expander's label. */
	noOpCount: number;
}

/**
 * What to show before the user asks for everything. A history is mostly restores — rows that re-point at
 * content already in the list — so those are folded first, and only the newest `keep` of what remains is
 * shown. Nothing is discarded: `hidden` is the rest, and the expander reveals it.
 *
 * The head is always visible. It is the version you are looking at; a history that hides "current" is a lie.
 */
export function collapseVersions(
	items: readonly VersionItem[], keep: number = VERSIONS_SHOWN,
): CollapsedVersions {
	const interesting = items.filter((v) => v.isHead || !v.sameAsParent);
	const visible = interesting.slice(0, keep);
	const shown = new Set(visible.map((v) => v.versionId));
	const hidden = items.filter((v) => !shown.has(v.versionId));
	return { visible, hidden, noOpCount: hidden.filter((v) => v.sameAsParent).length };
}

/** What a version row says it is. Names the rides a merge came from, when they still exist. */
export function versionSubtitle(v: VersionItem): string {
	if (v.parentNames.length === 0) return v.provenance;
	return `${v.provenance} — ${v.parentNames.join(', ')}`;
}

export function versionStats(v: VersionItem): string {
	const bits: string[] = [];
	if (v.distanceMeters && v.distanceMeters > 0) bits.push(`${(v.distanceMeters / 1000).toFixed(1)} km`);
	if (v.ascentMeters && v.ascentMeters > 0) bits.push(`${Math.round(v.ascentMeters)} m`);
	bits.push(`${v.pointCount} pts`);
	return bits.join(' · ');
}

/** Restoring the version you are already on would do nothing, so it is not offered. */
export function canRestore(v: VersionItem): boolean {
	return !v.isHead;
}

/**
 * Whether this row may offer "Undo this save" — the only thing that can un-merge or un-split a ride, because
 * restoring a version touches THIS ride and nothing else, while undoing a save also deletes the rides it
 * created and untrashes the ones it swallowed.
 *
 * A pure mirror of the server's revert eligibility, so we do not render doors that can only 409:
 *   · seq 1 with a commit — the save that CREATED this ride (a merge/split product). Reverting it deletes the
 *     ride whole, which is valid however much it has been edited since, so this door never expires. It is the
 *     one that used to be lost: it hung off `Activity.edited_commit_id`, which both reset and revert clear.
 *   · the head — its commit wrote the content the ride currently has, so there is a head to move back.
 * Anything older is superseded by a newer commit, and a chained revert must go newest → oldest (G20).
 */
export function canUndoSave(v: VersionItem): boolean {
	if (v.commitId === null) return false;   // an import, a recording, a restore or a recompute — no save to undo
	return v.seq === 1 || v.isHead;
}

/**
 * When this version came into being, in the reader's own timezone. The server sends a UTC instant (the field
 * is a DateTimeOffset since AR-02, so it always serializes with an explicit offset and Date parses it as the
 * instant it is — no local-vs-UTC ambiguity to get wrong here).
 *
 * "Which one do I want back?" is a question people answer by time far more often than by point count, so this
 * is the field the row is really navigated by.
 */
export function versionWhen(v: VersionItem): string {
	const at = new Date(v.createdAt);
	if (Number.isNaN(at.getTime())) return '';
	return at.toLocaleString(undefined, {
		day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit',
	});
}

/**
 * Whether this ride has a history worth showing.
 *
 * Counting versions is not enough. A ride BORN from a save — a merge or a split product — has exactly ONE
 * version, and that version IS an edit: the commit that wrote it is the merge. Asking `length > 1` called such
 * a ride "never edited" and, worse, hid the list entirely — so its "Undo this save" row (the only way to
 * un-merge it) never rendered, until some unrelated second edit happened to add a row and reveal it.
 *
 * A ride that was imported or recorded also has one version, but no commit wrote it. That is the difference,
 * and it is exactly what `commitId` records.
 */
export function hasHistory(versions: readonly VersionItem[]): boolean {
	return hasHistoryFrom(versions.length, versions.some((v) => v.commitId !== null));
}

/**
 * The SAME rule, from the two counts alone — so the panel can answer it from the server-rendered
 * `data-version-count` / `data-any-commit` without fetching the list at all. Most rides are imported once and
 * never edited, and the panel used to spend an HTTP round-trip on every owner page load just to discover that.
 *
 * The server sends raw counts, never the verdict: this stays the one place the rule is written, so the two
 * cannot drift (which is exactly how the panel and the editor's history dialog drifted apart before).
 */
export function hasHistoryFrom(versionCount: number, anyCommit: boolean): boolean {
	return versionCount > 1 || anyCommit;
}

/**
 * Whether "reset to original" has anything to do. Reset deletes every version after the first — a ride whose
 * only version IS the first is already there, so the button would be an inert promise. This is deliberately
 * NOT the same question as hasHistory: a freshly merged ride HAS a history (it can be un-merged) and yet has
 * nothing to reset.
 */
export function canResetToOriginal(versions: readonly VersionItem[]): boolean {
	return versions.length > 1;
}

/**
 * Why a head move (restore / reset) was refused, in the user's words — or null when the code is not a refusal
 * we know, and the caller should fall back to its own "could not do this" wording.
 *
 * Lives with the model, not in either dialog, because BOTH history surfaces make this same call and they must
 * not answer it differently: the two of them drifted once already (the panel assumed every 409 was a live
 * recording and told people to "finish the recording" when in fact an editor was holding the ride).
 *
 * Both refusals name a thing the user has to go and resolve — that is the whole point of saying which one.
 */
export function headMoveRefusal(code: string | undefined): string | null {
	if (code === TRACK_EDIT_API_ERRORS.stillRecording)
		return 'This ride is still recording — finish the recording first.';
	if (code === TRACK_EDIT_API_ERRORS.alreadyCheckedOut)
		return 'This ride has an edit open — resume or discard it first.';
	return null;
}

/** Park takes a ride out of circulation; release puts it back. Trashed rides are handled in the trash. */
export function parkLabel(lifecycle: string): string | null {
	if (lifecycle === 'released') return 'Park (hide from stats)';
	if (lifecycle === 'in-work') return 'Put back into your activities';
	return null;
}
