import { describe, expect, it, vi } from 'vitest';
import { deleteConfirmText, initRaceListDelete } from './race-list-delete';

/** The server-rendered races list, cut down to the parts this module drives. */
function mount(rows: { raceId: number; name: string; participants: number }[]) {
	const root = document.createElement('div');
	root.innerHTML = `
		<input type="checkbox" id="raceSelectAll" />
		<span id="raceSelectionLabel">Select all</span>
		<button type="button" id="raceDeleteSelected" disabled>Delete selected</button>
		${rows
			.map(
				(row) => `
			<input type="checkbox" class="race-row-check"
			       data-race-id="${row.raceId}" data-race-name="${row.name}"
			       data-participant-count="${row.participants}" />
			<button type="button" class="race-row-delete"
			        data-race-id="${row.raceId}" data-race-name="${row.name}"
			        data-participant-count="${row.participants}">Delete</button>`,
			)
			.join('')}
	`;
	document.body.replaceChildren(root);
	return {
		root,
		selectAll: root.querySelector<HTMLInputElement>('#raceSelectAll')!,
		deleteButton: root.querySelector<HTMLButtonElement>('#raceDeleteSelected')!,
		label: root.querySelector<HTMLElement>('#raceSelectionLabel')!,
		checks: Array.from(root.querySelectorAll<HTMLInputElement>('.race-row-check')),
		rowDeletes: Array.from(root.querySelectorAll<HTMLButtonElement>('.race-row-delete')),
	};
}

const twoRaces = [
	{ raceId: 7, name: 'Alpine Classic', participants: 424 },
	{ raceId: 9, name: 'Winter Crit', participants: 12 },
];

describe('deleteConfirmText', () => {
	it('names the race and its riders when one is selected', () => {
		const text = deleteConfirmText([{ raceId: 7, name: 'Alpine Classic', participantCount: 424 }]);
		expect(text).toContain('Alpine Classic');
		expect(text).toContain('424 riders');
		expect(text).toContain('cannot be undone');
	});

	// An empty race has no field to warn about — claiming "0 riders" would be noise.
	it('omits the rider clause for an empty race', () => {
		expect(deleteConfirmText([{ raceId: 7, name: 'Alpine Classic', participantCount: 0 }])).not.toContain('riders');
	});

	it('counts races and pools their riders for a selection', () => {
		const text = deleteConfirmText([
			{ raceId: 7, name: 'Alpine Classic', participantCount: 424 },
			{ raceId: 9, name: 'Winter Crit', participantCount: 12 },
		]);
		expect(text).toContain('Delete 2 races');
		expect(text).toContain('436 riders');
	});
});

describe('initRaceListDelete', () => {
	it('deletes only the checked races and tells the page', async () => {
		const dom = mount(twoRaces);
		const onDeleted = vi.fn();
		const remove = vi.fn(async () => {});
		initRaceListDelete({ root: dom.root, onDeleted, remove, confirm: () => true });

		dom.checks[1]!.checked = true;
		dom.checks[1]!.dispatchEvent(new Event('change'));
		dom.deleteButton.click();
		await vi.waitFor(() => expect(onDeleted).toHaveBeenCalled());

		expect(remove).toHaveBeenCalledTimes(1);
		expect(remove).toHaveBeenCalledWith(9);
	});

	it('deletes one race from its own row button', async () => {
		const dom = mount(twoRaces);
		const onDeleted = vi.fn();
		const remove = vi.fn(async () => {});
		initRaceListDelete({ root: dom.root, onDeleted, remove, confirm: () => true });

		dom.rowDeletes[0]!.click();
		await vi.waitFor(() => expect(onDeleted).toHaveBeenCalled());

		expect(remove).toHaveBeenCalledWith(7);
	});

	// The delete is final and un-trashed: a declined confirm must reach no API at all.
	it('does nothing when the confirm is declined', () => {
		const dom = mount(twoRaces);
		const remove = vi.fn(async () => {});
		const onDeleted = vi.fn();
		initRaceListDelete({ root: dom.root, onDeleted, remove, confirm: () => false });

		dom.rowDeletes[0]!.click();

		expect(remove).not.toHaveBeenCalled();
		expect(onDeleted).not.toHaveBeenCalled();
	});

	it('select-all checks every row and the Apply button follows the selection', () => {
		const dom = mount(twoRaces);
		initRaceListDelete({ root: dom.root, onDeleted: () => {}, remove: async () => {} });

		expect(dom.deleteButton.disabled).toBe(true);
		expect(dom.label.textContent).toBe('Select all');

		dom.selectAll.checked = true;
		dom.selectAll.dispatchEvent(new Event('change'));

		expect(dom.checks.every((c) => c.checked)).toBe(true);
		expect(dom.deleteButton.disabled).toBe(false);
		expect(dom.label.textContent).toBe('2 selected');
	});

	// A card filtered out of the grid is hidden; its checkbox must not be swept up by Select all / Delete.
	it('excludes non-selectable (filtered-out) checkboxes from select-all and delete', async () => {
		const dom = mount(twoRaces);
		const remove = vi.fn(async () => {});
		initRaceListDelete({
			root: dom.root,
			onDeleted: () => {},
			remove,
			confirm: () => true,
			isSelectable: (check) => check.dataset.raceId !== '7', // race 7 is "filtered out"
		});

		dom.selectAll.checked = true;
		dom.selectAll.dispatchEvent(new Event('change'));

		expect(dom.checks.find((c) => c.dataset.raceId === '7')!.checked).toBe(false);
		expect(dom.checks.find((c) => c.dataset.raceId === '9')!.checked).toBe(true);
		expect(dom.label.textContent).toBe('1 selected');

		dom.deleteButton.click();
		await vi.waitFor(() => expect(remove).toHaveBeenCalled());
		expect(remove).toHaveBeenCalledTimes(1);
		expect(remove).toHaveBeenCalledWith(9);
	});

	// Double-submit guard: the Apply button must lock the instant the batch starts, before the first
	// remove() resolves — a second click mid-flight would re-issue deletes for already-deleted races.
	it('disables the Apply button immediately on click, before the deletes resolve', async () => {
		const dom = mount(twoRaces);
		let settleRemove: () => void = () => {};
		const remove = vi.fn(() => new Promise<void>((res) => { settleRemove = res; }));
		const onDeleted = vi.fn();
		initRaceListDelete({ root: dom.root, onDeleted, remove, confirm: () => true });

		dom.checks[0]!.checked = true;
		dom.checks[0]!.dispatchEvent(new Event('change'));
		expect(dom.deleteButton.disabled).toBe(false); // enabled once a row is checked

		dom.deleteButton.click();
		// Synchronously after the click, while remove() is still pending:
		expect(dom.deleteButton.disabled).toBe(true);
		expect(onDeleted).not.toHaveBeenCalled();

		settleRemove();
		await vi.waitFor(() => expect(onDeleted).toHaveBeenCalled());
	});

	it('reports a refused delete and leaves the selection usable', async () => {
		const dom = mount(twoRaces);
		const onError = vi.fn();
		const remove = vi.fn(async () => { throw new Error('Not your race.'); });
		initRaceListDelete({ root: dom.root, onDeleted: () => {}, remove, confirm: () => true, onError });

		dom.checks[0]!.checked = true;
		dom.checks[0]!.dispatchEvent(new Event('change'));
		dom.deleteButton.click();
		await vi.waitFor(() => expect(onError).toHaveBeenCalledWith('Not your race.'));

		expect(dom.deleteButton.disabled).toBe(false);
	});
});
