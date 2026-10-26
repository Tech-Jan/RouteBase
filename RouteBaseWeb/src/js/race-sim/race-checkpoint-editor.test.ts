import { beforeEach, describe, expect, it, vi } from 'vitest';

const addCheckpoint = vi.fn();
const updateCheckpoint = vi.fn();
const deleteCheckpoint = vi.fn();

vi.mock('./race-api', () => ({
	addCheckpoint: (...args: unknown[]) => addCheckpoint(...args),
	deleteCheckpoint: (...args: unknown[]) => deleteCheckpoint(...args),
	updateCheckpoint: (...args: unknown[]) => updateCheckpoint(...args),
}));

import { initRaceCheckpointEditor, sectionsOf } from './race-checkpoint-editor';
import type { RaceCheckpointDto } from './race-api';

// Two sections: "XC Ski" (0 → 5.108 km) and "Downhill" (5.108 km → the Finish). A section's name and
// colour live on the checkpoint it FOLLOWS — checkpoint 1 carries section 1's.
const checkpoints: RaceCheckpointDto[] = [
	{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, legName: 'XC Ski', legColor: '#4dabf7', node: 0 },
	{ checkpointId: 2, seq: 1, name: 'XC Ski', chainageMeters: 5108, legName: 'Downhill', legColor: '#f76707', node: 0 },
	{ checkpointId: 3, seq: 2, name: 'Finish', chainageMeters: 142228, legName: null, legColor: null, node: 0 },
];

function mount(onArmPlacement?: (armed: boolean) => void) {
	const el = document.createElement('div');
	document.body.replaceChildren(el);
	const editor = initRaceCheckpointEditor({
		raceId: 7, mount: el, editable: true, courseTotalMeters: 143000, onChanged: () => {}, onArmPlacement,
	});
	editor.setCheckpoints(checkpoints);
	return { el, editor };
}

/** A section row is keyed by the checkpoint that ENDS it — what the profile draws and drags. */
const row = (el: HTMLElement, endsAt: number) => el.querySelector<HTMLElement>(`[data-checkpoint-id="${endsAt}"]`)!;
const field = (el: HTMLElement, endsAt: number, name: string) =>
	row(el, endsAt).querySelector<HTMLInputElement>(`[data-field="${name}"]`)!;
const applyBtn = (el: HTMLElement) => el.querySelector<HTMLButtonElement>('.race-cp-apply')!;
const cancelBtn = (el: HTMLElement) => el.querySelector<HTMLButtonElement>('.race-cp-cancel')!;
const addBtn = (el: HTMLElement) => el.querySelector<HTMLButtonElement>('.race-cp-add')!;

function commit(input: HTMLInputElement, value: string): void {
	input.value = value;
	input.dispatchEvent(new Event('change', { bubbles: true }));
}

beforeEach(() => {
	addCheckpoint.mockReset();
	updateCheckpoint.mockReset().mockResolvedValue(checkpoints[1]);
	deleteCheckpoint.mockReset().mockResolvedValue(undefined);
});

describe('sectionsOf', () => {
	it('turns N checkpoints into the N-1 sections between them', () => {
		const sections = sectionsOf(checkpoints);
		expect(sections).toHaveLength(2);
		expect(sections[0]!.start.checkpointId).toBe(1);
		expect(sections[0]!.end.checkpointId).toBe(2);
		expect(sections[0]!.endsAtFinish).toBe(false);
		expect(sections[1]!.endsAtFinish).toBe(true);
	});

	it('reads a section name off the checkpoint it follows, not the one it ends at', () => {
		expect(sectionsOf(checkpoints)[0]!.start.legName).toBe('XC Ski');
	});
});

