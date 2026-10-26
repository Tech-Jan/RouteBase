/**
 * The /Races/Replay page — the OWNER's shell.
 *
 * Thin by design, in the strong sense: every pixel of the replay itself (map, markers, charts, results
 * table, playback chrome) belongs to race-sim/race-replay-view.ts, which the anonymous share fork
 * (race-sim/share-race-view.ts) mounts too. What lives HERE is only what a visitor must never have — the
 * field/course edit panels and the share-link mint dialog.
 *
 * That is the rule to keep: a new race CONTROL goes in this file (the fork then cannot show it, so there
 * is nothing to gate); a new race VIEW goes in a race-sim/* module (and both pages pick it up).
 */

import { readData } from '../core/data-attrs';
import { createRaceReplayView } from './race-replay-view';
import { fetchRaceReplay } from './race-api';
import { initRaceImportPanel } from './race-import-panel';
import { initRaceRidersPanel } from './race-riders-panel';
import { initRaceSettingsPanel } from './race-settings-panel';
import { initRaceClearField } from './race-clear-field';
import { initRaceWaveEditor } from './race-waves';
import { setupShareLinks } from '../core/share-links';

/** Returns the page teardown, or undefined when this is not the race replay page. */
export function initRaceReplayPage(): (() => void) | undefined {
	const root = document.getElementById('raceReplay');
	if (!root) return undefined;

	const raceId = Number(readData(root, 'raceId'));
	if (!Number.isFinite(raceId)) return undefined;

	// canEdit (owner OR a permitted collaborator) gates every field/course edit. The narrower owner-only
	// controls — the edit-policy dropdown and the share button — are gated SERVER-SIDE (Replay.cshtml
	// @if Model.IsOwner), so canShare is a read of what was rendered, not a second authority.
	const canEdit = readData(root, 'canEdit') === 'true';

	const view = createRaceReplayView({
		raceId,
		editable: canEdit,
		// The payload is read HERE as well as by the view, so the start-block editor can be built from the
		// waves the field actually has. An owner-only control, so it belongs in this shell — the share
		// fork must never be able to rewrite start times.
		loadPayload: async () => {
			const payload = await fetchRaceReplay(raceId);
			if (canEdit) initRaceWaveEditor(raceId, payload.participants, () => window.location.reload());
			return payload;
		},
	});

	// Both panels only make sense once there IS a race, so they live here rather than on the list page.
	// The import panel needs the checkpoints: a split column is mapped PER CHECKPOINT, so the pickers
	// cannot exist until the checkpoints do. They are bootstrapped from the page, not the payload, because
	// the panel is usable before the (async) replay fetch lands.
	// No checkpoint getter any more: the pickers are built from the PREVIEW, which the server answers
	// with this race's current checkpoints — so a checkpoint added on the profile is picked up by the
	// next round-trip instead of by this module remembering to re-read a snapshot.
	initRaceImportPanel(raceId, () => window.location.reload());
	// Refreshed in place, not reloaded: the riders panel is a column of editable name boxes, and a
	// document reload would discard whatever the user typed into the rows he has not saved yet. The
	// panels above and below it change the field wholesale and hold no unsaved text, so they still reload.
	initRaceRidersPanel(raceId, () => view.reload());
	initRaceSettingsPanel(raceId);   // owner-only edit-policy control; no-op when its select is absent

	const clearFieldButton = document.getElementById('raceClearField');
	if (clearFieldButton) {
		initRaceClearField({ raceId, button: clearFieldButton, onCleared: () => window.location.reload() });
	}

	// The mint dialog. Kind-agnostic module, owner-gated by the MARKUP: setupShareLinks no-ops when the
	// dialog was not rendered, so a non-owner needs no branch here.
	if (readData(root, 'canShare') === 'true') setupShareLinks({ kind: 'race', resourceId: raceId });

	return view.destroy;
}

const dispose = initRaceReplayPage();
// Same wiring as the share fork (share-race-view.ts): pagehide fires on bfcache ENTRY too
// (persisted=true), and disposing there would destroy the map shell's own restore handling —
// only a real unload disposes. Without this the view's destroy path was unreachable here.
window.addEventListener('pagehide', (e) => { if (!e.persisted) dispose?.(); });
