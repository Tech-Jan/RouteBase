/**
 * The results table — and the race's colour UI.
 *
 * Filter the table (division = Corporate, gender = female, and the filters COMPOSE), then press Paint:
 * every rider currently passing the filter takes that colour, on the map and in the charts. That is why
 * there is no "colour by category" dropdown anywhere in this feature — a dropdown can only key on ONE
 * field and would need a 16-colour palette for Ski to Sea's divisions. Painting the filtered set needs
 * no palette at all, and composes.
 *
 * Emphasis is the SECOND channel on the same filtered set: bigger dots, drawn in front of the field.
 * Colour alone runs out at 508 riders — a painted group is still the same 4px dot, and can be covered by
 * whoever is drawn after it. So paint answers "which group is this", emphasis answers "watch THESE".
 *
 * Paint is how you READ a 508-team field, not an edit of it, so everyone gets it — including the anonymous
 * /s/{token} visitor. `paintMode` is the only axis the two differ on: `persist` writes the colour to the
 * DB, `local` keeps it in this browser, this session. Everything downstream (markers, charts, swatches,
 * status text) is identical either way, deliberately. Emphasis rides the same switch.
 *
 * The table also drives the map: its filtered set dims or isolates the markers (race-map-display), and a
 * row click follows that rider.
 */

import { GridView } from '../core/grid/grid-view';
import { attachTopScrollbar } from '../core/grid/grid-top-scrollbar';
import { setupMapToolbar } from '../core/map-toolbar';
import { InMemoryGridDataSource } from '../core/grid/grid-data-source';
import type { GridDataResponse } from '../core/grid/grid-types';
import { setParticipantColors, setParticipantEmphasis } from './race-api';
import { buildResultSchema, defaultColorFor } from './race-results-columns';
import type { RaceLegColumn, RaceResultRow } from './race-results-row';
import type { RaceMapMode } from './race-map-display';

/**
 * Whether this table can WRITE what it paints.
 *
 * Both modes apply the paint the same way and immediately — the difference is only what happens next.
 * `staged` renders a Save button; `local` (the anonymous /s/{token} viewer) has no way to persist and
 * loses its colouring on reload, which is the intended read-the-field-yourself behaviour, not a
 * degraded copy of the owner's.
 */
export type PaintMode = 'staged' | 'local';

export interface RaceResultsTableOptions {
	readonly raceId: number;
	readonly mount: HTMLElement;
	readonly toolbarMount?: HTMLElement | undefined;
	readonly rows: RaceResultRow[];
	readonly legs: readonly RaceLegColumn[];
	/**
	 * Whether a paint can be SAVED. The paint controls render either way — the field is unreadable
	 * without them — and neither mode writes on the paint itself.
	 */
	readonly paintMode: PaintMode;
	/**
	 * Unsaved changes this mount inherits. Held by the caller because rebuildField() destroys and
	 * re-mounts the table: a flag owned in here would come back clean with the changes still unsaved,
	 * and the Save button would lie.
	 */
	readonly dirty?: boolean;
	/** The unsaved-changes flag moved. Only fires in `staged` mode. */
	readonly onDirtyChange?: ((dirty: boolean) => void) | undefined;

	/** The filtered rows, as FIELD INDICES — what the markers are addressed by. */
	readonly onFiltered: (indices: number[], filtering: boolean) => void;
	/** A row click — follow that rider on the map. */
	readonly onRowClick: (index: number) => void;
	/** Riders were repainted: index → colour. Push it to the markers. */
	readonly onPainted: (colors: Map<number, string>) => void;
	/** Riders were emphasised or un-emphasised: index → on. Push it to the markers. */
	readonly onEmphasised: (flags: Map<number, boolean>) => void;
	readonly onModeChange: (mode: RaceMapMode) => void;
}

export interface RaceResultsTable {
	destroy(): void;
}

/** The .map-dd summary chevron, matching the Style/Names dropdowns above the map. */
const CHEVRON_ICON =
	'<svg class="map-dd-chevron" width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor"'
	+ ' stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'
	+ '<polyline points="6 9 12 15 18 9"/></svg>';

