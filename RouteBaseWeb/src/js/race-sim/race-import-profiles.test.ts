// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { profileSelections, renderProfileOptions } from './race-import-profiles';
import type { RaceImportProfileDto } from './race-api';

function profile(over: Partial<RaceImportProfileDto> = {}): RaceImportProfileDto {
	return {
		name: 'Datasport',
		nameColumn: 'name',
		finishColumn: 'finish',
		bibColumn: 'bib',
		categoryColumn: 'category',
		genderColumn: null,
		clubColumn: 'club_loc',
		statusColumn: 'status',
		gunTimeColumn: null,
		netTimeColumn: null,
		startOffsetColumn: 'start_offset',
		waveColumn: null,
		disciplineColumn: null,
		perLegTimes: false,
		splitColumns: { 2: 'cp_hinterzarten', 3: 'cp_raimartihof' },
		...over,
	};
}

const splitSelectId = (checkpointId: number): string => `csvSplit-${checkpointId}`;

describe('renderProfileOptions', () => {
	it('always offers the empty choice, so the picker can be left alone', () => {
		expect(renderProfileOptions([])).toBe('<option value="">(saved mappings…)</option>');
	});

	it('escapes the name — the user typed it', () => {
		const html = renderProfileOptions([profile({ name: '<img src=x onerror=alert(1)>' })]);

		expect(html).not.toContain('<img');
		expect(html).toContain('&lt;img');
	});
});

describe('profileSelections', () => {
	it('maps every field onto the picker that holds it', () => {
		const selections = profileSelections(profile(), splitSelectId);

		expect(selections.get('csvName')).toBe('name');
		expect(selections.get('csvFinish')).toBe('finish');
		expect(selections.get('csvBib')).toBe('bib');
		expect(selections.get('csvClub')).toBe('club_loc');
		expect(selections.get('csvStatus')).toBe('status');
	});

	it('carries the staggered-start columns too — a saved mapping must reproduce the WHOLE import', () => {
		// A field added to the mapping and forgotten here is the failure mode: the profile then imports a
		// file the user believes they mapped identically, minus the start offsets.
		expect(profileSelections(profile(), splitSelectId).get('csvStartOffset')).toBe('start_offset');
	});

	it('CLEARS a field the profile has no column for, rather than leaving the last one standing', () => {
		// Applying a profile must be a complete state, not a partial overlay: a Gender column left over
		// from the previous file would silently import the wrong column under a mapping the user believes
		// they just chose wholesale.
		expect(profileSelections(profile(), splitSelectId).get('csvGender')).toBe('');
	});

	it('maps split columns onto THIS race\'s checkpoint pickers', () => {
		const selections = profileSelections(profile(), splitSelectId);

		expect(selections.get('csvSplit-2')).toBe('cp_hinterzarten');
		expect(selections.get('csvSplit-3')).toBe('cp_raimartihof');
	});

	it('touches no picker for a checkpoint the server could not resolve', () => {
		// The server drops an unmatched split rather than sliding it onto a neighbour — mapping one column
		// onto two checkpoints is a composite-PK collision at import.
		const selections = profileSelections(profile({ splitColumns: { 2: 'cp_a' } }), splitSelectId);

		expect(selections.has('csvSplit-2')).toBe(true);
		expect(selections.has('csvSplit-3')).toBe(false);
	});
});
