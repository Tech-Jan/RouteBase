/**
 * shape-filter/shape-filter-panel.ts — the control UI.
 *
 * DOM only: renders the toolbar (activate, draw zone/gate/select), one context-sensitive
 * selection row (edits the focused shape's role/direction, or the next-drawn shape's draft
 * defaults), the adopt-filters row, the effort-extent dropdown, the live summary sentence,
 * and the save/heatmap rows. It emits user intents via callbacks and subscribes to nothing —
 * the orchestrator pushes all state in through the setters.
 *
 * The many role/direction/extent buttons of the old panel collapsed into dropdowns
 * (`panel-dropdown.ts`): one role select re-skinned per shape kind, a gate direction select,
 * and an extent select whose invalid options are greyed with a "why" tooltip.
 */

import type { CycleInfo, Tool } from './shape-interactions';
import type { CrossDirection, GateRole, MatchMode, Shape, ZoneRole } from './shape-model';
import { AdoptFiltersRow } from './adopt-filters-row';
import { DEFAULT_GATE_DEBOUNCE_SECONDS, type GridFiltersMap } from './rule-document';
import { buildSelect, setOptionDisabled, setSelectOptions, setSelectValue, type SelectOption } from './panel-dropdown';
import { createInlineError, type InlineError } from '../../core/inline-error';

export interface PanelDeps {
	onToggleActive: (active: boolean) => void;
	onToolChange: (tool: Tool) => void;
	onZoneRoleChange: (role: ZoneRole) => void;
	onGateRoleChange: (role: GateRole) => void;
	onGateDirectionChange: (direction: CrossDirection) => void;
	onMatchModeChange: (mode: MatchMode) => void;
	onGateDebounceChange: (seconds: number) => void;
	onAdoptedFiltersChange: () => void;
	onDeleteSelected: () => void;
	onClear: () => void;
	onSave: () => void;
}

/** Heatmap intent read off the panel at save time. */
export interface HeatmapOptions {
	/** Whether the user opted to also generate a heatmap. */
	enabled: boolean;
	/** Activity types to include in tiles (empty → all). */
	types: string[];
}

const ZONE_ROLE_OPTIONS: readonly SelectOption[] = [
	{ value: 'any', label: 'Any of', title: 'Track must pass through at least one "any of" zone (per group)' },
	{ value: 'all', label: 'Must pass', title: 'Track must pass through every "must pass" zone' },
	{ value: 'avoid', label: '⛔ Avoid', title: 'Tracks touching this zone are excluded' },
	{ value: 'within', label: '⬚ Stay inside', title: 'The effort must never leave the "stay inside" zones' },
	{ value: 'start', label: '🚩 Starts in', title: "The track's start point must lie inside this zone" },
	{ value: 'finish', label: '🏁 Ends in', title: "The track's end point must lie inside this zone" },
	{ value: 'either', label: '⚑ Either end', title: "The track's start OR end point must lie inside this zone" },
];

const GATE_ROLE_OPTIONS: readonly SelectOption[] = [
	{ value: 'start', label: '🚩 Start' },
	{ value: 'finish', label: '🏁 Finish' },
	{ value: 'both', label: '🚩🏁 Both (lap)', title: 'This gate is both start and finish — defines a lap course' },
];

const DIRECTION_OPTIONS: readonly SelectOption[] = [
	{ value: 'any', label: '↔ Any way' },
	{ value: 'forward', label: '→ Forward only' },
	{ value: 'backward', label: '← Backward only' },
];

const EXTENT_OPTIONS: readonly SelectOption[] = [
	{ value: 'whole', label: 'Whole track' },
	{ value: 'clip', label: 'Clip to zones' },
	{ value: 'gates', label: 'Between gates' },
];

/** Same-gate crossing debounce. A document constant until now, invisible and untunable: two real
 * crossings closer than the window merge into one, so a short lap silently under-counts. Presets
 * rather than free text — no parsing, no range validation, and the server's default is the middle. */
