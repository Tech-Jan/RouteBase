// record-review.ts — the review-queue controls on the merged Record page.
// Minimaps for the review cards, per-card Approve/Delete/Restore, and a bulk (multiselect) Apply toolbar.
// All actions hit the owner-scoped /api/review AJAX endpoints (ReviewApiController).

import { asyncCallback } from '../core/async-listener';
import { readData } from '../core/data-attrs';
import { setupLazyThumbnails } from '../core/track-thumbnail';
import { postReview } from '../core/review-api';
import type { LatLngDto } from '../generated/contracts';
import type { RecordableActivityType } from '../activity/activity-type-vocabulary';

interface RowCheckbox {
	checked: boolean;
	trackId: number;
}

/** Track ids of the checked pending rows. Pure — unit-tested without the DOM. */
export function selectedTrackIds(checkboxes: ReadonlyArray<RowCheckbox>): number[] {
	return checkboxes.filter((c) => c.checked).map((c) => c.trackId);
}

/** True when there is at least one row and every row is checked (drives "select all"). Pure. */
export function allSelected(checkboxes: ReadonlyArray<RowCheckbox>): boolean {
	return checkboxes.length > 0 && checkboxes.every((c) => c.checked);
}

// ── DOM wiring ────────────────────────────────────────────────────────────

function rowCheckboxes(): HTMLInputElement[] {
	return Array.from(document.querySelectorAll<HTMLInputElement>('.rec-row-check'));
}

function toRowCheckbox(el: HTMLInputElement): RowCheckbox {
	return { checked: el.checked, trackId: Number(readData(el, 'trackId')) };
}

/** Renders each `data-utc-ts` cell's ISO instant in the browser's own local time, "dd.MM. HH:mm". */
function formatStartedColumn(): void {
	const pad = (n: number): string => String(n).padStart(2, '0');
	document.querySelectorAll<HTMLElement>('.rec-sessions-table [data-utc-ts]').forEach((el) => {
		const iso = readData(el, 'utcTs');
		if (!iso) return;
		const d = new Date(iso);
		el.textContent = `${pad(d.getDate())}.${pad(d.getMonth() + 1)}. ${pad(d.getHours())}:${pad(d.getMinutes())}`;
	});
}

function setupMinimaps(): void {
	setupLazyThumbnails({
		containerSelector: '.rec-review-card .minimap[data-track-id]',
		idAttribute: 'data-track-id',
		fetchGps: async (trackId): Promise<LatLngDto[] | null> => {
			const resp = await fetch(`/api/review/${trackId}/gps`);
			if (!resp.ok) throw new Error('GPS fetch failed: ' + resp.status);
			return resp.json();
		},
		trackColor: '#f59e0b', // --acc-amber, matches the pending badge
	});
}

function setupSelection(): void {
	const toolbar = document.getElementById('rec-bulk-toolbar');
	if (!toolbar) return;

	const selectAll = document.getElementById('rec-select-all') as HTMLInputElement | null;
	const countEl = document.getElementById('rec-bulk-count');
	const applyBtn = document.getElementById('rec-bulk-apply-btn') as HTMLButtonElement | null;
	const checks = rowCheckboxes();

	function refresh(): void {
		const models = checks.map(toRowCheckbox);
		const selected = selectedTrackIds(models);
		if (countEl) countEl.textContent = `${selected.length} selected`;
		if (applyBtn) applyBtn.disabled = selected.length === 0;
		if (selectAll) selectAll.checked = allSelected(models);
	}

	selectAll?.addEventListener('change', () => {
		for (const c of checks) c.checked = selectAll.checked;
		refresh();
	});
	for (const c of checks) c.addEventListener('change', refresh);

	const actionSel = document.getElementById('rec-bulk-action') as HTMLSelectElement | null;
	applyBtn?.addEventListener('click', asyncCallback('bulk review apply', async () => {
		const ids = selectedTrackIds(checks.map(toRowCheckbox));
		if (ids.length === 0) return;
		// UI "Delete" is a soft trash (2-day grace + restore), not a permanent delete.
		const action = (actionSel?.value === 'delete' ? 'trash' : 'approve') as 'approve' | 'trash';
		if (action === 'trash' && !confirm(`Move ${ids.length} activit${ids.length === 1 ? 'y' : 'ies'} to trash? They'll be permanently deleted after 2 days — restore anytime before then.`))
			return;

		applyBtn.disabled = true;
		if (await postReview(action, { trackIds: ids })) window.location.reload();
		else applyBtn.disabled = false;
	}));

	refresh();
}

function setupRowActions(): void {
	document.querySelectorAll<HTMLButtonElement>('.rec-approve-btn').forEach((btn) => {
		btn.addEventListener('click', asyncCallback('approve recording', async () => {
			const trackId = Number(readData(btn, 'trackId'));
			const card = btn.closest('.rec-review-card');
			const typeSel = card?.querySelector<HTMLSelectElement>('.rec-review-type');
			btn.disabled = true;
			// rec-review-type <option>s are server-rendered from ActivityTypeVocabulary.Recordable
			// (plus an LR-06 passthrough option for an out-of-list detected value).
			const activityType = typeSel?.value as RecordableActivityType | undefined;
			const body = { trackIds: [trackId], activityType };
			if (await postReview('approve', body)) window.location.reload();
			else btn.disabled = false;
		}));
	});

	document.querySelectorAll<HTMLButtonElement>('.rec-delete-btn').forEach((btn) => {
		btn.addEventListener('click', asyncCallback('trash recording', async () => {
			const trackId = Number(readData(btn, 'trackId'));
			if (!confirm('Move this activity to trash? It will be permanently deleted after 2 days — restore anytime before then.')) return;
			btn.disabled = true;
			if (await postReview('trash', { trackIds: [trackId] })) window.location.reload();
			else btn.disabled = false;
		}));
	});

	document.querySelectorAll<HTMLButtonElement>('.rec-restore-btn').forEach((btn) => {
		btn.addEventListener('click', asyncCallback('restore recording', async () => {
			const trackId = Number(readData(btn, 'trackId'));
			btn.disabled = true;
			if (await postReview('restore', { trackIds: [trackId] })) window.location.reload();
			else btn.disabled = false;
		}));
	});
}

document.addEventListener('DOMContentLoaded', () => {
	// Either surface can render alone: a first-time user has only review cards, and a user who has approved
	// everything has only the sessions table.
	if (!document.querySelector('.rec-sessions-table, .rec-review-cards')) return;
	formatStartedColumn(); // PI-03: local-time "Started" column
	setupMinimaps(); // review cards (pending + trashed)
	setupSelection(); // no-op when the bulk toolbar is hidden (no pending cards)
	setupRowActions(); // approve / trash / restore
});
