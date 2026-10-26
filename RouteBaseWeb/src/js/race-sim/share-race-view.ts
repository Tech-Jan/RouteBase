// share-race-view.ts — PUBLIC (anonymous) race replay: /s/{token}, kind = "race".
//
// A fork of the owner's /Races/Replay page in the same sense share-live-view.ts is a fork of the LiveView:
// it reuses the render engine wholesale and owns none of it. Everything visible comes from
// race-sim/race-replay-view.ts — the SAME module the owner page mounts — at `editable: false`, fed from the
// redacted share payload. So a chart improvement, a marker fix or a new results column lands once and shows
// up on both pages, while every owner control stays in race-sim/race-replay.ts where a visitor cannot reach it.
//
// Keep this file a mount list. If it ever grows rendering logic, that logic belongs in a race-sim/* module.

import { createRaceReplayView } from './race-replay-view';
import { fetchShareRaceReplay } from './race-api';
import { readIsland } from '../core/page-bootstrap';
import type { ShareRaceSeedDto } from '../generated/contracts';

export function initShareRaceView(): (() => void) | undefined {
	const root = document.getElementById('shareRaceApp');
	if (!root) return undefined;

	// Parsed, never eval'd, and no window global — the same seed-island idiom as share-live-view.
	const seed = readIsland<ShareRaceSeedDto | null>('share-race-data', null);
	if (!seed) return undefined;

	const view = createRaceReplayView({
		raceId: seed.raceId,
		// The whole fork, in one flag: no checkpoint editor (its mount is not rendered either), no paint
		// controls, no click-to-place on the profile.
		editable: false,
		// No basemap here: race-replay-view reads it from the account-settings seam, which on a share page
		// holds the global defaults. No MapTiler key ever reaches a share page, so the keyless basemap is
		// the only resolvable one and the registry falls back to it on its own.
		loadPayload: () => fetchShareRaceReplay(seed.apiBase),
	});

	return view.destroy;
}

const dispose = initShareRaceView();
// pagehide fires on bfcache ENTRY too (persisted=true); disposing there destroys the map shell — including
// its own restore handling — so Back would restore a blank, dead page (the FE-05 lesson from the live fork).
// Only a real unload disposes.
window.addEventListener('pagehide', (e) => { if (!e.persisted) dispose?.(); });
