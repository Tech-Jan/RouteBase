// version-history.ts — the Details page's "Version history" panel (owner only).
//
// THE one place a ride's version changes (EditorSurfaces). Three actions, three scopes, and the panel's whole
// job is to keep them distinguishable:
//   · Restore a version   — moves THIS ride's points back. Nothing else is touched. Appends to history.
//   · Undo this save      — also deletes the rides that save created and untrashes the ones it swallowed. The
//                           only thing that can un-merge or un-split. Offered per row (version-row.ts).
//   · Reset to original   — back to the ride's first version, history emptied. The one irreversible action.
//
// There is deliberately no second surface. Every one of these moves the head, and the server refuses a head
// move while the ride is checked out — so an in-editor copy of this panel could never work, and used to sit
// there 409ing. While the ride IS checked out, the actions render disabled and say which edit is holding it.
//
// Also hosts Park/Release: same axis (taking a ride out of circulation without deleting it).

import { readData } from '../core/data-attrs';
import {
	canResetToOriginal, hasHistory, hasHistoryFrom, headMoveRefusal, parkLabel, type VersionItem,
} from './version-history-model';
import { renderVersionList } from './version-row';

interface PanelState {
	trackId: number;
	checkedOut: boolean;
	/** Versions the ride has, as loaded — NOT the rows on screen: the list collapses restores away by default,
	 * and the reset confirmation must state how many versions it is really about to delete. */
	versionCount: number;
}

import type { TrackEditErrorBody, VersionsListResponse } from '../generated/contracts';

function statusEl(): HTMLElement | null {
	return document.getElementById('versionStatus');
}

async function loadVersions(state: PanelState): Promise<void> {
	const list = document.getElementById('versionList') as HTMLUListElement | null;
	const status = statusEl();
	const actions = document.getElementById('versionPanelActions');
	if (!list || !status) return;

	let items: VersionItem[];
	try {
		const resp = await fetch(`/api/activities/${state.trackId}/versions`);
		if (!resp.ok) throw new Error(String(resp.status));
		const body: VersionsListResponse = await resp.json();
		items = body.versions;
	} catch {
		status.textContent = 'Could not load the version history.';
		return;
	}

	state.versionCount = items.length;
	list.replaceChildren();
	document.getElementById('versionExpander')?.remove();
	if (!hasHistory(items)) {
		showNothingToShow();
		return;
	}
	status.hidden = true;

	// Two different questions (version-history-model): a freshly merged ride HAS a history — its one version is
	// the merge, and undoing that is the only un-merge — but it has nothing to RESET, being already at its first
	// version. Offering Reset there would be an inert button.
	if (actions) actions.hidden = !canResetToOriginal(items);

	const expander = renderVersionList(list, items, {
		checkedOut: state.checkedOut,
		onRestore: (version) => void restore(state, version),
		onUndoSave: (commitId) => void undoSave(state, commitId),
	});
	if (expander) {
		expander.id = 'versionExpander';
		list.after(expander);
	}
}

async function restore(state: PanelState, v: VersionItem): Promise<void> {
	// Restoring appends to history rather than rewriting it — the version you are leaving stays on record, so
	// this is undoable by restoring that one in turn. Worth saying, because "restore" usually is not.
	if (!window.confirm(`Restore v${v.seq}? Your current version stays in the history, so you can come back to it.`))
		return;

	// try/catch: onRestore fires this as `void restore(...)` — a network failure would otherwise die
	// as an unhandled rejection with zero feedback right after a confirmed action.
	let resp: Response;
	try {
		resp = await fetch(`/api/activities/${state.trackId}/versions/${v.versionId}/restore`, { method: 'POST' });
	} catch {
		window.alert('Could not restore this version — check the connection and try again.');
		return;
	}
	if (resp.ok) {
		window.location.reload();
		return;
	}
	window.alert(await refusal(resp, 'Could not restore this version.'));
}

/**
 * Undo the save that wrote this version — the un-merge / un-split. Its consequences reach other rides, so the
 * confirmation is the server's own summary of them (names, what gets deleted, what gets untrashed), not a
 * sentence we compose here. That dialog also handles the case where the revert deletes the ride we are looking
 * at, which is why the navigation is its business and not ours.
 */
async function undoSave(state: PanelState, commitId: number): Promise<void> {
	const { openRevertFlow } = await import('../track-edit/edit-dialogs');
	await openRevertFlow(commitId, { trackId: state.trackId });
}

