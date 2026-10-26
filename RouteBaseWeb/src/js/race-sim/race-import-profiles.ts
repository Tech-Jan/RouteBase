/**
 * Saved column mappings — the thing that makes NEXT year's file a one-click import.
 *
 * Per USER, not per race. That is the whole point: the same timing platform emits the same header every
 * season, so a mapping named "Datasport" should outlive the race it was first worked out on. What is
 * stored is therefore race-INDEPENDENT (split columns by checkpoint name/seq, never by checkpoint id) and
 * the server resolves it onto whichever race asked — see RaceImportProfileStore.
 *
 * Pure rendering + fetch. The panel owns the DOM ids and the mapping; this owns the round trips.
 */

import { escapeHtml } from '../core/html';
import { deleteRaceImportProfile, listRaceImportProfiles, saveRaceImportProfile } from './race-api';
import type { RaceCsvMappingBody, RaceImportProfileDto } from './race-api';

/** The `<option>`s for the saved-mapping picker. Names come from the user, so they are escaped. */
export function renderProfileOptions(profiles: readonly RaceImportProfileDto[]): string {
	return '<option value="">(saved mappings…)</option>'
		+ profiles.map((p) => `<option value="${escapeHtml(p.name)}">${escapeHtml(p.name)}</option>`).join('');
}

/**
 * A saved profile → the picker values to apply.
 *
 * Returns a map of element id → value, rather than touching the DOM, so the "which id holds which field"
 * knowledge stays in ONE place (the panel) and this stays testable without a document.
 */
export function profileSelections(
	profile: RaceImportProfileDto, splitSelectId: (checkpointId: number) => string,
): Map<string, string> {
	const selections = new Map<string, string>();
	const put = (id: string, value: string | null): void => { selections.set(id, value ?? ''); };

	put('csvName', profile.nameColumn);
	put('csvFinish', profile.finishColumn);
	put('csvBib', profile.bibColumn);
	put('csvCategory', profile.categoryColumn);
	put('csvGender', profile.genderColumn);
	put('csvClub', profile.clubColumn);
	put('csvStatus', profile.statusColumn);
	put('csvGun', profile.gunTimeColumn);
	put('csvNet', profile.netTimeColumn);
	put('csvStartOffset', profile.startOffsetColumn);
	put('csvWave', profile.waveColumn);
	put('csvDiscipline', profile.disciplineColumn);
	// Only the checkpoints THIS race has: the server has already dropped any the profile could not be
	// resolved onto, so an unmatched split is simply absent rather than mapped onto the wrong checkpoint.
	for (const [checkpointId, column] of Object.entries(profile.splitColumns)) {
		put(splitSelectId(Number(checkpointId)), column);
	}
	return selections;
}

export interface RaceImportProfiles {
	/** Re-fetch and re-render the picker. */
	readonly refresh: () => Promise<void>;
	/** The profile the picker currently names, or null. */
	readonly selected: () => RaceImportProfileDto | null;
	readonly save: (name: string, mapping: RaceCsvMappingBody) => Promise<void>;
	readonly remove: (name: string) => Promise<void>;
}

/**
 * Wire the saved-mapping controls. `select`/`saveButton`/`deleteButton` are optional: the panel renders
 * them only for a user who may edit, and a missing control must be a no-op rather than a throw.
 */
export function createRaceImportProfiles(
	raceId: number,
	select: HTMLSelectElement | null,
	onApply: (profile: RaceImportProfileDto) => void,
): RaceImportProfiles {
	let profiles: RaceImportProfileDto[] = [];

	async function refresh(): Promise<void> {
		profiles = await listRaceImportProfiles(raceId);
		if (!select) return;
		const previous = select.value;
		// eslint-disable-next-line no-unsanitized/property -- renderProfileOptions escapes every name
		select.innerHTML = renderProfileOptions(profiles);
		select.value = profiles.some((p) => p.name === previous) ? previous : '';
	}

	const selected = (): RaceImportProfileDto | null =>
		profiles.find((p) => p.name === select?.value) ?? null;

	select?.addEventListener('change', () => {
		const profile = selected();
		if (profile) onApply(profile);
	});

	return {
		refresh,
		selected,
		save: async (name, mapping) => {
			await saveRaceImportProfile(raceId, name, mapping);
			await refresh();
			if (select) select.value = name;
		},
		remove: async (name) => {
			await deleteRaceImportProfile(raceId, name);
			await refresh();
		},
	};
}
