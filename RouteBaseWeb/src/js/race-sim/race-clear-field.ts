/**
 * "Clear field" — empty a race of every rider so a corrected results CSV can be imported clean.
 *
 * Destructive and owner-only, so it asks first and says what it removed. The course, the checkpoints and
 * the race itself survive: this deletes the FIELD, not the race (that lives on the races list).
 */

import { asyncCallback } from '../core/async-listener';
import { clearField } from './race-api';

export interface RaceClearFieldOptions {
	readonly raceId: number;
	readonly button: HTMLElement;
	/** The field is gone — the page's view of it is now a lie. */
	readonly onCleared: () => void;
	/** Injected so the confirm can be driven in a test. */
	readonly confirm?: (message: string) => boolean;
	readonly onError?: (message: string) => void;
	/** The write itself. Injected so the refusal path is testable without mocking the module. */
	readonly clear?: (raceId: number) => Promise<unknown>;
}

export function initRaceClearField(options: RaceClearFieldOptions): void {
	const { raceId, button } = options;
	const ask = options.confirm ?? ((message: string) => window.confirm(message));
	const clear = options.clear ?? clearField;

	button.addEventListener('click', asyncCallback('clear race field', async () => {
		if (!ask('Remove every rider from this race? The course and its checkpoints are kept.')) return;

		button.setAttribute('disabled', 'disabled');
		try {
			await clear(raceId);
			options.onCleared();
		} catch (error) {
			options.onError?.((error as Error).message);
			button.removeAttribute('disabled');
		}
	}));
}