/**
 * Back to the points the ride started with, history emptied. The one irreversible thing in a system that is
 * otherwise append-only — so it confirms with the cost stated plainly, and it does not un-merge: a merge
 * product's first version IS the merged content, and undoing THAT is the row's "Undo this save".
 */
async function resetToOriginal(state: PanelState): Promise<void> {
	const edits = state.versionCount - 1;
	const confirmed = window.confirm(
		`Put this ride back to the points it started with? Its ${edits} later ` +
		`${edits === 1 ? 'version' : 'versions'} are deleted and the history is emptied — the edits cannot be ` +
		'restored, and the saves that made them cannot be undone.');
	if (!confirmed) return;

	// try/catch mirrors restore(): the user just accepted an irreversible dialog — a network failure
	// must say whether anything happened, not vanish as an unhandled rejection.
	let resp: Response;
	try {
		resp = await fetch(`/api/activities/${state.trackId}/versions/reset`, { method: 'POST' });
	} catch {
		window.alert('Could not reset this ride — check the connection and reload to see its current state.');
		return;
	}
	if (resp.ok) {
		window.location.reload();
		return;
	}
	window.alert(await refusal(resp, 'Could not reset this ride.'));
}

/** A head move has two expected refusals, and they send the user to different places — so read the code rather
 * than assuming (the panel used to call every 409 a live recording). The wording is shared, in the model. */
async function refusal(resp: Response, fallback: string): Promise<string> {
	const code = ((await resp.json().catch(() => null)) as Partial<TrackEditErrorBody> | null)?.code;
	return headMoveRefusal(code) ?? fallback;
}

async function togglePark(trackId: number, lifecycle: string): Promise<void> {
	const action = lifecycle === 'released' ? 'park' : 'release';
	const resp = await fetch(`/api/activities/${trackId}/${action}`, { method: 'POST' });
	if (resp.ok) window.location.reload();
	else window.alert(action === 'park' ? 'Could not park this ride.' : 'Could not release this ride.');
}

function init(): void {
	const root = document.getElementById('versionHistoryPanel');
	if (!root) return;   // not the owner: the panel is not rendered at all

	const trackId = Number(readData(root, 'trackId'));
	if (!Number.isFinite(trackId)) return;
	const state: PanelState = { trackId, checkedOut: readData(root, 'checkedOut') === 'true', versionCount: 0 };
	const lifecycle = readData(root, 'lifecycle') ?? 'released';

	// The server already answered "is there anything to show?" (one indexed seek, rendered into the panel). On a
	// never-edited ride — most of them — that is NO, and the whole /versions request is skipped: the panel goes
	// straight to its empty state instead of round-tripping to learn it has nothing.
	// Absent/garbled facts must NOT read as "0 versions" — null falls back to asking the server.
	const serverCountAttr = readData(root, 'versionCount');
	const serverCount = serverCountAttr === null ? Number.NaN : Number(serverCountAttr);
	const serverAnyCommit = readData(root, 'anyCommit') === 'true';
	const worthFetching = !Number.isFinite(serverCount) || hasHistoryFrom(serverCount, serverAnyCommit);

	if (state.checkedOut) {
		const notice = document.getElementById('versionCheckedOutNotice');
		if (notice) notice.hidden = false;
	}

	const parkBtn = document.getElementById('parkToggleBtn') as HTMLButtonElement | null;
	const label = parkLabel(lifecycle);
	if (parkBtn && label) {
		parkBtn.textContent = label;
		parkBtn.hidden = false;
		parkBtn.addEventListener('click', () => void togglePark(trackId, lifecycle));
	}

	const resetBtn = document.getElementById('versionResetBtn') as HTMLButtonElement | null;
	if (resetBtn) {
		if (state.checkedOut) {
			resetBtn.disabled = true;
			resetBtn.title = 'This ride has an edit open — resume or discard it first';
		} else {
			resetBtn.addEventListener('click', () => void resetToOriginal(state));
		}
	}

	if (worthFetching) void loadVersions(state);
	else showNothingToShow();
}

/** The empty state, without a request. Same words loadVersions would have reached after the round-trip. */
function showNothingToShow(): void {
	const status = statusEl();
	if (!status) return;
	status.textContent = 'This ride has not been edited. Its original points are what you see.';
	status.hidden = false;
}

if (typeof document !== 'undefined') {
	if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
	else init();
}
