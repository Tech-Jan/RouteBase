import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { initRaceResultsTable, type PaintMode } from './race-results-table';
import { REAL_COLOR, SIMULATED_COLOR } from './race-build';
import type { RaceResultRow } from './race-results-row';
import type { RaceMapMode } from './race-map-display';

/**
 * Paint — the results table's colour UI — and the one axis on which an editor and a viewer differ.
 *
 * Painting the filtered set is how a 508-team field is READ, not how it is edited, so the controls render
 * for everyone, including the anonymous /s/{token} visitor. What `paintMode` decides is only whether the
 * colour reaches the DB:
 *
 *   persist — PUT /api/RacesApi/{id}/participants/color, then recolour (owner / permitted collaborator)
 *   local   — recolour, and nothing else. No request, no storage, gone on reload.
 *
 * Both branches must be indistinguishable downstream: same swatches, same onPainted callback, same status
 * text. So the assertions below are "did the network happen" plus "did the colour land", never "does the
 * viewer get a different UI".
 */

const RIDERS: readonly RaceResultRow[] = [
	makeRow({ participantId: 11, index: 0, name: 'Corporate Crushers', category: 'Corporate' }),
	makeRow({ participantId: 22, index: 1, name: 'Open Otters', category: 'Open' }),
	makeRow({ participantId: 33, index: 2, name: 'My Own Ride', category: 'Open', isReal: true }),
];

function makeRow(over: Partial<RaceResultRow> & { participantId: number; index: number }): RaceResultRow {
	return {
		name: `Rider ${over.index}`, bib: null, category: null, gender: null, club: null,
		discipline: null, isReal: false, color: null, emphasised: false, calloutOff: false, position: over.index + 1, finished: true, status: 'finished', statusRaw: null,
		accumulatedSeconds: 3600, officialSeconds: 3600, deltaSeconds: 0, agreement: 'match',
		legSeconds: [],
		...over,
	};
}

const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

interface Mounted {
	toolbar: HTMLElement;
	painted: Map<number, string>[];
	emphasised: Map<number, boolean>[];
	modes: RaceMapMode[];
	/** Every value the table reported through onDirtyChange, in order. */
	dirtyFlags: boolean[];
	saveButton: () => HTMLButtonElement | null;
	save: () => Promise<void>;
	status: () => string;
	/** The label the emphasis toggle currently shows — it follows the filtered set, not the last click. */
	emphasisLabel: () => string;
	paint: (color?: string) => Promise<void>;
	clear: () => Promise<void>;
	toggleEmphasis: () => Promise<void>;
	pickMode: (label: string) => Promise<void>;
	modeValue: () => string;
	filterDivision: (value: string) => Promise<void>;
}

