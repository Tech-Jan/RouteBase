// review-api.ts — the owner-scoped review/trash calls, shared by the /Record review cards and the
// activity-details review banner. Fetch wrappers only: no DOM and no reload policy, so each page decides
// for itself what a success means (the cards refresh in place, the banner navigates).

async function postJson(url: string, body: object): Promise<boolean> {
	try {
		const resp = await fetch(url, {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify(body),
		});
		return resp.ok;
	} catch {
		return false;
	}
}

/**
 * POST /api/review/{action} — approve / trash / restore for auto-detected recordings.
 *
 * Returns false on any network-level failure so a rejected fetch never escapes as an unhandled rejection
 * that leaves the triggering button permanently disabled (LR-05).
 */
export async function postReview(action: 'approve' | 'trash' | 'restore', body: object): Promise<boolean> {
	return postJson(`/api/review/${action}`, body);
}

/**
 * POST /api/trash/restore — the generic restore (each ride returns to its lifecycle_prev).
 *
 * A trashed AUTO recording goes through postReview('restore') instead: that one also clears the owning
 * session's 'abandoned' flag, without which the background purge would reclaim the ride it just restored.
 */
export async function postTrashRestore(trackIds: number[]): Promise<boolean> {
	return postJson('/api/trash/restore', { trackIds });
}
