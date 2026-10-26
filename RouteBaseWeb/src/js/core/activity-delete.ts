/**
 * activity-delete.ts — Delete an activity via the API (with confirmation).
 *
 * Extracted from the old utils god-file (CB-01 dissolution / CB-03 residual).
 * Used by dashboard/dashboard.ts (delegated card-click listener, no guard, reload on success) and
 * activity/activity-actions.ts (Details toolbar delete button, DC-10 re-entry guard, redirect on
 * success) via the `opts` below — CR6-06.
 */

import { readApiErrorMessage } from "./api-error";

export interface DeleteActivityOptions {
	/** Called after a successful delete instead of the default `location.reload()`. */
	onSuccess?: () => void;
	/**
	 * DC-10: guard re-entry — a double-click would otherwise fire a second DELETE racing the
	 * navigation. Disabled before the fetch; re-enabled only on the error paths (success navigates
	 * away, so there is nothing left to re-enable).
	 */
	guardBtn?: HTMLButtonElement;
}

/**
 * Delete an activity via the API, with confirmation prompt, then `onSuccess` (default: reload).
 */
export async function deleteActivity(
	trackId: number,
	trackName: string,
	opts?: DeleteActivityOptions,
): Promise<void> {
	if (
		!confirm(
			`Delete activity "${trackName}"?\n\nThis will permanently delete the activity, its GPS data, and segment matches.`,
		)
	) {
		return;
	}

	const guardBtn = opts?.guardBtn;
	if (guardBtn) {
		if (guardBtn.disabled) return;
		guardBtn.disabled = true;
	}

	try {
		const response = await fetch(`/api/activities/${trackId}`, {
			method: "DELETE",
		});
		if (response.ok || response.status === 204) {
			(opts?.onSuccess ?? (() => location.reload()))();
		} else {
			alert(`Error deleting activity: ${await readApiErrorMessage(response)}`);
			if (guardBtn) guardBtn.disabled = false;
		}
	} catch (error: unknown) {
		console.error("Error deleting activity:", error);
		const msg = error instanceof Error ? error.message : String(error);
		alert(`Error deleting activity: ${msg}`);
		if (guardBtn) guardBtn.disabled = false;
	}
}
