// version-row.ts — one row of a ride's history, as DOM. Pure rendering: no fetch, no reload, no confirm.
//
// One caller now (the Details history panel). It stays its own module because the row is where two different
// undos sit side by side and must not be confused for each other:
//   · Restore  — moves THIS ride's points back. Nothing else is touched.
//   · Undo this save — also deletes the rides that save created and untrashes the ones it swallowed. The only
//     thing that can un-merge or un-split, and it is offered on the row whose version that save wrote.
// What a click DOES is handed back through the callbacks; this module only decides what a row may offer.

import {
	canRestore, canUndoSave, collapseVersions, versionStats, versionSubtitle, versionWhen, type VersionItem,
} from './version-history-model';

export interface VersionRowOptions {
	/** Called when the user asks to restore this version. Absent = render the row read-only. */
	onRestore?: (v: VersionItem, row: HTMLElement, button: HTMLButtonElement) => void;
	/** Called when the user asks to undo the save that wrote this version. Absent = never offered. */
	onUndoSave?: (commitId: number, v: VersionItem) => void;
	/** The ride is checked out: the server refuses every head move, so the buttons render disabled and say why
	 * rather than firing a request that can only 409. */
	checkedOut?: boolean;
}

/**
 * Fill `list` with the versions worth showing, and return an expander for the rest (null when nothing is
 * folded). The caller places the expander — it sits after the list, and is not itself a version.
 *
 * Collapsing, not deleting: a long history is a display problem. Versions share content by SHA, so the rows
 * folded here cost nothing to keep, and keeping them is what lets any of them still be restored.
 */
export function renderVersionList(
	list: HTMLUListElement, items: readonly VersionItem[], opts: VersionRowOptions = {},
): HTMLButtonElement | null {
	const { visible, hidden, noOpCount } = collapseVersions(items);
	list.replaceChildren(...visible.map((v) => buildVersionRow(v, opts)));
	if (hidden.length === 0) return null;

	const expander = document.createElement('button');
	expander.type = 'button';
	expander.className = 'version-expander';
	expander.textContent = expanderLabel(items.length, hidden.length, noOpCount);
	expander.addEventListener('click', () => {
		// Re-render the whole list in version order rather than appending the hidden tail — the folded rows are
		// interleaved with the visible ones, not older than all of them.
		list.replaceChildren(...items.map((v) => buildVersionRow(v, opts)));
		expander.remove();
	});
	return expander;
}

function expanderLabel(total: number, hiddenCount: number, noOpCount: number): string {
	const restores = noOpCount > 0
		? `, incl. ${noOpCount} ${noOpCount === 1 ? 'restore that changed nothing' : 'restores that changed nothing'}`
		: '';
	return `Show all ${total} versions (${hiddenCount} hidden${restores})`;
}

const CHECKED_OUT_TITLE = 'This ride has an edit open — resume or discard it first';

export function buildVersionRow(v: VersionItem, opts: VersionRowOptions = {}): HTMLLIElement {
	const row = document.createElement('li');
	row.className = 'version-row' + (v.isHead ? ' version-row--head' : '');

	const main = document.createElement('div');
	main.className = 'version-row-main';

	const title = document.createElement('div');
	title.className = 'version-row-title';
	title.textContent = v.isHead ? `v${v.seq} — current` : `v${v.seq}`;

	const when = document.createElement('span');
	when.className = 'version-row-when';
	when.textContent = versionWhen(v);
	if (when.textContent) title.append(' ', when);

	const sub = document.createElement('div');
	sub.className = 'version-row-sub';
	sub.textContent = versionSubtitle(v);

	const stats = document.createElement('div');
	stats.className = 'version-row-stats';
	stats.textContent = versionStats(v);

	main.append(title, sub, stats);
	row.append(main);

	if (opts.onRestore && canRestore(v)) {
		const handler = opts.onRestore;
		row.append(action('Restore', opts.checkedOut, (btn) => handler(v, row, btn)));
	}

	// The un-merge / un-split door, on the row whose version the save wrote — so a merge product's v1 carries
	// its own, and cannot lose it the way a single `edited_commit_id` badge could.
	if (opts.onUndoSave && canUndoSave(v)) {
		const handler = opts.onUndoSave;
		const commitId = v.commitId as number;   // canUndoSave is false when it is null
		row.append(action('Undo this save…', opts.checkedOut, () => handler(commitId, v)));
	}
	return row;
}

function action(label: string, checkedOut: boolean | undefined, onClick: (btn: HTMLButtonElement) => void): HTMLButtonElement {
	const btn = document.createElement('button');
	btn.type = 'button';
	btn.className = 'btn-cockpit btn-cockpit-ghost btn-cockpit-sm';
	btn.textContent = label;
	if (checkedOut) {
		btn.disabled = true;
		btn.title = CHECKED_OUT_TITLE;
	} else {
		btn.addEventListener('click', () => onClick(btn));
	}
	return btn;
}
