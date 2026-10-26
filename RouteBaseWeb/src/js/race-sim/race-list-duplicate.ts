/**
 * "Save as" from the races list — deep-copy a row's race into a new one you own.
 *
 * The mirror of race-list-delete, minus the danger: a copy destroys nothing, so it asks nothing first.
 * The server names the copy and the page re-renders from the new row rather than the client splicing it in.
 */

import { asyncCallback } from '../core/async-listener';
import { readData } from '../core/data-attrs';
import { duplicateRace } from './race-api';

export interface RaceListDuplicateOptions {
	readonly root: ParentNode;
	/** The copy is a new server-rendered row — re-render rather than keep a second copy of the truth. */
	readonly onDuplicated: () => void;
	readonly onError?: (message: string) => void;
	/** Injected so the write can be driven in a test. */
	readonly duplicate?: (raceId: number) => Promise<unknown>;
}

export function initRaceListDuplicate(options: RaceListDuplicateOptions): void {
	const copy = options.duplicate ?? duplicateRace;

	for (const button of options.root.querySelectorAll<HTMLButtonElement>('.race-row-duplicate')) {
		const raceId = Number(readData(button, 'raceId'));
		if (!Number.isFinite(raceId)) continue;
		button.addEventListener('click', asyncCallback('duplicate race', async () => {
			// Guard against a double-submit — a second copy is a second race, not a no-op.
			button.disabled = true;
			try {
				await copy(raceId);
				options.onDuplicated();
			} catch (error) {
				button.disabled = false;
				options.onError?.((error as Error).message);
			}
		}));
	}
}
