// review-banner.ts — the approve / delete / restore bar the details page renders above a ride that is not
// released yet (see Pages/Activities/_ReviewBanner.cshtml).
//
// The details page opens such a ride only for its OWNER (ActivityViewOptions.AllowOwnerNonReleased), so
// this wiring is owner-only by construction — the markup simply does not exist for anyone else.

import { asyncCallback } from '../core/async-listener';
import { readData } from '../core/data-attrs';
import { postReview, postTrashRestore } from '../core/review-api';
import type { RecordableActivityType } from './activity-type-vocabulary';

/** Ride the button through a review call, re-enabling it if the call failed (LR-05). */
function onAction(btn: HTMLButtonElement, run: () => Promise<boolean>, done: () => void): void {
	btn.addEventListener('click', asyncCallback('review action', async () => {
		btn.disabled = true;
		if (await run()) done();
		else btn.disabled = false;
	}));
}

export function setupReviewBanner(): void {
	const banner = document.getElementById('reviewBanner');
	if (!banner) return; // released ride — no banner rendered

	const trackId = Number(readData(banner, 'trackId'));
	if (!Number.isFinite(trackId)) return;
	// "auto" = an auto-detected recording, which owns the extra session bookkeeping on restore.
	const isAuto = readData(banner, 'reviewSource') === 'auto';

	const typeSel = document.getElementById('reviewTypeSelect') as HTMLSelectElement | null;
	const approveBtn = document.getElementById('reviewApproveBtn') as HTMLButtonElement | null;
	const trashBtn = document.getElementById('reviewTrashBtn') as HTMLButtonElement | null;
	const restoreBtn = document.getElementById('reviewRestoreBtn') as HTMLButtonElement | null;

	const reload = (): void => { window.location.reload(); };

	if (approveBtn) {
		onAction(approveBtn, () => {
			// Options come from ActivityTypeVocabulary.Recordable, plus the LR-06 passthrough for a
			// detected type outside that list.
			const activityType = typeSel?.value as RecordableActivityType | undefined;
			return postReview('approve', { trackIds: [trackId], activityType });
		}, reload); // the approved ride re-renders as a normal activity
	}

	if (trashBtn) {
		onAction(trashBtn, async () => {
			if (!confirm('Move this activity to trash? It will be permanently deleted after 2 days — restore anytime before then.'))
				return false;
			return postReview('trash', { trackIds: [trackId] });
		}, () => { window.location.href = '/Record'; }); // back to the review queue it came from
	}

	if (restoreBtn) {
		onAction(restoreBtn,
			() => (isAuto ? postReview('restore', { trackIds: [trackId] }) : postTrashRestore([trackId])),
			reload);
	}
}