describe('initRaceCheckpointEditor — rendering', () => {
	it('renders one row per section, named for the section', () => {
		const { el } = mount();
		expect(el.querySelectorAll('.race-cp-row')).toHaveLength(2);
		expect(field(el, 2, 'sectionName').value).toBe('XC Ski');
		expect(field(el, 3, 'sectionName').value).toBe('Downhill');
	});

	// The Finish USED to be a fixed label here, seeded from the course activity's own distance — a different
	// metric from the geometry chainage is measured in, which left it 337 m inside a 70 km ride with no way
	// to correct it. Both lines are typed now; the ride's length is stated beside them, never as them.
	it('lets every boundary be typed, the Finish included', () => {
		const { el } = mount();
		expect(field(el, 2, 'endKm').value).toBe('5.108');
		expect(field(el, 3, 'endKm').value).toBe('142.228');
		expect(row(el, 3).querySelector('.race-cp-finish-tag')).not.toBeNull();
	});

	it('lets the Start be typed too — a race may begin after the roll-out', () => {
		const { el } = mount();
		expect(el.querySelector<HTMLInputElement>('.race-cp-endpoint [data-field="startKm"]')!.value).toBe('0.000');
	});

	it('states the race distance and the ride distance apart', () => {
		const { el } = mount();
		expect(el.querySelector('.race-cp-distance')!.textContent)
			.toBe('Race 142.228 km · ride 143.000 km');
	});

	it('starts clean — Apply and Cancel are disabled until something changes', () => {
		const { el } = mount();
		expect(applyBtn(el).disabled).toBe(true);
		expect(cancelBtn(el).disabled).toBe(true);
	});
});

describe('initRaceCheckpointEditor — staged edits (nothing until Apply)', () => {
	it('does not touch the server on an edit; Apply persists the typed km', async () => {
		const { el } = mount();

		commit(field(el, 2, 'endKm'), '8.504');
		// Staged: no write yet, but the editor is now dirty.
		expect(updateCheckpoint).not.toHaveBeenCalled();
		expect(applyBtn(el).disabled).toBe(false);

		applyBtn(el).click();
		await vi.waitFor(() => expect(updateCheckpoint).toHaveBeenCalled());
		expect(updateCheckpoint).toHaveBeenCalledWith(7, 2, { chainageMeters: 8504 });
	});

	it('Apply persists a moved Start and a moved Finish — both go through the same reconciler', async () => {
		const { el } = mount();

		commit(el.querySelector<HTMLInputElement>('.race-cp-endpoint [data-field="startKm"]')!, '1.200');
		commit(field(el, 3, 'endKm'), '141.800');
		expect(updateCheckpoint).not.toHaveBeenCalled();

		applyBtn(el).click();
		await vi.waitFor(() => expect(updateCheckpoint).toHaveBeenCalledTimes(2));
		expect(updateCheckpoint).toHaveBeenCalledWith(7, 1, { chainageMeters: 1200 });
		expect(updateCheckpoint).toHaveBeenCalledWith(7, 3, { chainageMeters: 141800 });
		// The race got shorter; the ride did not.
		expect(el.querySelector('.race-cp-distance')!.textContent).toBe('Race 140.600 km · ride 143.000 km');
	});

	it('Apply writes a colour to the checkpoint the section follows', async () => {
		const { el } = mount();
		commit(field(el, 2, 'sectionColor'), '#ff0000');
		expect(updateCheckpoint).not.toHaveBeenCalled();

		applyBtn(el).click();
		await vi.waitFor(() => expect(updateCheckpoint).toHaveBeenCalled());
		// Section 1's colour lives on checkpoint 1 — NOT on checkpoint 2, which merely ends it.
		expect(updateCheckpoint).toHaveBeenCalledWith(7, 1, { legColor: '#ff0000' });
	});

	it('Apply names the section and the split that ends it in one commit', async () => {
		const { el } = mount();
		commit(field(el, 2, 'sectionName'), 'xcski');

		applyBtn(el).click();
		await vi.waitFor(() => expect(updateCheckpoint).toHaveBeenCalledTimes(2));
		expect(updateCheckpoint).toHaveBeenCalledWith(7, 1, { legName: 'xcski' });
		expect(updateCheckpoint).toHaveBeenCalledWith(7, 2, { name: 'xcski' });
	});

	it('does not rename the Finish when the last section is named', async () => {
		const { el } = mount();
		commit(field(el, 3, 'sectionName'), 'kayak');

		applyBtn(el).click();
		await vi.waitFor(() => expect(updateCheckpoint).toHaveBeenCalled());
		expect(updateCheckpoint).toHaveBeenCalledTimes(1);
		expect(updateCheckpoint).toHaveBeenCalledWith(7, 2, { legName: 'kayak' });
	});

	it('deleting a row stages a merge; the row goes at once, the delete waits for Apply', async () => {
		const { el } = mount();

		row(el, 2).querySelector<HTMLButtonElement>('[data-action="delete"]')!.click();
		expect(el.querySelectorAll('.race-cp-row')).toHaveLength(1);   // drafted immediately
		expect(deleteCheckpoint).not.toHaveBeenCalled();

		applyBtn(el).click();
		await vi.waitFor(() => expect(deleteCheckpoint).toHaveBeenCalledWith(7, 2));
	});

	it('Cancel throws the draft away and snaps the boundary back', () => {
		const { el } = mount();
		commit(field(el, 2, 'endKm'), '8.504');
		expect(field(el, 2, 'endKm').value).toBe('8.504');

		cancelBtn(el).click();
		expect(field(el, 2, 'endKm').value).toBe('5.108');
		expect(updateCheckpoint).not.toHaveBeenCalled();
		expect(applyBtn(el).disabled).toBe(true);
	});
});

