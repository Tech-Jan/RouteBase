/**
 * Activities grid — the "Export GPX (N)" header button.
 *
 * N is how many of the table's current matches the user OWNS (`ownedFilteredCount` off every grid
 * response), because an export never contains anyone else's rides — a button reading the plain match
 * count would promise rides that cannot be in the ZIP.
 *
 * The click only ENQUEUES. Zipping a few hundred rides takes minutes, so the work runs on the operation
 * queue and the global op-toast owns progress and the download from there — which is what lets the user
 * leave this page while their file is built. The ids sent are the ones the table already resolved
 * (`filteredIds`, all pages, shape-filter included); the server intersects them with ownership itself.
 */

import { startExportPolling } from '../core/op-polling-bridge';
import type { ActivityExportRequest, ApiError } from '../generated/contracts';

const ENQUEUE_URL = '/api/activities/export';
const ICON_IDLE = 'bi bi-file-earmark-zip';
const ICON_BUSY = 'bi bi-hourglass-split';

export interface ExportController {
	/** Re-point the button at the table's current matches. `owned` is the count that goes in the label. */
	setFiltered(ids: readonly number[], owned: number): void;
}

export function createExportController(buttonId: string): ExportController {
	const el = document.getElementById(buttonId);
	if (!(el instanceof HTMLButtonElement)) return { setFiltered: () => {} };
	const btn: HTMLButtonElement = el;

	const icon = btn.querySelector('i');
	const label = btn.querySelector('.act-export-label');
	let filteredIds: readonly number[] = [];
	let ownedCount = 0;
	let enqueuing = false;

	function paint(): void {
		if (enqueuing) return; // the busy state owns the button until the enqueue resolves
		btn.disabled = ownedCount === 0;
		if (icon) icon.className = ICON_IDLE;
		if (label) label.textContent = `Export GPX (${ownedCount})`;
		btn.title = ownedCount === 0
			? 'None of the activities shown in the table are yours to export'
			: `Build a ZIP of the ${ownedCount} activities you own in the current table filter`;
	}

	function setBusy(busy: boolean): void {
		enqueuing = busy;
		btn.disabled = busy;
		if (busy) {
			if (icon) icon.className = ICON_BUSY;
			if (label) label.textContent = 'Queueing…';
		} else {
			paint();
		}
	}

	async function enqueue(): Promise<void> {
		if (enqueuing || ownedCount === 0) return;
		setBusy(true);
		try {
			const body: ActivityExportRequest = { trackIds: [...filteredIds] };
			const res = await fetch(ENQUEUE_URL, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify(body),
			});
			// 409 = this user already has an export building. That is not a failure to report — adopt the
			// run and let the toast say what it is already saying, rather than interrupting with a dialog.
			if (res.status === 409) {
				startExportPolling();
				return;
			}
			if (!res.ok) {
				const err = (await res.json().catch(() => ({}))) as Partial<ApiError>;
				throw new Error(err.message ?? `Request failed (${res.status}).`);
			}
			// Hand off to the op-toast: it polls /api/queue/progress and turns into a Download button when
			// the ZIP lands, on whatever page the user happens to be on by then.
			startExportPolling();
		} catch (err) {
			const detail = err instanceof Error && err.message ? ` (${err.message})` : '';
			alert(`Could not start the export${detail}.`);
		} finally {
			setBusy(false);
		}
	}

	btn.addEventListener('click', () => void enqueue());
	paint();

	return {
		setFiltered(ids: readonly number[], owned: number): void {
			filteredIds = ids;
			ownedCount = owned;
			paint();
		},
	};
}
