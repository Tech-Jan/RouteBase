/**
 * Activities grid — batched multi-delete controller.
 *
 * Mirrors the segment-stats efforts Apply pattern (segment-stats-apply-controller.ts): per-row delete
 * buttons MARK activities for deletion (instant, local) instead of deleting one-by-one. A "Delete"
 * button in the toolbar shows the pending count and commits every marked activity behind a single
 * confirm, then reloads. Marks are keyed by trackId, so they survive paging/sorting.
 */

import { chunkIds } from './activities-bulk-controller';
import type { ApiError } from '../generated/contracts';

export interface DeleteController {
	mountButton(toolbar: HTMLElement): void;
	/** Toggle pending-deletion for a track. Returns the new marked state. */
	toggleRemoval(trackId: number): boolean;
	isMarked(trackId: number): boolean;
}

export function createDeleteController(): DeleteController {
	const pending = new Set<number>();
	let btn: HTMLButtonElement | null = null;
	let badge: HTMLSpanElement | null = null;
	let applying = false;

	function refreshButton(): void {
		if (!btn || !badge) return;
		const dirty = pending.size > 0;
		btn.disabled = !dirty;
		btn.classList.toggle('act-delete-btn--dirty', dirty);
		badge.textContent = String(pending.size);
		badge.hidden = !dirty;
	}

	async function apply(): Promise<void> {
		if (applying || !btn || pending.size === 0) return;
		const n = pending.size;
		const noun = n === 1 ? 'activity' : 'activities';
		// Soft-trash, not a purge: the endpoint moves rows to the trash, restorable from the Trash page.
		if (!confirm(`Move ${n} ${noun} to the trash?\n\nThey can be restored from the Trash page.`)) return;

		applying = true;
		btn.disabled = true;
		let committed = 0;
		try {
			// Batch request(s) (not N single DELETEs): the server moves all marked rows in one load/save per
			// batch. chunkIds splits selections above the per-request cap. Foreign ids are skipped server-side,
			// so we don't tally per-id failures here.
			for (const batch of chunkIds([...pending])) {
				const res = await fetch('/api/activities/bulk/delete', {
					method: 'POST',
					headers: { 'Content-Type': 'application/json' },
					body: JSON.stringify({ trackIds: batch }),
				});
				if (!res.ok) {
					const body = (await res.json().catch(() => ({}))) as Partial<ApiError>;
					throw new Error(body.message ?? `Request failed (${res.status}).`);
				}
				committed += batch.length;
			}
			location.reload();
		} catch (err) {
			const detail = err instanceof Error && err.message ? ` (${err.message})` : '';
			if (committed > 0) {
				// Batches before the failure are already trashed server-side — reload so the grid stops
				// showing rows that are gone (and can't re-mark them into a second delete).
				alert(`Moved ${committed} of ${n} ${noun} to the trash before a batch failed${detail}. Reloading.`);
				location.reload();
				return;
			}
			alert(`Failed to move the ${noun} to the trash${detail}.`);
			applying = false;
			btn.disabled = false;
		}
	}

	return {
		mountButton(toolbar: HTMLElement): void {
			btn = document.createElement('button');
			btn.type = 'button';
			btn.className = 'grid-toolbar-btn act-delete-btn';
			btn.title = 'Delete the marked activities';
			btn.disabled = true;
			badge = document.createElement('span');
			badge.className = 'act-delete-badge';
			badge.hidden = true;
			btn.innerHTML = '<i class="bi bi-trash"></i> Delete ';
			btn.appendChild(badge);
			btn.addEventListener('click', () => void apply());
			toolbar.appendChild(btn);
		},
		toggleRemoval(trackId: number): boolean {
			if (applying) return pending.has(trackId); // ignore marks while a delete commit is in flight
			const marked = !pending.has(trackId);
			if (marked) pending.add(trackId);
			else pending.delete(trackId);
			refreshButton();
			return marked;
		},
		isMarked(trackId: number): boolean {
			return pending.has(trackId);
		},
	};
}