const GATE_DEBOUNCE_OPTIONS: readonly SelectOption[] = [
	{ value: '0', label: 'Off', title: 'Count every crossing, however close together' },
	{ value: '5', label: '5 s' },
	{ value: '15', label: '15 s (default)' },
	{ value: '30', label: '30 s' },
	{ value: '60', label: '60 s', title: 'Long window — use when GPS noise re-crosses the line' },
];

export class ShapeFilterPanel {
	private readonly activateBtn: HTMLButtonElement;
	private readonly error: InlineError;
	private readonly controls: HTMLDivElement;
	private readonly drawZoneBtn: HTMLButtonElement;
	private readonly drawGateBtn: HTMLButtonElement;
	private readonly selectBtn: HTMLButtonElement;

	// Selection-context row: names the focused shape, its role/direction dropdowns and Delete.
	private readonly contextLabel: HTMLSpanElement;
	private readonly roleLabel: HTMLSpanElement;
	private readonly roleSelect: HTMLSelectElement;
	private readonly dirLabel: HTMLSpanElement;
	private readonly dirSelect: HTMLSelectElement;
	private readonly deleteBtn: HTMLButtonElement;

	private readonly adoptFiltersRow: AdoptFiltersRow;
	private readonly matchSelect: HTMLSelectElement;
	// Only meaningful under a gates extent, so it rides beside the extent select and hides otherwise.
	private readonly debounceLabel: HTMLSpanElement;
	private readonly debounceSelect: HTMLSelectElement;
	private readonly clearBtn: HTMLButtonElement;
	private readonly summaryEl: HTMLDivElement;
	private readonly nameInput: HTMLInputElement;
	private readonly saveBtn: HTMLButtonElement;
	private readonly runChip: HTMLButtonElement;
	private readonly rideChip: HTMLButtonElement;
	private readonly heatmapToggle: HTMLButtonElement;

	// The activity count is rendered inline on the activate ("Done") button rather than
	// as a separate element, so these track the latest value to recompose its label.
	private active = false;
	private count = 0;
	private countExact = true;
	// Armed before the map was ready: the activation is pending, not refused (see setWaiting).
	private waiting = false;

	// Save-button gating tracked as two independent inputs so a late preview result (setSaveEnabled)
	// can never re-enable the button mid-save: setSaving wins (SP-01).
	private saving = false;
	private saveEnabled = false;

	// Which role set the shared role select currently holds — avoids rebuilding its options
	// (and losing the pending value) on every re-render when the shape kind hasn't changed.
	private roleKind: 'zone' | 'gate' | null = null;