/** Rows are cloned per mount: paint writes onto them, so a shared fixture would leak between tests. */
async function mountTable(paintMode: PaintMode, over: { dirty?: boolean } = {}): Promise<Mounted> {
	const mount = document.createElement('div');
	const toolbar = document.createElement('div');
	document.body.append(toolbar, mount);

	const painted: Map<number, string>[] = [];
	const emphasised: Map<number, boolean>[] = [];
	const modes: RaceMapMode[] = [];
	const dirtyFlags: boolean[] = [];
	initRaceResultsTable({
		raceId: 7,
		mount,
		toolbarMount: toolbar,
		rows: RIDERS.map((r) => ({ ...r })),
		legs: [],
		paintMode,
		dirty: over.dirty === true,
		onFiltered: () => {},
		onRowClick: () => {},
		onPainted: (colors) => painted.push(colors),
		onEmphasised: (flags) => emphasised.push(flags),
		onModeChange: (mode) => modes.push(mode),
		onDirtyChange: (value) => dirtyFlags.push(value),
	});
	await flush();   // the grid's initial query

	const control = <T extends HTMLElement>(selector: string): T => {
		const found = toolbar.querySelector<T>(selector);
		if (!found) throw new Error(`no "${selector}" in the toolbar`);
		return found;
	};

	return {
		toolbar,
		painted,
		emphasised,
		modes,
		dirtyFlags,
		saveButton: () => toolbar.querySelector<HTMLButtonElement>('.race-save'),
		async save() {
			control<HTMLButtonElement>('.race-save').click();
			await flush();
		},
		status: () => toolbar.querySelector('.race-table-status')?.textContent ?? '',
		emphasisLabel: () => toolbar.querySelector('.map-dd-toggle-text')?.textContent ?? '',
		modeValue: () => toolbar.querySelector('.map-dd-value')?.textContent ?? '',
		// Picking the colour IS the paint — `change` is what a committed pick fires.
		async paint(color = '#00ff00') {
			const input = control<HTMLInputElement>('.race-color-input');
			input.value = color;
			input.dispatchEvent(new Event('change'));
			await flush();
		},
		async clear() {
			control<HTMLButtonElement>('[aria-label="Clear colours"]').click();
			await flush();
		},
		async toggleEmphasis() {
			control<HTMLButtonElement>('.map-dd-toggle').click();
			await flush();
		},
		async pickMode(label: string) {
			const item = [...toolbar.querySelectorAll<HTMLButtonElement>('.map-dd-item')]
				.find((b) => b.textContent === label);
			if (!item) throw new Error(`no "${label}" item in the mode picker`);
			item.click();
			await flush();
		},
		// The combo popup applies on change, undebounced — unlike its text box, which waits 250 ms.
		async filterDivision(value: string) {
			const box = mount.querySelector<HTMLInputElement>(`input[type="checkbox"][value="${value}"]`);
			if (!box) throw new Error(`no "${value}" option in the Division filter`);
			box.checked = true;
			box.dispatchEvent(new Event('change'));
			await flush();
		},
	};
}

beforeEach(() => vi.stubGlobal('fetch', vi.fn(async () =>
	new Response(JSON.stringify({ painted: 3 }), { headers: { 'Content-Type': 'application/json' } }))));

afterEach(() => {
	vi.unstubAllGlobals();
	document.body.innerHTML = '';
});

describe('paint controls', () => {
	it('render for a viewer who cannot persist — the field is unreadable without them', async () => {
		const table = await mountTable('local');

		expect(table.toolbar.querySelector('.race-color-input')).not.toBeNull();
		expect(table.toolbar.querySelector('[aria-label="Clear colours"]')).not.toBeNull();
	});

	it('paint on pick: a committed colour needs no second click to land', async () => {
		const table = await mountTable('local');

		await table.paint('#00ff00');

		expect(table.painted).toHaveLength(1);
		expect(table.status()).toBe('Painted 3 rider(s).');
	});

});

describe('map-mode picker', () => {
	it('opens on Highlight — the mode a replay is read in', async () => {
		const table = await mountTable('local');

		expect(table.modeValue()).toBe('Highlight');
	});

	it('reports the picked mode and moves the summary with it', async () => {
		const table = await mountTable('local');

		await table.pickMode('Hide');

		expect(table.modes).toEqual(['hide']);
		expect(table.modeValue()).toBe('Hide');
	});
});

describe('paintMode: local', () => {
	it('colours the field without touching the network', async () => {
		const table = await mountTable('local');

		await table.paint('#00ff00');

		expect(fetch).not.toHaveBeenCalled();
		// Field INDEX → colour: what the markers and the charts are addressed by, not the participant id.
		expect(table.painted).toEqual([new Map([[0, '#00ff00'], [1, '#00ff00'], [2, '#00ff00']])]);
	});

	it('reports the paint exactly as an editor sees it reported', async () => {
		const table = await mountTable('local');

		await table.paint();

		// No "this view only" note by design: a viewer's paint IS the feature, not a degraded copy of it.
		expect(table.status()).toBe('Painted 3 rider(s).');
	});

	it('paints only the filtered set — the whole point of having no colour-by dropdown', async () => {
		const table = await mountTable('local');

		await table.filterDivision('Corporate');
		await table.paint('#0000ff');

		expect(table.painted).toEqual([new Map([[0, '#0000ff']])]);
		expect(table.status()).toBe('Painted 1 rider(s).');
	});

	it('clears back to the build-time default, per rider kind', async () => {
		const table = await mountTable('local');

		await table.paint('#00ff00');
		await table.clear();

		expect(fetch).not.toHaveBeenCalled();
		// A cleared rider is not "uncoloured": he returns to the simulated/real default the map draws him
		// with, which is why defaultColorFor and not null comes back through onPainted.
		expect(table.painted[1]).toEqual(
			new Map([[0, SIMULATED_COLOR], [1, SIMULATED_COLOR], [2, REAL_COLOR]]));
		expect(table.status()).toBe('Cleared 3 rider(s).');
	});
});

