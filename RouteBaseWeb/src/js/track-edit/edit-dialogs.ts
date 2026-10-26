/**
 * edit-dialogs.ts — the save / discard / stale / undo-a-save dialogs (A3 §5, §6). This module is only ever
 * DYNAMIC-imported (from the controller for save/discard, from the Details history panel for undo-a-save), so
 * it may pull edit-api directly without weighing on a view-only Details visit. Consequence lists render
 * VERBATIM from the server DTO (names/slots are server-computed — the dialog never re-derives the mapping,
 * §5.1). All user text goes through textContent (no innerHTML) so names are safe by construction.
 */

import { TRACK_EDIT_API_ERRORS, type SavePreflightResponse, type SaveMode, type CommitSummaryResponse } from './edit-session-api';
import { asyncCallback } from '../core/async-listener';
import { EditApiError, getCommitSummary, revertCommit } from './edit-api';
import { actionButton, actionsRow, buildDialog, radio, type DialogHandle } from './track-edit-dom';

export type { DialogHandle };

// ---- save dialog ----

export interface SaveDialogDeps {
	fetchPreflight(mode: SaveMode): Promise<SavePreflightResponse>;
	onConfirm(mode: SaveMode): void;
	onCancel?(): void;
}

/** Renders the exact Replace/Copy consequences (or a blocked state) from a preflight DTO, verbatim. */
export function renderConsequences(preflight: SavePreflightResponse): HTMLElement {
	const wrap = document.createElement('div');
	if (preflight.blockedBy) {
		const error = document.createElement('div');
		error.className = 'track-edit-dialog-error';
		error.textContent = preflight.blockedBy === 'nothingToSave'
			? 'No changes to save.'
			: 'This session can no longer be saved.';
		wrap.appendChild(error);
		return wrap;
	}
	wrap.appendChild(paragraph('This will:'));
	const list = document.createElement('ul');
	for (const u of preflight.updated) list.appendChild(item(`update “${u.name}”`));
	for (const c of preflight.created) list.appendChild(item(`create “${c.name}”`));
	for (const t of preflight.trashed) list.appendChild(item(`move “${t.name}” to trash`));
	wrap.appendChild(list);
	wrap.appendChild(note('Previous versions are kept — revertable from this page.'));
	return wrap;
}

let saveDialogSeq = 0;

export function openSaveDialog(deps: SaveDialogDeps): DialogHandle {
	const dialog = buildDialog('Save changes');
	const modes = document.createElement('div');
	modes.className = 'track-edit-dialog-modes';
	// The radio group name must be unique per dialog: a fixed one makes two open dialogs share ONE group, so
	// picking a mode in the second silently unchecks the first's radios.
	const group = `trackEditSaveMode-${++saveDialogSeq}`;
	const replace = radio(group, 'replace', true, 'Replace (default) — update this activity in place');
	const copy = radio(group, 'copy', false, 'Save as copy — keep the originals untouched');
	modes.append(replace.label, copy.label);
	dialog.body.appendChild(modes);

	const host = document.createElement('div');
	dialog.body.appendChild(host);

	const cancelBtn = actionButton('Cancel', 'btn-cockpit-ghost');
	const saveBtn = actionButton('Save', 'btn-cockpit-primary');
	dialog.body.appendChild(actionsRow(cancelBtn, saveBtn));

	let mode: SaveMode = 'replace';
	async function loadPreflight(next: SaveMode): Promise<void> {
		mode = next;
		saveBtn.disabled = true;
		try {
			const preflight = await deps.fetchPreflight(next);
			host.replaceChildren(renderConsequences(preflight));
			saveBtn.disabled = Boolean(preflight.blockedBy);
		} catch {
			host.replaceChildren(errorNode('Could not compute the save preview.'));
			saveBtn.disabled = true;
		}
	}

	replace.input.addEventListener('change', () => { if (replace.input.checked) void loadPreflight('replace'); });
	copy.input.addEventListener('change', () => { if (copy.input.checked) void loadPreflight('copy'); });
	const dismiss = (): void => { dialog.close(); deps.onCancel?.(); };
	cancelBtn.addEventListener('click', dismiss);
	dialog.onEscape(dismiss); // Escape must clear the same re-entrancy guard Cancel does (onCancel)
	saveBtn.addEventListener('click', () => { dialog.close(); deps.onConfirm(mode); });

	void loadPreflight('replace');
	return { el: dialog.backdrop, close: dialog.close };
}

// ---- discard dialog ----

export interface DiscardDialogDeps {
	editCount: number;
	activityName?: string;
	onConfirm(): void;
	onCancel?(): void;
}

export function openDiscardDialog(deps: DiscardDialogDeps): DialogHandle {
	const dialog = buildDialog('Discard edits');
	const subject = deps.activityName ? `“${deps.activityName}”` : 'The activity';
	dialog.body.appendChild(paragraph(
		`Discard all ${deps.editCount} edit${deps.editCount === 1 ? '' : 's'}? ${subject} stays unchanged.`));

	const keepBtn = actionButton('Keep editing', 'btn-cockpit-ghost');
	const discardBtn = actionButton('Discard', 'btn-cockpit-danger');
	dialog.body.appendChild(actionsRow(keepBtn, discardBtn));
	const dismiss = (): void => { dialog.close(); deps.onCancel?.(); };
	keepBtn.addEventListener('click', dismiss);
	dialog.onEscape(dismiss);
	discardBtn.addEventListener('click', () => { dialog.close(); deps.onConfirm(); });
	return { el: dialog.backdrop, close: dialog.close };
}