	constructor(host: HTMLElement, private readonly deps: PanelDeps) {
		host.classList.add('shape-filter-panel');
		host.innerHTML = '';

		this.activateBtn = this.button('Paint filter', 'shape-btn shape-btn--activate');
		this.drawZoneBtn = this.button('▭ Zone', 'shape-btn');
		this.drawGateBtn = this.button('╱ Gate', 'shape-btn');
		this.selectBtn = this.button('↖ Select', 'shape-btn');

		// One context-sensitive row edits whichever shape has focus (or the next-drawn draft):
		// a label ("Z1 (2/3)" / "New gate:" / hint), a role select re-skinned per kind, a gate
		// direction select, and Delete. The role select routes to the zone or gate callback by
		// the kind it currently holds (roleKind).
		this.contextLabel = this.span('shape-context-label');
		this.roleLabel = this.span('shape-context-tag', 'Role:');
		this.roleSelect = buildSelect('shape-select', (v) => {
			if (this.roleKind === 'gate') this.deps.onGateRoleChange(v as GateRole);
			else this.deps.onZoneRoleChange(v as ZoneRole);
		});
		this.dirLabel = this.span('shape-context-tag', 'Direction:');
		this.dirSelect = buildSelect('shape-select', (v) => this.deps.onGateDirectionChange(v as CrossDirection));
		setSelectOptions(this.dirSelect, DIRECTION_OPTIONS);
		this.deleteBtn = this.button('Delete', 'shape-btn');

		this.adoptFiltersRow = new AdoptFiltersRow({ onChange: () => this.deps.onAdoptedFiltersChange() });

		this.matchSelect = buildSelect('shape-select shape-select--extent', (v) => this.pickMatchMode(v as MatchMode));
		setSelectOptions(this.matchSelect, EXTENT_OPTIONS);
		this.debounceLabel = this.span('shape-context-tag', 'Min gap:');
		this.debounceSelect = buildSelect('shape-select shape-select--gate-gap', (v) => this.deps.onGateDebounceChange(Number(v)));
		setSelectOptions(this.debounceSelect, GATE_DEBOUNCE_OPTIONS);
		this.clearBtn = this.button('Clear all', 'shape-btn');

		this.summaryEl = document.createElement('div');
		this.summaryEl.className = 'shape-summary';

		this.nameInput = document.createElement('input');
		this.nameInput.type = 'text';
		this.nameInput.className = 'shape-name-input';
		this.nameInput.placeholder = 'Segment name';
		this.nameInput.maxLength = 100;
		this.saveBtn = this.button('Save as segment', 'shape-btn shape-btn--save');
		this.runChip = this.button('🏃 Run', 'shape-btn shape-btn--type');
		this.rideChip = this.button('🚴 Ride', 'shape-btn shape-btn--type');
		this.heatmapToggle = this.button('🔥 + Heatmap', 'shape-btn shape-btn--heatmap');

		// Layout is a vertical stack of rows so each logical group keeps its own line on mobile.
		// The head row carries the activate ("Done") button plus the tool buttons; the rest live
		// inside `controls`, hidden wholesale when painting is off.
		const headRow = this.row(this.activateBtn, this.drawZoneBtn, this.drawGateBtn, this.selectBtn);
		headRow.classList.add('shape-row--head');
		host.appendChild(headRow);

		this.controls = document.createElement('div');
		this.controls.className = 'shape-controls';
		this.controls.hidden = true;
		const selectionRow = this.row(
			this.contextLabel, this.roleLabel, this.roleSelect, this.dirLabel, this.dirSelect, this.deleteBtn);
		selectionRow.classList.add('shape-row--selection');
		const extentRow = this.row(
			this.span('shape-context-tag', 'Effort:'), this.matchSelect,
			this.debounceLabel, this.debounceSelect, this.clearBtn);
		extentRow.classList.add('shape-row--fill');
		const saveRow = this.row(this.nameInput, this.saveBtn);
		// Heatmap is an optional attachment on the saved segment; its row carries the opt-in
		// toggle plus the type chips (which activities to paint), gated on the toggle.
		const heatmapRow = this.row(this.heatmapToggle, this.runChip, this.rideChip);
		for (const r of [saveRow, heatmapRow]) r.classList.add('shape-row--fill');
		this.controls.append(
			this.sectionTitle('1. Which tracks?'),
			selectionRow,
			this.adoptFiltersRow.root,
			this.sectionTitle('2. What counts as the effort?'),
			extentRow,
			this.summaryEl,
			saveRow,
			heatmapRow,
		);
		host.appendChild(this.controls);
		// Save-time refusals (empty name, an extent mode the drawn shapes can't satisfy) land here
		// instead of in a modal: the panel stays open, the shapes stay drawn, the message clears as
		// soon as the user edits the thing it is about.
		this.error = createInlineError(this.controls, { fields: [this.nameInput, this.matchSelect] });

		this.wire();
		this.setActive(false); // start collapsed: only "Paint filter" shows, tool buttons hidden
		this.setTool(null);
		this.setDraftRole(null, 'any', 'start', 'any'); // hint row until a tool arms / a shape is picked
		this.setGateDebounce(DEFAULT_GATE_DEBOUNCE_SECONDS); // before setMatchMode, which hides the row
		this.setMatchMode('whole');
		this.setShapeCounts(0, 0); // clip/gates disabled until shapes exist
		this.runChip.classList.add('active'); // both types included by default
		this.rideChip.classList.add('active');
		this.updateHeatmapChips(); // toggle off → type chips disabled
	}

	// ── state setters (pushed by the orchestrator) ───────────────────────────

	/** The effective adopted grid filters (null = none) — read for preview, summary and save. */
	getAdoptedGridFilters(): GridFiltersMap | null {
		return this.adoptFiltersRow.getEffective();
	}

