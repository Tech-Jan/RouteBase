/**
 * Deleting races from the races list — one row at a time, or a checked selection in one Apply.
 *
 * A race delete is FINAL: there is no race trash, and the cascade takes the checkpoints, the field and
 * every split with it (the course activity itself is untouched — the race only points at it). So the
 * verb always asks first, and the prompt names what is about to go.
 */

import { readData } from '../core/data-attrs';
import { must } from '../core/invariant';
import { deleteRace } from './race-api';

export interface RaceListEntry {
	readonly raceId: number;
	readonly name: string;
	readonly participantCount: number;
}

/** What the confirm dialog says. Pure — the wording is the tested part, not the dialog. */
export function deleteConfirmText(races: readonly RaceListEntry[]): string {
	const carried = 'Checkpoints and results go with it. This cannot be undone.';
	if (races.length === 1) {
		const race = must(races[0], 'length === 1 (guarded)');
		const riders = race.participantCount > 0 ? ` and its ${race.participantCount} riders` : '';
		return `Delete the race “${race.name}”${riders}?\n\n${carried}`;
	}
	const riders = races.reduce((sum, race) => sum + race.participantCount, 0);
	const withRiders = riders > 0 ? ` and their ${riders} riders` : '';
	return `Delete ${races.length} races${withRiders}?\n\n${carried}`;
}

/** Reads a row's entry off the checkbox that carries it — the list is server-rendered, so the DOM is the model. */
function entryOf(element: HTMLElement): RaceListEntry | null {
	const raceId = Number(readData(element, 'raceId'));
	if (!Number.isFinite(raceId)) return null;
	return {
		raceId,
		name: readData(element, 'raceName') ?? 'this race',
		participantCount: Number(readData(element, 'participantCount')) || 0,
	};
}

export interface RaceListDeleteOptions {
	readonly root: ParentNode;
	/** The list is a lie once a race is gone — the page re-renders from the server. */
	readonly onDeleted: () => void;
	readonly onError?: (message: string) => void;
	/** Injected so the confirm and the write can both be driven in a test. */
	readonly confirm?: (message: string) => boolean;
	readonly remove?: (raceId: number) => Promise<unknown>;
	/**
	 * Which checkboxes count as selectable — defaults to all. The card list filters cards in/out, and a
	 * hidden card must not be swept up by Select all / Delete selected: the page passes a predicate that
	 * excludes checkboxes inside a filtered-out (`.is-hidden`) card.
	 */
	readonly isSelectable?: (check: HTMLInputElement) => boolean;
}

export function initRaceListDelete(options: RaceListDeleteOptions): void {
	const { root } = options;
	const ask = options.confirm ?? ((message: string) => window.confirm(message));
	const remove = options.remove ?? deleteRace;
	const isSelectable = options.isSelectable ?? (() => true);

	// Only the selectable (visible) checkboxes: select-all, the count and the indeterminate math all read this.
	const checks = () => Array.from(root.querySelectorAll<HTMLInputElement>('.race-row-check')).filter(isSelectable);
	const selected = () => checks().filter((c) => c.checked).map(entryOf).filter((e): e is RaceListEntry => e !== null);

	const selectAll = root.querySelector<HTMLInputElement>('#raceSelectAll');
	const deleteButton = root.querySelector<HTMLButtonElement>('#raceDeleteSelected');
	const label = root.querySelector<HTMLElement>('#raceSelectionLabel');

	function syncToolbar(): void {
		const count = selected().length;
		if (deleteButton) deleteButton.disabled = count === 0;
		if (label) label.textContent = count === 0 ? 'Select all' : `${count} selected`;
		if (selectAll) {
			const all = checks();
			selectAll.checked = all.length > 0 && count === all.length;
			selectAll.indeterminate = count > 0 && count < all.length;
		}
	}

	// Sequential, not Promise.all: a bulk delete of a checked set is a handful of rows, and one 403 should
	// not leave the rest of the batch racing an already-failing request.
	async function deleteAll(races: readonly RaceListEntry[]): Promise<void> {
		if (races.length === 0 || !ask(deleteConfirmText(races))) return;

		if (deleteButton) deleteButton.disabled = true;

		// Count what actually went, because a PARTIAL failure still has to refresh the list. Bailing out of
		// the loop skipped onDeleted() altogether: races 1-2 were gone from the database and still on the
		// page, and clicking one of their trash buttons again produced a second, stranger error about a
		// race that no longer existed.
		let deleted = 0;
		try {
			for (const race of races) {
				await remove(race.raceId);
				deleted += 1;
			}
			options.onDeleted();
		} catch (error) {
			options.onError?.((error as Error).message);

			// If anything was removed, the page is now LYING about what exists, and a stale list is worse
			// than a lost status line — so refresh. (onDeleted reloads, which does discard the message
			// above; the alternative is leaving deleted races on screen, which is the bug being fixed.)
			// Nothing removed → nothing is stale, so keep the error on screen.
			if (deleted > 0) options.onDeleted();
			else syncToolbar();
		}
	}

	for (const check of checks()) check.addEventListener('change', syncToolbar);

	selectAll?.addEventListener('change', () => {
		for (const check of checks()) check.checked = selectAll.checked;
		syncToolbar();
	});

	deleteButton?.addEventListener('click', () => void deleteAll(selected()));

	for (const button of root.querySelectorAll<HTMLButtonElement>('.race-row-delete')) {
		const entry = entryOf(button);
		if (entry) button.addEventListener('click', () => void deleteAll([entry]));
	}

	syncToolbar();
}