// ---- stale dialog (save-time 409 staleTrack / resume with stale stamps, §6) ----

export interface StaleDialogDeps {
	onDiscard(): void;
	onKeep(): void;
}

export function openStaleDialog(deps: StaleDialogDeps): DialogHandle {
	const dialog = buildDialog('Track changed');
	dialog.body.appendChild(paragraph(
		'This track changed since the session started (an elevation refresh or new data). It can’t be saved.'));
	const keepBtn = actionButton('Keep open', 'btn-cockpit-ghost');
	const discardBtn = actionButton('Discard session', 'btn-cockpit-danger');
	dialog.body.appendChild(actionsRow(keepBtn, discardBtn));
	// Escape = the non-destructive dismiss (Keep open), never Discard session.
	const dismiss = (): void => { dialog.close(); deps.onKeep(); };
	keepBtn.addEventListener('click', dismiss);
	dialog.onEscape(dismiss);
	discardBtn.addEventListener('click', () => { dialog.close(); deps.onDiscard(); });
	return { el: dialog.backdrop, close: dialog.close };
}

// ---- undo a save (revert a commit) — opened from the Details history panel ----

export interface RevertFlowHooks {
	/** The ride whose page this is. A revert DELETES the rides its commit created, and a split/merge product's
	 * own history offers that revert — so the page can be reverting itself out of existence. */
	trackId: number;
}

/** Where we land when the revert deletes the ride we are looking at. */
const ACTIVITIES_URL = '/Activities';

/** Load the commit summary, open the revert dialog, and reload on a successful revert. */
export async function openRevertFlow(commitId: number, hooks: RevertFlowHooks): Promise<void> {
	let summary: CommitSummaryResponse;
	try {
		summary = await getCommitSummary(commitId);
	} catch {
		return;
	}
	const dialog = buildDialog('Undo this save');
	if (!summary.revertible) {
		dialog.body.appendChild(paragraph(notRevertibleMessage(summary)));
		const closeBtn = actionButton('Close', 'btn-cockpit-ghost');
		dialog.body.appendChild(actionsRow(closeBtn));
		closeBtn.addEventListener('click', dialog.close);
		return;
	}

	dialog.body.appendChild(paragraph('Reverting will:'));
	const list = document.createElement('ul');
	for (const r of summary.replaced) list.appendChild(item(`restore “${r.name}” to its pre-edit track`));
	for (const c of summary.created) list.appendChild(item(`delete “${c.name}”`));
	for (const t of summary.trashed) list.appendChild(item(`untrash “${t.name}”`));
	dialog.body.appendChild(list);

	// This ride is one the commit created (a split/merge product), so the revert deletes the page we are on.
	// Say so before the click, and afterwards leave rather than reload — the activity is gone, and a reload
	// would land on a 404.
	const deletesThisRide = summary.created.some((c) => c.trackId === hooks.trackId);
	if (deletesThisRide)
		dialog.body.appendChild(paragraph('This ride is one of them — you will be taken back to your activities.'));

	const cancelBtn = actionButton('Cancel', 'btn-cockpit-ghost');
	const revertBtn = actionButton('Revert', 'btn-cockpit-danger');
	dialog.body.appendChild(actionsRow(cancelBtn, revertBtn));
	cancelBtn.addEventListener('click', dialog.close);
	revertBtn.addEventListener('click', asyncCallback('revert commit', async () => {
		revertBtn.disabled = true;
		try {
			await revertCommit(commitId);
			if (deletesThisRide) window.location.assign(ACTIVITIES_URL);
			else window.location.reload();
		} catch (err) {
			// inputsPurged: one of this merge's inputs was reclaimed between opening the dialog and
			// clicking Revert (a race with the retention sweep) — a retry can't succeed, so say so and
			// leave the button disabled rather than inviting one (J).
			const purged = err instanceof EditApiError && err.code === TRACK_EDIT_API_ERRORS.inputsPurged;
			revertBtn.disabled = purged;
			dialog.body.appendChild(errorNode(purged
				? 'This merge can no longer be reverted — one of its inputs has since been purged and cannot be restored.'
				: 'Revert failed — please try again.'));
		}
	}));
}

/** Why a commit can no longer be reverted. inputsPurged (J) gets its own copy — unlike the other
 * blockedBy reasons, "try again" or "a newer edit supersedes it" would both be actively wrong: the inputs
 * are gone for good, not superseded or temporarily unavailable. */
function notRevertibleMessage(summary: CommitSummaryResponse): string {
	if (summary.revertedAt) return 'This edit was already reverted.';
	if (summary.blockedBy === TRACK_EDIT_API_ERRORS.inputsPurged)
		return 'This merge can no longer be reverted — one of its inputs has since been purged and cannot be restored.';
	return 'This edit can no longer be reverted (a newer edit supersedes it).';
}

// ---- DOM helpers ----

function paragraph(text: string): HTMLElement {
	const p = document.createElement('p');
	p.textContent = text;
	return p;
}

function note(text: string): HTMLElement {
	const p = paragraph(text);
	p.className = 'track-edit-dialog-note';
	return p;
}

function item(text: string): HTMLElement {
	const li = document.createElement('li');
	li.textContent = text;
	return li;
}

function errorNode(text: string): HTMLElement {
	const el = paragraph(text);
	el.className = 'track-edit-dialog-error';
	return el;
}