/**
 * Staged mode — the owner.
 *
 * The paint used to reach the DB on every action. Reading a 500-team field means painting a division,
 * looking, repainting and clearing, so that trail of experiments was being written; now it stays in the
 * browser and ONE button commits the answer. What these pin is that the write moved, not that it went:
 * nothing reaches the wire until Save, and Save carries the whole field.
 */
describe('staged mode (the owner)', () => {
	it('paints without touching the network — that is the entire point', async () => {
		const table = await mountTable('staged');

		await table.paint('#00ff00');
		await table.toggleEmphasis();

		expect(fetch).not.toHaveBeenCalled();
		expect(table.painted).toEqual([new Map([[0, '#00ff00'], [1, '#00ff00'], [2, '#00ff00']])]);
	});

	it('offers nothing to save until something changes', async () => {
		const table = await mountTable('staged');

		expect(table.saveButton()?.disabled).toBe(true);

		await table.paint('#00ff00');

		expect(table.saveButton()?.disabled).toBe(false);
	});

	it('writes one request per distinct colour, plus the two emphasis halves', async () => {
		const table = await mountTable('staged');

		await table.filterDivision('Corporate');
		await table.paint('#0000ff');        // one rider
		await table.toggleEmphasis();        // the same one
		await table.save();

		const calls = vi.mocked(fetch).mock.calls.map(([url, init]) =>
			`${String(url)} ${String((init as RequestInit).body)}`);
		// Two colours in the field now — the painted rider and the two still on their default — so two
		// colour writes, and both emphasis halves because the field is split across them too.
		expect(calls).toEqual([
			'/api/RacesApi/7/participants/color {"participantIds":[11],"color":"#0000ff"}',
			'/api/RacesApi/7/participants/color {"participantIds":[22,33],"color":null}',
			'/api/RacesApi/7/participants/emphasis {"participantIds":[11],"emphasised":true}',
			'/api/RacesApi/7/participants/emphasis {"participantIds":[22,33],"emphasised":false}',
		]);
	});

	it('goes clean once the write lands, and says what it saved', async () => {
		const table = await mountTable('staged');

		await table.paint('#00ff00');
		await table.save();

		expect(table.status()).toBe('Saved 3 rider(s).');
		expect(table.saveButton()?.disabled).toBe(true);
		expect(table.dirtyFlags).toEqual([true, false]);
	});

	it('stays dirty when the write fails — the colours are on screen and NOT in the race', async () => {
		vi.stubGlobal('fetch', vi.fn(async () => new Response(
			JSON.stringify({ code: 'not_found', message: 'No such race.' }),
			{ status: 404, headers: { 'Content-Type': 'application/json' } })));
		const table = await mountTable('staged');

		await table.paint('#00ff00');
		await table.save();

		expect(table.status()).toContain('Save failed');
		expect(table.saveButton()?.disabled).toBe(false);
		expect(table.dirtyFlags).toEqual([true]);
	});

	it('inherits unsaved changes across a re-mount — the field rebuild must not lose them', async () => {
		// rebuildField() destroys this table and mounts a new one over the same rows. The flag is the
		// caller's, so the new mount comes up dirty and the Save button keeps advertising it.
		const table = await mountTable('staged', { dirty: true });

		expect(table.saveButton()?.disabled).toBe(false);
	});
});

