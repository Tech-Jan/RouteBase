/**
 * The /Races page — create a race from one of your rides.
 *
 * Creating a race is one call: the course is an ACTIVITY (yours, possibly one the editor merged out of
 * several days), so there is no geometry to build here. Importing the field happens on the race page,
 * which is the only place that has a race to import into.
 */

import { asyncCallback } from '../core/async-listener';
import { readData } from '../core/data-attrs';
import { attachSelectFilter } from '../core/select-filter';
import { setupLazyThumbnails } from '../core/track-thumbnail';
import { initRaceListDelete } from './race-list-delete';
import { initRaceListDuplicate } from './race-list-duplicate';
import type { RaceSummaryDto } from '../generated/contracts';

// POST /api/RacesApi returns the full RaceSummaryDto; this page reads only raceId to redirect (D4).
type CreateRaceResponse = Pick<RaceSummaryDto, 'raceId'>;

function el<T extends HTMLElement>(id: string): T | null {
	return document.getElementById(id) as T | null;
}

const courseFilter = el<HTMLInputElement>('raceCourseFilter');
const courseSelect = el<HTMLSelectElement>('raceCourseActivity');
if (courseFilter && courseSelect) attachSelectFilter(courseFilter, courseSelect);

function showStatus(message: string, isError = false): void {
	const box = el('raceStatus');
	if (!box) return;
	box.textContent = message;
	box.className = isError ? 'race-import-status is-error' : 'race-import-status';
	box.hidden = false;
}

const raceList = document.getElementById('raceListSection');
if (raceList) {
	// Course-shape minimaps — one per race card, from the course activity's GPS. Same lazy loader and
	// endpoint as the dashboard/segments cards; a race's course is always a released ride with distance.
	setupLazyThumbnails({
		containerSelector: '.minimap[data-track-id]',
		idAttribute: 'data-track-id',
		fetchGps: async (trackId) => {
			const resp = await fetch('/api/activities/' + trackId + '/gps-summary');
			if (!resp.ok) throw new Error('GPS fetch failed: ' + resp.status);
			return resp.json();
		},
		trackColor: '#22d3ee', // --acc-cyan
	});

	initRaceListDelete({
		root: raceList,
		// The list, the counts and the empty state are all server-rendered — re-render rather than keep a
		// second copy of the truth in the client.
		onDeleted: () => window.location.reload(),
		onError: (message) => showStatus(`Could not delete: ${message}`, true),
		// A card filtered out by the controls grid is hidden — never sweep it into Select all / Delete selected.
		isSelectable: (check) => !check.closest('.race-card')?.classList.contains('is-hidden'),
	});
	initRaceListDuplicate({
		root: raceList,
		onDuplicated: () => window.location.reload(),
		onError: (message) => showStatus(`Could not copy the race: ${message}`, true),
	});

	// Click the card to open its replay — same as a segment/dashboard card. Clicks that land on an inner
	// control (select checkbox, delete, copy) are theirs to handle, so they never navigate.
	const cardGrid = raceList.querySelector<HTMLElement>('.race-card-grid');
	function replayHrefFor(target: EventTarget | null): string | undefined {
		if (target instanceof HTMLElement && target.closest('button, input, label, a')) return undefined;
		const card = target instanceof HTMLElement ? target.closest<HTMLElement>('.race-card') : null;
		return card ? readData(card, 'replayHref') ?? undefined : undefined;
	}
	cardGrid?.addEventListener('click', (event) => {
		const href = replayHrefFor(event.target);
		if (href) window.location.href = href;
	});
	// Keyboard: the card is role="link", so Enter on the focused card activates it.
	cardGrid?.addEventListener('keydown', (event) => {
		if (event.key !== 'Enter') return;
		const card = event.target;
		if (!(card instanceof HTMLElement) || !card.classList.contains('race-card')) return;
		const href = readData(card, 'replayHref');
		if (href) window.location.href = href;
	});
}

el('raceCreateForm')?.addEventListener('submit', asyncCallback('create race', async (event) => {
	event.preventDefault();
	const trackId = Number(el<HTMLSelectElement>('raceCourseActivity')?.value);
	const name = el<HTMLInputElement>('raceName')?.value?.trim();
	if (!trackId || !name) return;

	try {
		const response = await fetch('/api/RacesApi', {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({
				courseTrackId: trackId,
				raceName: name,
				raceDate: el<HTMLInputElement>('raceDate')?.value || null,
				// `||`, not `??`: an empty-string select value must also fall back to the safe default — the
				// server stores visibility verbatim, and `?? 'friends'` would let '' through (PF-04).
				visibility: el<HTMLSelectElement>('raceVisibility')?.value || 'friends',
			}),
		});
		if (!response.ok) throw new Error((await response.text()) || `HTTP ${response.status}`);

		const race: CreateRaceResponse = await response.json();
		window.location.href = `/Races/Replay?raceId=${race.raceId}`;
	} catch (error) {
		showStatus(`Could not create the race: ${(error as Error).message}`, true);
	}
}));