describe('initRaceCheckpointEditor — placement is gated by Add checkpoint', () => {
	it('ignores a profile click until Add checkpoint arms it, then places one', async () => {
		const armed = vi.fn();
		const { el, editor } = mount(armed);

		// Unarmed: a profile click does nothing.
		editor.place(3000);
		expect(el.querySelectorAll('.race-cp-row')).toHaveLength(2);

		// Arm, then place: a new section row appears (drafted), and placement disarms itself.
		addBtn(el).click();
		expect(armed).toHaveBeenLastCalledWith(true);
		editor.place(3000);
		expect(el.querySelectorAll('.race-cp-row')).toHaveLength(3);
		expect(armed).toHaveBeenLastCalledWith(false);
		expect(addCheckpoint).not.toHaveBeenCalled();   // still staged

		addCheckpoint.mockResolvedValue({ checkpointId: 9, seq: 1, name: '', chainageMeters: 3000, legName: null, legColor: null });
		applyBtn(el).click();
		await vi.waitFor(() => expect(addCheckpoint).toHaveBeenCalledWith(7, '', 3000));
	});

	// PF-05: the created id was adopted onto the draft only AFTER the follow-up leg update. If that update
	// throws (a transient server error), the draft kept its temporary negative id — so a re-Apply created a
	// SECOND, duplicate checkpoint for a boundary that already exists on the server. The id must be adopted
	// the instant addCheckpoint succeeds.
	it('does not re-create a checkpoint on re-Apply when its leg update failed once', async () => {
		const { el, editor } = mount();
		addBtn(el).click();
		editor.place(3000);
		// Give the new section (new → cp2, row keyed by cp2) a colour, so the add path runs updateCheckpoint.
		commit(field(el, 2, 'sectionColor'), '#00ff00');

		addCheckpoint.mockResolvedValue({ checkpointId: 9, seq: 1, name: '', chainageMeters: 3000, legName: null, legColor: null });
		updateCheckpoint.mockReset()
			.mockRejectedValueOnce(new Error('boom'))                                  // the leg update fails once…
			.mockResolvedValue({ checkpointId: 9, seq: 1, name: '', chainageMeters: 3000, legName: null, legColor: '#00ff00' });

		applyBtn(el).click();                                                           // add succeeds, leg update throws
		await vi.waitFor(() => expect(updateCheckpoint).toHaveBeenCalled());
		expect(addCheckpoint).toHaveBeenCalledTimes(1);

		applyBtn(el).click();                                                           // re-Apply after the failure
		await vi.waitFor(() => expect(applyBtn(el).disabled).toBe(true));
		expect(addCheckpoint).toHaveBeenCalledTimes(1);                                 // NOT re-created (no duplicate)
	});
});

describe('initRaceCheckpointEditor — input survival', () => {
	it('keeps the clicked input alive when the click selects the row', () => {
		const { el } = mount();
		const km = field(el, 2, 'endKm');
		km.focus();
		km.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect(field(el, 2, 'endKm')).toBe(km);
		expect(document.activeElement).toBe(km);
		expect(row(el, 2).classList.contains('is-selected')).toBe(true);
	});

	it('does not rebuild the rows when a colour is staged', () => {
		const { el } = mount();
		const color = field(el, 2, 'sectionColor');
		commit(color, '#ff0000');
		expect(field(el, 2, 'sectionColor')).toBe(color);
	});
});