	/** Should the drawn shape restrict the table? (live merge only — see AdoptFiltersRow.restrictsTable.) */
	mergeRestrictsTable(): boolean {
		return this.adoptFiltersRow.restrictsTable();
	}

	/** Hydrate the adopt row from a loaded segment's stored filters (edit mode). Fires no change events. */
	setStoredGridFilters(filters: GridFiltersMap | null): void {
		this.adoptFiltersRow.setStored(filters);
	}

	setActive(active: boolean): void {
		this.active = active;
		this.controls.hidden = !active;

		// Tool buttons share the activate button's row, so hide them when not painting.
		this.drawZoneBtn.hidden = !active;
		this.drawGateBtn.hidden = !active;
		this.selectBtn.hidden = !active;
		this.activateBtn.classList.toggle('active', active);
		this.renderActivateLabel();
	}

	setTool(tool: Tool | null): void {
		this.drawZoneBtn.classList.toggle('active', tool === 'draw-zone');
		this.drawGateBtn.classList.toggle('active', tool === 'draw-gate');
		this.selectBtn.classList.toggle('active', tool === 'select');
	}

	/** Editing an existing shape: name it, show its kind's role (+ direction for gates) and Delete.
	 * `badge` is the "Z1"/"G2" label; the no-selection case is driven by {@link setDraftRole}. */
	setSelected(shape: Shape, badge: string, cycle?: CycleInfo | null): void {
		this.contextLabel.textContent = badge + this.cycleSuffix(cycle ?? null);
		this.deleteBtn.hidden = false;
		this.showRoleControls(true);
		if (shape.kind === 'gate') {
			this.setRoleKind('gate');
			setSelectValue(this.roleSelect, shape.role);
			this.showDirection(true);
			setSelectValue(this.dirSelect, shape.direction);
		} else {
			this.setRoleKind('zone');
			setSelectValue(this.roleSelect, shape.role);
			this.showDirection(false);
		}
	}

	/** Nothing selected. `kind` = the armed draw tool's shape (role select drives the next-drawn
	 * shape's default); `null` = no draw tool armed → show the "tap a shape" hint. */
	setDraftRole(kind: 'zone' | 'gate' | null, zoneRole: ZoneRole, gateRole: GateRole, gateDirection: CrossDirection): void {
		this.deleteBtn.hidden = true;
		if (kind === null) {
			this.contextLabel.textContent = 'Tap a shape to edit it';
			this.showRoleControls(false);
			this.showDirection(false);
			return;
		}
		this.showRoleControls(true);
		if (kind === 'gate') {
			this.contextLabel.textContent = 'New gate:';
			this.setRoleKind('gate');
			setSelectValue(this.roleSelect, gateRole);
			this.showDirection(true);
			setSelectValue(this.dirSelect, gateDirection);
		} else {
			this.contextLabel.textContent = 'New zone:';
			this.setRoleKind('zone');
			setSelectValue(this.roleSelect, zoneRole);
			this.showDirection(false);
		}
	}

	/** Grey out extent options that can't apply yet, with a tooltip why (driven by the store). */
	setShapeCounts(zones: number, gates: number): void {
		setOptionDisabled(this.matchSelect, 'clip', zones === 0, 'Add a non-avoid zone first');
		setOptionDisabled(this.matchSelect, 'gates', gates === 0, 'Add a start and finish gate first');
	}

	setMatchMode(mode: MatchMode): void {
		setSelectValue(this.matchSelect, mode);
		// The debounce only affects gate crossings — hide it under the other extents rather than
		// offering a control that changes nothing. Routed through here (not pickMatchMode) so the
		// orchestrator's edit-mode restore toggles it too.
		this.showGateDebounce(mode === 'gates');
	}

	/** Push the same-gate debounce; edit mode restores whatever the stored document carried. A
	 * hand-authored value outside the presets gets its own option rather than rendering blank —
	 * the control must never misreport what the segment will actually match with. */
	setGateDebounce(seconds: number): void {
		const value = String(seconds);
		const custom = GATE_DEBOUNCE_OPTIONS.every((o) => o.value !== value);
		setSelectOptions(
			this.debounceSelect,
			custom ? [...GATE_DEBOUNCE_OPTIONS, { value, label: `${seconds} s` }] : GATE_DEBOUNCE_OPTIONS);
		setSelectValue(this.debounceSelect, value);
	}

