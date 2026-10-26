/**
 * slot-tools.ts — the multi-track chip row + [+ Add track] / [⇥ Append…] buttons that mount into A3's empty
 * `#trackEditSlotTools` container (A4 §2.3). One chip per live slot (color dot = `colorForSlot`, active chip =
 * red + ring), click = switch editing to it. The pure `slotToolsModel` builds the view-model (tested V8–V10);
 * `createSlotTools` renders it and re-renders on every controller `refresh(slots, activeSlotId)`. The
 * `#trackEditSlotBadge` text stays owned by the toolbar — slot-tools only owns its container.
 */

import { colorForSlot, ACTIVE_SLOT_COLOR } from './slot-overlay';
import type { ReplaySlot } from './apply-script';

export interface SlotChipModel {
	slotId: number;
	color: string;   // palette hue, or ACTIVE_SLOT_COLOR for the active chip
	active: boolean;
	label: string;   // root-name tooltip (+ " (split)")
}

export interface SlotToolsModel {
	chips: SlotChipModel[];
	addEnabled: boolean;
	addHint: string | null;   // reason when add is disabled (slot cap)
	appendEnabled: boolean;   // needs ≥ 2 live slots
}

/** Pure view-model for the slot tools. `labelFor` resolves a slot's root-name tooltip (genealogy). */
export function slotToolsModel(
	slots: ReplaySlot[], activeSlotId: number, maxSlots: number,
	labelFor: (slotId: number) => string = (id) => `#${id}`,
): SlotToolsModel {
	const chips = [...slots]
		.sort((a, b) => a.slotId - b.slotId)
		.map((s) => ({
			slotId: s.slotId,
			color: s.slotId === activeSlotId ? ACTIVE_SLOT_COLOR : colorForSlot(s.slotId),
			active: s.slotId === activeSlotId,
			label: labelFor(s.slotId),
		}));
	const live = slots.length;
	const addEnabled = live < maxSlots;
	return {
		chips,
		addEnabled,
		addHint: addEnabled ? null : 'slot limit reached',
		appendEnabled: live >= 2,
	};
}

export interface SlotToolsDeps {
	maxSlots: number;
	labelFor(slotId: number): string;
	onSwitch(slotId: number): void;
	onAddTrack?(): void;
	onAppend?(): void;
}

export interface SlotToolsHandle {
	refresh(slots: ReplaySlot[], activeSlotId: number): void;
	destroy(): void;
}

export function createSlotTools(deps: SlotToolsDeps): SlotToolsHandle {
	const container0 = document.getElementById('trackEditSlotTools');
	if (!container0) return { refresh: () => {}, destroy: () => {} };
	const container = container0; // narrowed rebind for the closures below (G3)

	function render(model: SlotToolsModel): void {
		const frag = document.createDocumentFragment();
		for (const chip of model.chips) frag.appendChild(chipButton(chip));
		frag.appendChild(addButton(model));
		frag.appendChild(appendButton(model));
		container.replaceChildren(frag);
	}

	function chipButton(chip: SlotChipModel): HTMLButtonElement {
		const btn = document.createElement('button');
		btn.type = 'button';
		btn.className = chip.active ? 'track-edit-slot-chip is-active' : 'track-edit-slot-chip';
		btn.title = chip.label;
		const dot = document.createElement('span');
		dot.className = 'track-edit-slot-dot';
		dot.style.backgroundColor = chip.color;
		const text = document.createElement('span');
		text.textContent = `#${chip.slotId}`;
		btn.append(dot, text);
		btn.addEventListener('click', () => deps.onSwitch(chip.slotId));
		return btn;
	}

	function addButton(model: SlotToolsModel): HTMLButtonElement {
		const btn = document.createElement('button');
		btn.type = 'button';
		btn.className = 'btn-cockpit btn-cockpit-sm track-edit-add-track';
		btn.textContent = '+ Add track';
		btn.disabled = !model.addEnabled;
		if (model.addHint) btn.title = model.addHint;
		btn.addEventListener('click', () => deps.onAddTrack?.());
		return btn;
	}

	function appendButton(model: SlotToolsModel): HTMLButtonElement {
		const btn = document.createElement('button');
		btn.type = 'button';
		btn.className = 'btn-cockpit btn-cockpit-sm track-edit-append-track';
		btn.textContent = '⇥ Append…';
		btn.disabled = !model.appendEnabled;
		btn.addEventListener('click', () => deps.onAppend?.());
		return btn;
	}

	return {
		refresh(slots, activeSlotId): void {
			render(slotToolsModel(slots, activeSlotId, deps.maxSlots, deps.labelFor));
		},
		destroy(): void {
			container.replaceChildren();
		},
	};
}
