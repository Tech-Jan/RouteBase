/**
 * edit-mode-entry.ts — the ONE script seam into the Details entry (called from bootDetails). Thin by design:
 * it wires the editor door and the `?editSession=` auto-resume, then DYNAMIC-imports the controller chunk so a
 * view-only visit never pays for the editor (D-U8 — editor modules ride lazy chunks of the existing
 * activity-details-page entry, no new vite entry).
 *
 * THE DOOR IS ONE BUTTON, in the page header, with two states (EditorSurfaces). Checked out is a state of the
 * RIDE, so it is the button that changes, not a banner that appears somewhere else:
 *   · free        → “Edit track”
 *   · checked out → “Resume edit · 3 changes, 5 min ago” + “Discard edit”
 *
 * Both states are SERVER-RENDERED (F-1) — the resume values sit on the button as data-resume-* attributes, so
 * this module issues NO HTTP request at page load. A ride with no open edit has no resume button, and that
 * absence is the whole check.
 *
 * History is NOT here. It is the Details history panel, and only there: a head cannot move while the ride is
 * checked out, so the editor has no history door to offer.
 */

import type { EditModeConfig } from './edit-mode-controller';
import type { ResumeHint } from './edit-session-api';

/** The slice of the details config the editor door needs. A full ActivityDetailsConfig satisfies this
 *  structurally, so the caller (activity-details-page) passes it unchanged — but track-edit no longer
 *  imports the activity type, closing the last track-edit -> activity edge. */
export interface TrackEditEntryConfig {
	isOwner: boolean;
	activityId: number;
	tzOffsetHours: number;
}

export function wireTrackEditEntry(config: TrackEditEntryConfig): void {
	if (!config.isOwner) return; // non-owner never edits (server guards too, invariant 6)
	const editConfig: EditModeConfig = { activityId: config.activityId, tzOffsetHours: config.tzOffsetHours };

	// Flow A redirect / reloaded editor tab: ?editSession= present → resume immediately.
	const sessionId = new URLSearchParams(window.location.search).get('editSession');
	if (sessionId) {
		void enter(editConfig, sessionId);
		return;
	}

	document.getElementById('trackEditEnterBtn')?.addEventListener('click', () => void enter(editConfig));
	wireResume(editConfig);
}

/** The checked-out state of the header: label the Resume button with what is waiting, and wire the abandon. */
function wireResume(editConfig: EditModeConfig): void {
	const hint = readResumeHint();
	if (!hint) return;

	const label = document.getElementById('trackEditResumeLabel');
	if (label) label.textContent = resumeLabel(hint);

	document.getElementById('trackEditResumeBtn')
		?.addEventListener('click', () => void enter(editConfig, hint.sessionId));
	document.getElementById('trackEditDiscardOpenBtn')
		?.addEventListener('click', () => void discardOpenEdit(hint.sessionId));
}

/**
 * Abandon the open edit without entering it. Deleting the session row releases the ride's checkout pointer
 * (ON DELETE SET NULL), so the reload comes back with a plain “Edit track” — and the history panel's actions,
 * which the checkout was blocking, come back with it.
 */
async function discardOpenEdit(sessionId: string): Promise<void> {
	if (!window.confirm('Discard the unsaved edit on this ride? The ride itself stays exactly as it is.')) return;
	const { discardSession } = await import('./edit-api');
	try {
		await discardSession(sessionId);
	} catch {
		// idempotent — already gone is the outcome we wanted
	}
	window.location.reload();
}

/** What is waiting behind Resume. Pure — the button's whole text, so it is testable without a DOM. */
export function resumeLabel(hint: ResumeHint): string {
	const edits = hint.editCount === 1 ? '1 change' : `${hint.editCount} changes`;
	return `Resume edit · ${edits}, ${timeAgo(hint.updatedAt)}`;
}

export function timeAgo(iso: string): string {
	const then = Date.parse(iso);
	if (Number.isNaN(then)) return 'recently';
	const minutes = Math.max(0, Math.round((Date.now() - then) / 60000));
	if (minutes < 1) return 'just now';
	if (minutes < 60) return `${minutes} min ago`;
	const hours = Math.round(minutes / 60);
	if (hours < 24) return `${hours} h ago`;
	return `${Math.round(hours / 24)} d ago`;
}

/** The server already answered “does this ride have an open edit?” — the resume button exists iff it does. */
export function readResumeHint(): ResumeHint | null {
	const el = document.getElementById('trackEditResumeBtn');
	const sessionId = el?.getAttribute('data-resume-session-id');
	const updatedAt = el?.getAttribute('data-resume-updated');
	if (!sessionId || !updatedAt) return null;
	const editCount = Number(el?.getAttribute('data-resume-edits'));
	return { sessionId, editCount: Number.isFinite(editCount) ? editCount : 0, updatedAt };
}

async function enter(editConfig: EditModeConfig, resumeSessionId?: string): Promise<void> {
	const controller = await import('./edit-mode-controller');
	await controller.enterEditMode(editConfig, resumeSessionId);
}