	/** Live legend sentence describing the current rules ("Efforts run G1 → G2, …"). */
	setSummary(text: string): void {
		this.summaryEl.textContent = text;
	}

	setCount(n: number, exact = true): void {
		this.count = n;
		this.countExact = exact;
		this.renderActivateLabel();
	}

	/**
	 * Paint mode was armed before the overview map finished loading: the orchestrator waits for the
	 * map instead of refusing, and the button says so meanwhile. Clicking it again cancels the wait.
	 */
	/** A save-time refusal, shown in the panel (see the ctor's createInlineError). */
	setError(message: string): void {
		this.error.show(message);
	}

	clearError(): void {
		this.error.clear();
	}

	setWaiting(waiting: boolean): void {
		this.waiting = waiting;
		this.activateBtn.classList.toggle('is-waiting', waiting);
		this.renderActivateLabel();
	}

	/**
	 * Composes the activate button label. When painting, the matched activity count rides on
	 * the button itself ("Done · 234 act."), with a leading "≈" while the count is still the
	 * fast bbox estimate. When off, it is simply "Paint filter".
	 */
	private renderActivateLabel(): void {
		if (this.waiting) {
			this.activateBtn.textContent = 'Waiting for map…';
			return;
		}
		if (!this.active) {
			this.activateBtn.textContent = 'Paint filter';
			return;
		}
		const approx = this.countExact ? '' : '≈';
		this.activateBtn.textContent = `Done · ${approx}${this.count} act.`;
	}

	setSaveEnabled(enabled: boolean): void {
		this.saveEnabled = enabled;
		this.saveBtn.disabled = this.saving || !enabled;
	}

	/** Show a busy state on the save button while the segment is being analyzed. */
	setSaving(busy: boolean): void {
		this.saving = busy;
		this.saveBtn.disabled = busy || !this.saveEnabled;
		this.heatmapToggle.disabled = busy;
		if (busy) {
			this.saveBtn.textContent = 'Analyzing…';
			this.runChip.disabled = true;
			this.rideChip.disabled = true;
		} else {
			this.updateSaveLabel();
			this.updateHeatmapChips();
		}
	}

	/** Heatmap opt-in + selected activity types, read at save time. */
	getHeatmapOptions(): HeatmapOptions {
		const enabled = this.heatmapToggle.classList.contains('active');
		const types: string[] = [];
		if (this.runChip.classList.contains('active')) types.push('Run');
		if (this.rideChip.classList.contains('active')) types.push('Ride');
		return { enabled, types };
	}

	/** Enable the type chips only while the heatmap toggle is on. */
	private updateHeatmapChips(): void {
		const on = this.heatmapToggle.classList.contains('active');
		this.runChip.disabled = !on;
		this.rideChip.disabled = !on;
	}

	/** Trimmed segment name typed into the panel's name field. */
	getName(): string {
		return this.nameInput.value.trim();
	}

	/** Prefill the name field (e.g. when loading an existing segment for editing). */
	setName(value: string): void {
		this.nameInput.value = value;
		this.updateSaveLabel();
	}

	/**
	 * Switch between create and edit modes. Pass the loaded segment's name to edit it (the save
	 * button then reads "Save changes" while the name is unchanged and "Save as copy" once renamed);
	 * pass null to return to plain create mode ("Save as segment").
	 */
	setEditContext(originalName: string | null): void {
		this.originalName = originalName;
		this.updateSaveLabel();
	}

	/** Draw attention to the name field (e.g. after an empty-name save attempt). */
	focusName(): void {
		this.nameInput.focus();
	}

	clearName(): void {
		this.nameInput.value = '';
	}

	// ── internals ─────────────────────────────────────────────────────────────

	private originalName: string | null = null;

