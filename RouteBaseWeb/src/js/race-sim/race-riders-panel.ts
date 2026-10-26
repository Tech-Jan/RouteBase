/**
 * The real-riders panel: put your own ride, or a friend's, into the field.
 *
 * Adding is gated on VIEW rights, not ownership — racing a friend's ride is the whole point, and the
 * server re-checks those rights on every read, so a revoked friendship drops him from the field even
 * though the row still names him.
 */

import { readApiErrorMessage } from '../core/api-error';
import { asyncCallback } from '../core/async-listener';
import { readData } from '../core/data-attrs';
import { attachSelectFilter } from '../core/select-filter';
import type { RaceCalloutResultDto, RaceParticipantRowDto, RacePaintResultDto } from '../generated/contracts';

function el<T extends HTMLElement>(id: string): T | null {
	return document.getElementById(id) as T | null;
}

/** `onChanged` may be async — a caller that refreshes the replay in place rather than reloading the
 * document has something to await. */
export function initRaceRidersPanel(raceId: number, onChanged: () => void | Promise<void>): void {
	const panel = el('raceRiders');
	if (!panel) return;

	const status = el('raceRiderStatus');

	const riderFilter = el<HTMLInputElement>('raceRiderFilter');
	const riderSelect = el<HTMLSelectElement>('raceRiderActivity');
	if (riderFilter && riderSelect) attachSelectFilter(riderFilter, riderSelect);

	const referenceFilter = el<HTMLInputElement>('raceReferenceFilter');
	const referenceSelect = el<HTMLSelectElement>('raceReferenceActivity');
	if (referenceFilter && referenceSelect) attachSelectFilter(referenceFilter, referenceSelect);

	function say(message: string, isError = false): void {
		if (!status) return;
		status.textContent = message;
		status.className = isError ? 'race-import-status is-error' : 'race-import-status';
	}

	/**
	 * Returns the parsed response body, or null when there isn't one — several of these endpoints answer
	 * with a bodyless Ok, and a caller that doesn't need the body simply ignores it.
	 */
	async function send<T>(url: string, method: 'POST' | 'PUT' | 'DELETE', body?: unknown): Promise<T | null> {
		const response = await fetch(url, {
			method,
			...(body ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}),
		});
		if (response.ok) return (await response.json().catch(() => null)) as T | null;

		// The server's refusals are meaningful — say what it actually said rather than "failed".
		if (response.status === 403) throw new Error('You cannot see that ride.');
		if (response.status === 409) throw new Error('That ride is already in the race.');
		throw new Error(await readApiErrorMessage(response));
	}

	el('raceRiderForm')?.addEventListener('submit', asyncCallback('add race rider', async (event) => {
		event.preventDefault();
		const select = el<HTMLSelectElement>('raceRiderActivity');
		const trackId = Number(select?.value);
		if (!trackId) return;

		say('Adding…');
		try {
			await send(`/api/RacesApi/${raceId}/participants`, 'POST', { trackId, displayName: null });
			await onChanged();
			say('Rider added.');
		} catch (error) {
			say((error as Error).message, true);
		}
	}));

	/**
	 * Point the race at the real effort its cost curve is fitted from.
	 *
	 * The endpoint, the column, the FK and the whole cost-curve cache-key rework existed already — nothing
	 * ever called them, so the feature was unreachable and a merged course (which is what /Races tells you
	 * to build for a multi-day route) had no way off the default curve.
	 *
	 * Empty value = null = "fall back to the course ride", which is exactly what the server means by it.
	 *
	 * Every one of these handlers has to report DONE, because the panel refreshes in place: the pending
	 * message used to be wiped by the document reload, and without one the last thing the user ever sees
	 * is "Setting the pacing reference…" — indistinguishable from a request that hung.
	 */
	el('raceReferenceForm')?.addEventListener('submit', asyncCallback('set pacing reference', async (event) => {
		event.preventDefault();
		const value = referenceSelect?.value ?? '';
		const referenceTrackId = value === '' ? null : Number(value);

		say('Setting the pacing reference…');
		try {
			await send(`/api/RacesApi/${raceId}`, 'PUT', { referenceTrackId });
			// The curve is refitted from a different ride, so the whole field is paced differently —
			// the payload has to be fetched again. Only once that lands is the new pacing on screen,
			// so the done message comes after it, not after the PUT.
			await onChanged();
			// Deliberately does NOT claim the field is now paced from that ride. The server answers a
			// bodyless Ok for a reference it later fails to FIT (no timestamps, or too few grade bins
			// overlapping the course), and the browser then falls back to its default curve — so the
			// save can succeed while the pacing is unchanged. Claiming the physics here would be a lie
			// in exactly the case the feature exists for. See RaceCostCurveService.FitAsync's nulls.
			say(referenceTrackId === null
				? 'Pacing reference cleared — falling back to the course ride.'
				: 'Pacing reference saved.');
		} catch (error) {
			say((error as Error).message, true);
		}
	}));

	/**
	 * Name the rider, not the file he came from.
	 *
	 * The box's RAW value goes to the server, empty string included — "blank means the ride's own name"
	 * is the server's rule (RaceParticipantCrud), and a client that resolved it here would be a second
	 * copy of that rule, free to drift. The server answers with the name it RESOLVED, which is the only
	 * way this box can show the ride's name after being cleared without re-deriving that rule here.
	 *
	 * Deliberately not a document reload: every other row is an editable box that may hold text the user
	 * has typed and not yet saved, and reloading the page to save this one would throw the rest away.
	 */
	for (const form of panel.querySelectorAll<HTMLFormElement>('.race-rider-rename')) {
		form.addEventListener('submit', asyncCallback('rename race rider', async (event) => {
			event.preventDefault();
			const participantId = Number(readData(form, 'participantId'));
			const input = form.querySelector<HTMLInputElement>('.race-rider-name');
			if (!participantId || !input) return;

			say('Renaming…');
			try {
				const row = await send<RaceParticipantRowDto>(
					`/api/RacesApi/${raceId}/participants/name`, 'PUT',
					{ participantId, displayName: input.value });
				if (row) input.value = row.name ?? '';

				// The name is also on the map label, the sidebar, the results board and the speed legend —
				// all built from the replay payload, so it has to be read again.
				await onChanged();
				say('Renamed.');
			} catch (error) {
				say((error as Error).message, true);
			}
		}));
	}

	/**
	 * Paint one rider from his own pill.
	 *
	 * `change`, not `input`: the native picker fires `input` continuously while the user drags through the
	 * spectrum, which would be a PUT and a full replay rebuild per frame. `change` fires once, when he has
	 * chosen.
	 *
	 * The endpoint is the results table's group paint with a one-id list — a single rider is not a special
	 * case of painting, and a second endpoint would be a second copy of the validation and the gate.
	 */
	for (const input of panel.querySelectorAll<HTMLInputElement>('.race-rider-color')) {
		input.addEventListener('change', asyncCallback('paint race rider', async () => {
			const participantId = Number(readData(input, 'participantId'));
			if (!participantId) return;

			say('Saving the colour…');
			try {
				await send<RacePaintResultDto>(`/api/RacesApi/${raceId}/participants/color`, 'PUT',
					{ participantIds: [participantId], color: input.value });
				// The colour is baked into race.meta at build time and read by the markers, the dock strip,
				// the results swatch and the speed chart — all of it comes from the payload.
				await onChanged();
				// The pill wears the rider's colour, and it is server-rendered — the refresh above rebuilds
				// the replay, not this panel, so the swatch would keep the old ring until a reload.
				input.closest('li')?.style.setProperty('--rider-color', input.value);
				say('Colour saved.');
			} catch (error) {
				say((error as Error).message, true);
			}
		}));
	}

	/**
	 * Hide a rider's call-out: no name on the map, no pinned sidebar row, no line in the speed chart. His
	 * dot keeps riding — this is "stop shouting about him", not "remove him".
	 *
	 * Pressed = hidden, and the button carries its own state (`aria-pressed`) because the panel is not
	 * re-rendered by the in-place refresh; the icon is flipped here for the same reason.
	 */
	for (const button of panel.querySelectorAll<HTMLButtonElement>('.race-rider-callout')) {
		button.addEventListener('click', asyncCallback('hide race rider callout', async () => {
			const participantId = Number(readData(button, 'participantId'));
			if (!participantId) return;

			const calloutOff = button.getAttribute('aria-pressed') !== 'true';
			say(calloutOff ? 'Hiding…' : 'Restoring…');
			try {
				await send<RaceCalloutResultDto>(`/api/RacesApi/${raceId}/participants/callout-off`, 'PUT',
					{ participantIds: [participantId], calloutOff });
				await onChanged();
				button.setAttribute('aria-pressed', calloutOff ? 'true' : 'false');
				button.querySelector('i')?.classList.toggle('bi-eye-slash', calloutOff);
				button.querySelector('i')?.classList.toggle('bi-eye', !calloutOff);
				button.closest('li')?.classList.toggle('is-callout-off', calloutOff);
				say(calloutOff ? 'Highlight hidden.' : 'Highlight restored.');
			} catch (error) {
				say((error as Error).message, true);
			}
		}));
	}

	for (const button of panel.querySelectorAll<HTMLButtonElement>('.race-rider-remove')) {
		button.addEventListener('click', asyncCallback('remove race rider', async () => {
			const participantId = Number(readData(button, 'participantId'));
			if (!participantId) return;

			say('Removing…');
			try {
				await send(`/api/RacesApi/${raceId}/participants/${participantId}`, 'DELETE');
				await onChanged();
				say('Rider removed.');
			} catch (error) {
				say((error as Error).message, true);
			}
		}));
	}
}
