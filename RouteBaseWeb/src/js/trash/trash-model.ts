// trash-model.ts — the trash's pure presentation logic (no DOM, no fetch); contracts come generated
// (TrashedActivity/TrashGridBootstrap in src/js/generated/contracts.ts, island schemas D27).
//
// The rule this UI exists to enforce: purging a ride that a merge consumed, while the merged ride still
// exists, destroys the ability to ever un-merge it. The server refuses such a purge with 409 until the caller
// confirms — these functions turn that refusal into a warning that NAMES the consequence, rather than a generic
// "are you sure?". The card's why-line and the confirm dialog both read from here, so they cannot word it
// differently.

import type { TrashedActivity } from '../generated/contracts';

/** Why a ride is in the trash, in the user's words. Unknown reasons fall back to the plain case. */
export function reasonLabel(item: TrashedActivity): string {
	switch (item.reason) {
		case 'edit-merge':
			return item.mergedIntoName
				? `Merged into “${item.mergedIntoName}”`
				: 'Merged into another ride';
		case 'review-rejected':
			return 'Rejected in review';
		case 'abandoned':
			return 'Recording abandoned';
		default:
			return 'Deleted';
	}
}

/**
 * The reason WITHOUT the merge product's name — the filter needs a handful of buckets, and
 * {@link reasonLabel} yields one distinct value per merged-into ride.
 */
export function reasonCategory(item: TrashedActivity): string {
	switch (item.reason) {
		case 'edit-merge':
			return 'Merged';
		case 'review-rejected':
			return 'Rejected in review';
		case 'abandoned':
			return 'Recording abandoned';
		default:
			return 'Deleted';
	}
}

/** What Restore will do — the transition, not a guess (a rejected auto ride goes back to the review queue). */
export function restoreLabel(item: TrashedActivity): string {
	return item.restoresTo === 'in-work' ? 'Restores to review queue' : 'Restores to your activities';
}

/**
 * True when purging these rides would take a still-undoable merge with them. Drives the extra warning
 * BEFORE we even ask the server — the server refuses anyway, but a confirm dialog that already names the
 * consequence beats one that has to round-trip to find out.
 */
export function purgeDestroysAMerge(items: ReadonlyArray<TrashedActivity>, ids: ReadonlyArray<number>): boolean {
	const chosen = new Set(ids);
	return items.some((i) => chosen.has(i.trackId) && i.mergedIntoId !== null);
}

/** The confirmation text for a permanent delete. Names the consequence when there is one to name. */
export function purgeConfirmText(items: ReadonlyArray<TrashedActivity>, ids: ReadonlyArray<number>): string {
	const n = ids.length;
	const what = n === 1 ? 'this ride' : `these ${n} rides`;
	const base = `Permanently delete ${what}? This cannot be undone.`;
	if (!purgeDestroysAMerge(items, ids)) return base;
	return `${base}\n\nSome of them were merged into another ride. Deleting them means that merge can never be undone.`;
}

/** The card's why-line: why it's here, and either what Restore does or what a purge would cost. */
export function whyLine(item: TrashedActivity): string {
	// A ride whose merge is still undoable is the one the user must not purge by accident — say so on the card,
	// not only in the dialog they are about to click through.
	return item.mergedIntoId !== null
		? `${reasonLabel(item)} — deleting it permanently would make that merge un-undoable`
		: `${reasonLabel(item)} · ${restoreLabel(item)}`;
}