	/** "(2/3)" when the selection sits in an overlap stack; "" for a lone shape. */
	private cycleSuffix(cycle: CycleInfo | null): string {
		return cycle && cycle.total > 1 ? ` (${cycle.index + 1}/${cycle.total})` : '';
	}

	/** Swap the shared role select between the zone and gate option sets (only when it changed). */
	private setRoleKind(kind: 'zone' | 'gate'): void {
		if (this.roleKind === kind) return;
		setSelectOptions(this.roleSelect, kind === 'gate' ? GATE_ROLE_OPTIONS : ZONE_ROLE_OPTIONS);
		this.roleKind = kind;
	}

	private showRoleControls(show: boolean): void {
		this.roleLabel.hidden = !show;
		this.roleSelect.hidden = !show;
	}

	private showGateDebounce(show: boolean): void {
		this.debounceLabel.hidden = !show;
		this.debounceSelect.hidden = !show;
	}

	private showDirection(show: boolean): void {
		this.dirLabel.hidden = !show;
		this.dirSelect.hidden = !show;
	}

	/** Pick the save-button label for the current mode + whether the name was changed. */
	private updateSaveLabel(): void {
		if (this.originalName === null) this.saveBtn.textContent = 'Save as segment';
		else this.saveBtn.textContent =
			this.getName() === this.originalName.trim() ? 'Save changes' : 'Save as copy';
	}

	private wire(): void {
		this.activateBtn.addEventListener('click', () => {
			// Read the shared `active` flag (kept by setActive) so a programmatic activation —
			// e.g. opening a segment for editing — stays in sync with the next user click.
			const next = !this.active;
			this.setActive(next);
			this.deps.onToggleActive(next);
		});
		// The orchestrator decides the resulting state (arm, or toggle back to navigation)
		// and pushes the button highlight back via setTool().
		this.drawZoneBtn.addEventListener('click', () => this.deps.onToolChange('draw-zone'));
		this.drawGateBtn.addEventListener('click', () => this.deps.onToolChange('draw-gate'));
		this.selectBtn.addEventListener('click', () => this.deps.onToolChange('select'));
		this.deleteBtn.addEventListener('click', () => this.deps.onDeleteSelected());
		this.clearBtn.addEventListener('click', () => this.deps.onClear());
		this.saveBtn.addEventListener('click', () => this.deps.onSave());
		this.runChip.addEventListener('click', () => this.runChip.classList.toggle('active'));
		this.rideChip.addEventListener('click', () => this.rideChip.classList.toggle('active'));
		this.heatmapToggle.addEventListener('click', () => {
			this.heatmapToggle.classList.toggle('active');
			this.updateHeatmapChips();
		});
		// In edit mode the save label depends on whether the name still matches the original.
		this.nameInput.addEventListener('input', () => this.updateSaveLabel());
		this.nameInput.addEventListener('keydown', (e) => {
			if (e.key === 'Enter' && !this.saveBtn.disabled) {
				e.preventDefault();
				this.deps.onSave();
			}
		});
	}

	// The extent select drives its own highlight (setMatchMode) and emits the intent; role/direction
	// selects emit intent only — the orchestrator applies it (to the selected shape or the draft
	// default) and pushes the value back via setSelected / setDraftRole (single source of truth).
	private pickMatchMode(mode: MatchMode): void {
		this.setMatchMode(mode);
		this.deps.onMatchModeChange(mode);
	}

	private button(label: string, className: string): HTMLButtonElement {
		const b = document.createElement('button');
		b.type = 'button';
		b.className = className;
		b.textContent = label;
		return b;
	}

	private span(className: string, text = ''): HTMLSpanElement {
		const s = document.createElement('span');
		s.className = className;
		s.textContent = text;
		return s;
	}

	/** A single horizontal row of controls; rows stack vertically in the panel. */
	private row(...children: HTMLElement[]): HTMLDivElement {
		const r = document.createElement('div');
		r.className = 'shape-row';
		r.append(...children);
		return r;
	}

	private sectionTitle(text: string): HTMLDivElement {
		const t = document.createElement('div');
		t.className = 'shape-section-title';
		t.textContent = text;
		return t;
	}
}