const MAP_MODES: ReadonlyArray<{ mode: RaceMapMode; label: string; title: string }> = [
	{ mode: 'all', label: 'All', title: 'The table filter does not touch the map' },
	{ mode: 'highlight', label: 'Highlight', title: 'Filtered riders stay bright; the rest recede' },
	{ mode: 'hide', label: 'Hide', title: 'Show only the filtered riders' },
];

export function initRaceResultsTable(options: RaceResultsTableOptions): RaceResultsTable {
	const { raceId, mount, rows, legs, paintMode } = options;

	// Which optional columns start shown is decided by THIS race's data, not a constant — see
	// ResultSchema. A column that ships hidden also ships with no filter control.
	const { columns, defaultVisibleKeys } = buildResultSchema(rows, legs);
	const dataSource = new InMemoryGridDataSource<RaceResultRow>(rows, {
		getId: (r) => r.participantId,
		columns,
	});

	// filteredIds are participant ids (the grid keys rows by them, and the paint endpoint takes them);
	// the MAP is addressed by field index. Rows carry both, so the translation is a lookup, not a search.
	//
	// A row's index is NULL for an entrant who never took the course (a DNS): he has a row and no dot.
	// Every read below therefore drops him rather than defaulting — index 0 is the leader, and a default
	// here would silently follow, paint and dim the wrong rider.
	const indexOf = new Map(rows.map((r) => [r.participantId, r.index]));
	let filteredIds: number[] = rows.map((r) => r.participantId);

	// Emphasis is a TOGGLE, so its state is read off the filtered set rather than remembered: a filter
	// change can move it from "all emphasised" to "none" with no click at all. A seam rather than a
	// direct call because the controls mount AFTER the grid (GridView clears the toolbar it is given),
	// and onData can fire first.
	let syncEmphasis: (() => void) | null = null;

	const grid = new GridView<RaceResultRow>({
		mount,
		columns,
		dataSource,
		defaultSort: { column: 'pos', direction: 'asc' },
		visibleKeys: new Set(defaultVisibleKeys),
		defaultVisibleKeys,
		getRowId: (r) => r.participantId,
		toolbarMount: options.toolbarMount,
		toolbar: { count: true, resetFilters: true, resetAll: true, columns: true },
		persistWidthsKey: 'race-results-v1',
		emptyMessage: 'No riders match the current filters.',
		onData: (res: GridDataResponse<RaceResultRow>, query) => {
			filteredIds = res.filteredIds;
			syncEmphasis?.();
			const filtering = Object.values(query.filters).some(
				(v) => v && (v.text?.trim() || v.selected?.length || v.expr?.trim() || v.from || v.to),
			);
			options.onFiltered(
				filteredIds.flatMap((id) => { const i = indexOf.get(id); return i == null ? [] : [i]; }),
				Boolean(filtering),
			);
		},
		onRowClick: (_id, row) => { if (row.index != null) options.onRowClick(row.index); },
	});

	// The wrapper is the horizontal scroll container, not the mount — a race with eight legs is far wider
	// than a phone, and without this the columns past the fold are simply unreachable (.race-panel clips).
	// Mirror its scrollbar above the table too: this grid has no fixed-height window, so the native one
	// sits 500 rows down the page. Same seam as the Activities grid and the segment efforts table.
	const scroller = mount.closest<HTMLElement>('.grid-table-wrapper');
	const table = mount.querySelector('table');
	const detachTopScrollbar = scroller && table ? attachTopScrollbar(scroller, table) : null;

	const controls = options.toolbarMount ? mountControls(options.toolbarMount, paintMode === 'staged') : null;

	// Only .active moves — the shell's observer copies the chosen item's label into the summary and
	// closes the menu, so the control cannot end up showing a mode the map is not in.
	controls?.modeItems.forEach((item, mode) => {
		item.addEventListener('click', () => {
			controls.modeItems.forEach((el, m) => el.classList.toggle('active', m === mode));
			options.onModeChange(mode);
		});
	});

	// `every` on an empty set is true, so the size check comes first: no riders means nothing to un-emphasise.
	const rowById = new Map(rows.map((r) => [r.participantId, r]));
	const filteredEmphasised = (): boolean =>
		filteredIds.length > 0 && filteredIds.every((id) => rowById.get(id)?.emphasised === true);

	if (controls) {
		const button = controls.emphasisButton;
		syncEmphasis = (): void => {
			const on = filteredEmphasised();
			button.classList.toggle('is-active', on);
			button.setAttribute('aria-pressed', String(on));
			const text = button.querySelector('.map-dd-toggle-text');
			if (text) text.textContent = on ? 'Un-emphasise' : 'Emphasise';
			button.title = on
				? 'Return the filtered riders to the size of the field'
				: 'Draw every rider passing the filter bigger, and in front of the field';
		};
		syncEmphasis();
	}

	// ── Unsaved changes ────────────────────────────────────────────────────────────
	// The paint is local in both modes, so `staged` is simply the mode that can also write it back.
	const savable = paintMode === 'staged';
	let dirty = savable && options.dirty === true;
	let saving = false;

	function setDirty(next: boolean): void {
		if (!savable || next === dirty) return;
		dirty = next;
		options.onDirtyChange?.(next);
		armUnloadGuard(next);
		syncSave();
	}

	function syncSave(): void {
		const button = controls?.saveButton;
		if (!button) return;
		button.disabled = !dirty || saving;
		button.classList.toggle('race-save--dirty', dirty && !saving);
		const text = button.querySelector('.race-save-text');
		if (text) text.textContent = dirty ? 'Save' : 'Saved';
		button.title = dirty
			? 'Write these colours and this emphasis to the race'
			: 'Nothing to save — the race matches what you see';
	}

	/**
	 * Warn before leaving with paint that only exists in this browser.
	 *
	 * Armed ONLY while dirty: a registered beforeunload listener disqualifies the page from bfcache for
	 * its whole lifetime, and this page is restored from it (race-replay-view re-asserts colours and
	 * emphasis on a bfcache restore). Same rule as /Record's LR-04 guard.
	 */
	const onBeforeUnload = (event: BeforeUnloadEvent): void => {
		event.preventDefault();
		event.returnValue = '';   // legacy browsers require a truthy returnValue to show the prompt
	};
	let guardArmed = false;
	function armUnloadGuard(on: boolean): void {
		if (on === guardArmed) return;
		guardArmed = on;
		if (on) window.addEventListener('beforeunload', onBeforeUnload);
		else window.removeEventListener('beforeunload', onBeforeUnload);
	}

	syncSave();
	armUnloadGuard(dirty);   // a re-mount inherits the flag, so it must inherit the warning too

	/**
	 * Paint the filtered set — in the browser, and only there.
	 *
	 * Nothing is written here in either mode. An editor reads a 500-team field the same way a viewer
	 * does: paint a division, look, repaint, clear — a trail of experiments that has no business in the
	 * DB. `save()` below is what commits the answer he settles on.
	 */
	function paint(color: string | null): void {
		if (!controls) return;
		const ids = filteredIds;
		if (ids.length === 0) return;

		// Keep the in-memory rows in step, or the swatches lie until the next reload.
		// A Set, not ids.includes: this loop is O(field) and the filtered set can be the whole 508.
		const painted = new Set(ids);
		const repainted = new Map<number, string>();
		for (const row of rows) {
			if (!painted.has(row.participantId)) continue;
			(row as { color: string | null }).color = color;
			// The swatch still updates for a rider with no dot — only the MARKER repaint needs an index.
			if (row.index != null) repainted.set(row.index, defaultColorFor(row));
		}
		options.onPainted(repainted);
		grid.redrawRows();   // the query is unchanged — only the swatches need to catch up
		setDirty(true);

		controls.status.textContent = color
			? `Painted ${ids.length} rider(s).`
			: `Cleared ${ids.length} rider(s).`;
	}

	/**
	 * The second presentation channel: bigger dots, drawn in front of the field.
	 *
	 * Its own action rather than a colour that happens to be fat, because the two questions are
	 * independent — you paint a division to read the race, and emphasise two riders inside it to watch
	 * them. Same filtered set, same staging, same status line.
	 */
	function emphasise(on: boolean): void {
		if (!controls) return;
		const ids = filteredIds;
		if (ids.length === 0) return;

		const changed = new Set(ids);
		const byIndex = new Map<number, boolean>();
		for (const row of rows) {
			if (!changed.has(row.participantId)) continue;
			(row as { emphasised: boolean }).emphasised = on;
			// Same rule as the paint: a DNS has a row and no dot, so only a real index reaches the map.
			if (row.index != null) byIndex.set(row.index, on);
		}
		options.onEmphasised(byIndex);
		grid.redrawRows();   // the swatch carries the emphasis marker — see swatchCell
		syncEmphasis?.();    // the set just became (un)emphasised — the toggle must say so
		setDirty(true);

		controls.status.textContent = on
			? `Emphasised ${ids.length} rider(s).`
			: `Cleared emphasis on ${ids.length} rider(s).`;
	}

	/**
	 * Commit the field: one PUT per distinct colour, plus the two emphasis halves.
	 *
	 * The whole field rather than a diff of it. There is no baseline to diff against — the local paint
	 * writes back onto the payload objects the rows are rebuilt from (race-replay-view's RF-02 seam), so
	 * a "what the server has" snapshot would have to be captured before the first paint AND survive the
	 * field rebuild. Six-ish idempotent writes are cheaper than owning that.
	 *
	 * Sequential, not Promise.all: every body carries up to 500 ids and they all land on the same table.
	 */
	async function save(): Promise<void> {
		const button = controls?.saveButton;
		if (!controls || !button || saving) return;

		const byColor = new Map<string | null, number[]>();
		const emphasisOn: number[] = [];
		const emphasisOff: number[] = [];
		for (const row of rows) {
			const group = byColor.get(row.color);
			if (group) group.push(row.participantId);
			else byColor.set(row.color, [row.participantId]);
			(row.emphasised ? emphasisOn : emphasisOff).push(row.participantId);
		}

		saving = true;
		syncSave();
		controls.status.textContent = 'Saving…';
		try {
			let painted = 0;
			for (const [color, ids] of byColor) {
				painted += (await setParticipantColors(raceId, ids, color)).painted;
			}
			if (emphasisOn.length > 0) await setParticipantEmphasis(raceId, emphasisOn, true);
			if (emphasisOff.length > 0) await setParticipantEmphasis(raceId, emphasisOff, false);
			saving = false;
			setDirty(false);
			controls.status.textContent = `Saved ${painted} rider(s).`;
		} catch (error) {
			// Still dirty: the colours are on screen and NOT in the race, which is exactly the state the
			// button has to keep advertising.
			saving = false;
			controls.status.textContent = `Save failed: ${(error as Error).message}`;
		}
		syncSave();
	}

	// `change`, never `input`: the latter fires per pixel of drag inside the colour dialog, and each one
	// would repaint every row in the filtered set.
	controls?.colorInput.addEventListener('change', () => paint(controls.colorInput.value));
	controls?.clearButton.addEventListener('click', () => paint(null));
	controls?.emphasisButton.addEventListener('click', () => emphasise(!filteredEmphasised()));
	controls?.saveButton?.addEventListener('click', () => void save());

	return {
		destroy() {
			// The flag lives on in the caller (onDirtyChange) — only this mount's listener goes.
			armUnloadGuard(false);
			controls?.teardown();
			controls?.root.remove();
			detachTopScrollbar?.();
		},
	};
}

