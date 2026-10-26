import { beforeEach, describe, expect, it } from 'vitest';
import { createToolbar, type ToolbarDeps } from './edit-toolbar';
import type { EditorState } from './edit-session-state';
import type { ReplayOutcome, ReplaySlot } from './apply-script';

// R-07: a completed range selection must be cleared by afterApply() — the controller calls afterApply (not
// refresh) on an active-slot switch so a stale selection's ordinals can't be applied to the newly-active slot.

const BUTTON_IDS = [
	'trackEditCropBtn', 'trackEditCutBtn', 'trackEditSplitBtn', 'trackEditUndoBtn',
	'trackEditRedoBtn', 'trackEditSaveBtn', 'trackEditDiscardBtn', 'trackEditCloseBtn',
];
const TEXT_IDS = ['trackEditSelReadout', 'trackEditHint', 'trackEditOpCounter', 'trackEditSlotBadge'];

function mountToolbarDom(): void {
	document.body.innerHTML = '';
	for (const id of BUTTON_IDS) {
		const b = document.createElement('button');
		b.id = id;
		document.body.appendChild(b);
	}
	for (const id of TEXT_IDS) {
		const d = document.createElement('div');
		d.id = id;
		document.body.appendChild(d);
	}
}

const slot: ReplaySlot = { slotId: 0, origin: { activityId: 101 }, track: { count: 10, channels: {} } };
const state: EditorState = {
	sessionId: 's1', sources: new Map(), ops: [{ op: 'loadActivity', activityId: 101 }],
	cursor: 1, seedCount: 1, activeSlotId: 0,
};

function deps(over: Partial<ToolbarDeps> = {}): ToolbarDeps {
	return {
		getState: () => state,
		currentOutcome: (): ReplayOutcome => ({ ok: true, slots: [slot] }),
		activeSlot: () => slot,
		applyOp: () => ({ ok: true }),
		undo: () => {},
		redo: () => {},
		requestSave: () => {},
		requestDiscard: () => {},
		requestClose: () => {},
		...over,
	};
}

describe('edit-toolbar afterApply clears the selection (R-07)', () => {
	beforeEach(() => mountToolbarDom());

	it('a completed selection enables Crop, and afterApply resets it', () => {
		const toolbar = createToolbar(deps());
		const cropBtn = document.getElementById('trackEditCropBtn') as HTMLButtonElement;
		const readout = document.getElementById('trackEditSelReadout') as HTMLElement;

		toolbar.onOrdinalClick(2); // anchor
		toolbar.onOrdinalClick(5); // complete [2..5]
		expect(readout.textContent).toBe('Selection: 4 pts');
		expect(cropBtn.disabled).toBe(false); // range of 4 pts on a 10-pt slot → Crop enabled

		toolbar.afterApply(); // the switch-path clear

		expect(readout.textContent).toBe('No selection');
		expect(cropBtn.disabled).toBe(true); // no selection → Crop disabled
	});
});