describe('a viewer (local mode)', () => {
	it('gets no Save button at all — its paint has nowhere to go', async () => {
		const table = await mountTable('local');

		expect(table.saveButton()).toBeNull();
		// …but every control that makes the field readable is still there.
		expect(table.toolbar.querySelector('.race-color-input')).not.toBeNull();
		expect(table.toolbar.querySelector('.map-dd-toggle')).not.toBeNull();
	});

	it('never reports a dirty flag — there is nothing to save to', async () => {
		const table = await mountTable('local');

		await table.paint('#00ff00');

		expect(table.dirtyFlags).toEqual([]);
	});
});

/**
 * The SECOND presentation channel. It rides the same filtered set and the same persist/local switch as
 * the paint, so what these pin is that it is genuinely independent of it: its own endpoint, its own
 * callback, and no colour touched on the way past.
 */
describe('emphasis', () => {
	it('renders its control for a viewer too — same rule as the paint', async () => {
		const table = await mountTable('local');

		expect(table.toolbar.querySelector('.map-dd-toggle')).not.toBeNull();
		expect(table.emphasisLabel()).toBe('Emphasise');
	});

	it('reads its state off the FILTERED set, not off the last click', async () => {
		// One toggle replaced the Emphasise/Un-emphasise pair, so what it offers has to follow the set
		// under it: filtering to an already-emphasised group must arrive showing Un-emphasise, with no
		// click involved. Remembering the last action instead would offer to emphasise them twice.
		const table = await mountTable('local');

		await table.filterDivision('Corporate');
		await table.toggleEmphasis();
		expect(table.emphasisLabel()).toBe('Un-emphasise');

		await table.filterDivision('Open');   // now spans an emphasised rider and two plain ones
		expect(table.emphasisLabel()).toBe('Emphasise');
	});

	it('local mode emphasises the filtered set without touching the network', async () => {
		const table = await mountTable('local');

		await table.filterDivision('Corporate');
		await table.toggleEmphasis();

		expect(fetch).not.toHaveBeenCalled();
		// Field INDEX → on, exactly as onPainted is addressed.
		expect(table.emphasised).toEqual([new Map([[0, true]])]);
		expect(table.status()).toBe('Emphasised 1 rider(s).');
	});

	it('saves to its OWN endpoint, not the colour one', async () => {
		const table = await mountTable('staged');

		await table.toggleEmphasis();
		await table.save();

		expect(fetch).toHaveBeenCalledWith('/api/RacesApi/7/participants/emphasis', expect.objectContaining({
			method: 'PUT',
			body: JSON.stringify({ participantIds: [11, 22, 33], emphasised: true }),
		}));
		expect(table.status()).toBe('Saved 3 rider(s).');
	});

	it('toggling an already-emphasised set sends the flag OFF rather than a second on', async () => {
		const table = await mountTable('staged');

		await table.toggleEmphasis();   // on
		await table.toggleEmphasis();   // the whole set is emphasised now, so this is the off half
		await table.save();

		// One save, so the flag is written once — as the state the user landed on, not as two events.
		const emphasisCalls = vi.mocked(fetch).mock.calls
			.filter(([url]) => String(url).endsWith('/emphasis'))
			.map(([, init]) => String((init as RequestInit).body));
		expect(emphasisCalls).toEqual([JSON.stringify({ participantIds: [11, 22, 33], emphasised: false })]);
		expect(table.emphasised.at(-1)).toEqual(new Map([[0, false], [1, false], [2, false]]));
	});

	it('reports the size of the set it changed', async () => {
		const table = await mountTable('staged');

		await table.filterDivision('Corporate');
		await table.toggleEmphasis();

		// The filtered set's own size: nothing was asked of the server, so there is no smaller number
		// it could come back with. That reconciliation moved to the save.
		expect(table.status()).toBe('Emphasised 1 rider(s).');
	});

	it('does not disturb the paint — the two channels are independent', async () => {
		const table = await mountTable('local');

		await table.paint('#00ff00');
		await table.toggleEmphasis();

		expect(table.painted).toHaveLength(1);   // emphasising did not fire a second repaint
		expect(table.emphasised).toEqual([new Map([[0, true], [1, true], [2, true]])]);
	});
});