interface Controls {
	root: HTMLElement;
	status: HTMLElement;
	modeItems: Map<RaceMapMode, HTMLButtonElement>;
	colorInput: HTMLInputElement;
	clearButton: HTMLButtonElement;
	emphasisButton: HTMLButtonElement;
	/** null for a viewer: nothing it could write, so nothing to render. */
	saveButton: HTMLButtonElement | null;
	/** Drops the map-toolbar shell's document-level outside-click listener. */
	teardown: () => void;
}

/**
 * The map-mode picker, the colour, and the emphasis toggle, appended to the grid's own toolbar so they
 * sit beside its count and Columns picker rather than in a second bar competing with it.
 *
 * This row used to be seven text controls — All | Highlight | Hide, a colour, "Paint filtered",
 * "Clear", "Emphasise", "Un-emphasise" — about 520 px that a phone simply cut off. Three things went:
 * the mode switch became a dropdown (the same .map-dd shell as the Style menu above the map, so a
 * >2-way choice looks the same everywhere in this slice), the colour paints on pick rather than arming
 * a button, and the emphasis pair became ONE toggle that reads its state off the filtered set.
 */
function mountControls(toolbar: HTMLElement, savable: boolean): Controls {
	const root = document.createElement('div');
	root.className = 'race-table-controls';

	const dropdown = document.createElement('details');
	dropdown.className = 'map-dd map-dd-end';
	// The shell only tracks a value for a dropdown that declares itself a picker.
	dropdown.setAttribute('data-picker', 'true');

	const summary = document.createElement('summary');
	summary.className = 'map-dd-summary';
	// .map-dd-value is filled by setupMapToolbar from whichever item carries .active — it is not written
	// here, so the label cannot drift from the mode.
	// eslint-disable-next-line no-unsanitized/property -- CHEVRON_ICON is a module-const SVG literal; no input reaches this
	summary.innerHTML = `<span class="map-dd-label">Filter</span><span class="map-dd-value"></span>${CHEVRON_ICON}`;

	const menu = document.createElement('div');
	menu.className = 'map-dd-menu';
	menu.setAttribute('role', 'group');
	menu.setAttribute('aria-label', 'What the filter does to the map');

	const modeItems = new Map<RaceMapMode, HTMLButtonElement>();
	for (const { mode, label, title } of MAP_MODES) {
		const item = document.createElement('button');
		item.type = 'button';
		item.className = 'map-dd-item';
		item.textContent = label;
		item.title = title;
		// Highlight is the default: a replay is about GAPS, so hiding the field you are measured against
		// destroys the thing you came to see.
		if (mode === 'highlight') item.classList.add('active');
		modeItems.set(mode, item);
		menu.appendChild(item);
	}
	dropdown.append(summary, menu);

	const colorInput = document.createElement('input');
	colorInput.type = 'color';
	colorInput.className = 'race-color-input';
	colorInput.value = '#e64980';
	colorInput.title = 'Pick a colour to paint every rider currently passing the filter';
	colorInput.setAttribute('aria-label', 'Paint the filtered riders');

	// Clearing is not a colour, so it cannot come from the picker — it stays its own control, sized and
	// shaped like the grid's own reset icons two slots to the left.
	const clearButton = document.createElement('button');
	clearButton.type = 'button';
	clearButton.className = 'grid-toolbar-btn grid-toolbar-btn--icon';
	// eslint-disable-next-line no-unsanitized/property -- module-const markup, nothing interpolated
	clearButton.innerHTML = '<i class="bi bi-eraser"></i>';
	clearButton.title = 'Reset the filtered riders to their default colour';
	clearButton.setAttribute('aria-label', 'Clear colours');

	// Label and pressed state are set by the caller's syncEmphasis — the toggle reflects the filtered
	// set, which a filter change can flip without anyone touching this button.
	const emphasisButton = document.createElement('button');
	emphasisButton.type = 'button';
	emphasisButton.className = 'map-dd-toggle';
	// eslint-disable-next-line no-unsanitized/property -- module-const markup, nothing interpolated
	emphasisButton.innerHTML =
		'<i class="bi bi-star-fill map-dd-toggle-icon"></i><span class="map-dd-toggle-text">Emphasise</span>';

	// Only where a write is possible. A viewer's paint is real and complete — it just has nowhere to go,
	// and a disabled Save telling him so would be chrome pretending to be a choice.
	let saveButton: HTMLButtonElement | null = null;
	if (savable) {
		saveButton = document.createElement('button');
		saveButton.type = 'button';
		saveButton.className = 'grid-toolbar-btn race-save';
		// eslint-disable-next-line no-unsanitized/property -- module-const markup, nothing interpolated
		saveButton.innerHTML = '<i class="bi bi-save"></i><span class="race-save-text">Saved</span>';
		saveButton.setAttribute('aria-label', 'Save colours and emphasis');
	}

	const status = document.createElement('span');
	status.className = 'race-table-status';

	root.append(dropdown, colorInput, clearButton, emphasisButton);
	if (saveButton) root.appendChild(saveButton);
	root.appendChild(status);
	toolbar.appendChild(root);

	// Scoped to this root: the replay page runs its own setup over the map's toolbar, and the two must
	// not adopt each other's dropdowns.
	const teardown = setupMapToolbar({ root });

	return { root, status, modeItems, colorInput, clearButton, emphasisButton, saveButton, teardown };
}
