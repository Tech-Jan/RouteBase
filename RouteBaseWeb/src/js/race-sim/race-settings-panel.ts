/**
 * The owner-only "Who can edit" control — a compact map-toolbar dropdown (see Replay.cshtml), not a panel.
 * Picking an option PUTs the policy; on success the summary label follows, on failure the menu stays open
 * with the reason and the choice is rolled back. A no-op when the dropdown is absent (a non-owner page).
 */

import { asyncCallback } from '../core/async-listener';
import { readData } from '../core/data-attrs';
import { setEditPolicy, type RaceEditPolicyValue } from './race-api';

function el<T extends HTMLElement>(id: string): T | null {
	return document.getElementById(id) as T | null;
}

export function initRaceSettingsPanel(raceId: number): void {
	const dropdown = el<HTMLDetailsElement>('raceEditPolicy');
	if (!dropdown) return;   // not the owner — the control isn't rendered

	const valueEl = el('raceEditPolicyValue');
	const msgEl = el('raceEditPolicyMsg');
	const items = Array.from(dropdown.querySelectorAll<HTMLButtonElement>('.map-dd-item[data-policy]'));

	function say(message: string, isError: boolean): void {
		if (!msgEl) return;
		msgEl.textContent = message;
		msgEl.classList.toggle('is-error', isError);
		msgEl.hidden = false;
	}

	function markActive(chosen: HTMLButtonElement): void {
		for (const item of items) item.classList.toggle('active', item === chosen);
		if (valueEl) valueEl.textContent = readData(chosen, 'short') ?? chosen.textContent ?? '';
	}

	for (const item of items) {
		item.addEventListener('click', asyncCallback('set edit policy', async () => {
			const policy = readData(item, 'policy') as RaceEditPolicyValue | undefined;
			if (!policy || item.classList.contains('active')) { dropdown.open = false; return; }

			const previous = items.find((b) => b.classList.contains('active')) ?? null;
			for (const b of items) b.setAttribute('disabled', 'disabled');
			say('Saving…', false);
			try {
				await setEditPolicy(raceId, policy);
				markActive(item);
				dropdown.open = false;
				if (msgEl) msgEl.hidden = true;
			} catch (error) {
				// The write was refused — don't let the UI claim it stuck. Keep the menu open on the reason.
				if (previous) markActive(previous);
				say((error as Error).message, true);
				dropdown.open = true;
			} finally {
				for (const b of items) b.removeAttribute('disabled');
			}
		}));
	}
}
