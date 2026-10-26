/**
 * edit-toolbar.ts — the server-rendered #trackEditToolbar's behaviour: tool buttons, the enablement matrix
 * (re-run per render), split arming, range-selection readout + highlight, and the Cut → timeMode popover.
 * It owns the pure selection state (edit-selection.ts) and routes chart/map clicks (delivered as CURRENT-state
 * ordinals — invariant 2) to either a split or a selection step. Ops are handed to the controller's `applyOp`,
 * which twin-validates them; a rejection surfaces as an inline hint (the tool stays armed).
 */

import { canUndo, canRedo, opCount, type EditorState } from './edit-session-state';
import {
	emptySelection, click as advanceSelection, selectionRange, selectionPointCount, isWholeTrack,
	type SelectionState,
} from './edit-selection';
import { cutGapLabels, formatGap } from './edit-gap-labels';
import { TRACK_EDIT_LIMITS } from './track-edit-errors';
import type { ReplayOutcome, ReplaySlot } from './apply-script';
import type { EditOp, TimeMode } from './edit-ops';
import { mapDispatcher } from '../core/map-dispatcher';
import { showSplitOverlay, clearSplitOverlay } from '../core/chart-overlay';
import { getSpeedChart, getElevationChart } from './activity-access';

const MIN = TRACK_EDIT_LIMITS.minSlotPoints;

export interface ToolbarDeps {
	getState(): EditorState;
	currentOutcome(): ReplayOutcome;
	activeSlot(): ReplaySlot | null;
	/** Twin-validate + apply an op (synchronous). `ok:false` ⇒ state untouched, `code` set. */
	applyOp(op: EditOp): { ok: boolean; code?: string };
	undo(): void;
	redo(): void;
	requestSave(): void;
	requestDiscard(): void;
	requestClose(): void;
	/** Open the Edit-history dialog (versions of the ride, restorable). Only reachable at zero ops — see refresh(). */
}

export interface ToolbarHandle {
	/** A chart/map click at a current-state ordinal (split step or selection step). */
	onOrdinalClick(ordinal: number): void;
	/** True while the split tool is armed or the cut popover is open (A4 D-A4-10 — slot-line clicks no-op). */
	isToolArmed(): boolean;
	/** Re-run the enablement matrix + readouts (controller calls after every render). */
	refresh(): void;
	/** Reset selection + split arming after a successful op (controller calls post-apply). */
	afterApply(): void;
	/** Freeze/unfreeze every control (desync). */
	setDisabled(disabled: boolean): void;
	destroy(): void;
}

export function createToolbar(deps: ToolbarDeps): ToolbarHandle {
	const el = (id: string) => document.getElementById(id) as HTMLButtonElement | null;
	const cropBtn = el('trackEditCropBtn');
	const cutBtn = el('trackEditCutBtn');
	const splitBtn = el('trackEditSplitBtn');
	const undoBtn = el('trackEditUndoBtn');
	const redoBtn = el('trackEditRedoBtn');
	const saveBtn = el('trackEditSaveBtn');
	const discardBtn = el('trackEditDiscardBtn');
	const closeBtn = el('trackEditCloseBtn');
	const readoutEl = document.getElementById('trackEditSelReadout');
	const hintEl = document.getElementById('trackEditHint');
	const opCounterEl = document.getElementById('trackEditOpCounter');
	const slotBadgeEl = document.getElementById('trackEditSlotBadge');

	let selection: SelectionState = emptySelection();
	let splitArmed = false;
	let disabled = false;
	let popover: HTMLElement | null = null;
	let hintTimer: ReturnType<typeof setTimeout> | undefined;

	// ---- click routing ----

	function onOrdinalClick(ordinal: number): void {
		if (disabled) return;
		if (splitArmed) {
			const result = deps.applyOp({ op: 'splitAt', slot: deps.getState().activeSlotId, index: ordinal });
			if (result.ok) splitArmed = false; // controller's afterApply already reset selection + repainted
			else flashHint("can't split at the very start or end");
			refresh();
			return;
		}
		selection = advanceSelection(selection, ordinal);
		paintSelection();
		refresh();
	}

	function paintSelection(): void {
		const range = selectionRange(selection);
		if (range) {
			mapDispatcher.showSplitHighlight(range.from, range.to);
			showSplitOverlay(getSpeedChart(), range.from, range.to);
			showSplitOverlay(getElevationChart(), range.from, range.to);
		} else {
			mapDispatcher.clearSplitHighlight();
			clearSplitOverlay(getSpeedChart());
			clearSplitOverlay(getElevationChart());
		}
	}

	// ---- tool handlers ----

	function onCrop(): void {
		const range = selectionRange(selection);
		if (!range) return;
		deps.applyOp({ op: 'cropTo', slot: deps.getState().activeSlotId, from: range.from, to: range.to });
	}

	function onCut(): void {
		const range = selectionRange(selection);
		const slot = deps.activeSlot();
		if (!range || !slot) return;
		if (range.to === slot.track.count - 1) {
			// tail cut → no junction → apply keep directly (E7)
			deps.applyOp({ op: 'deleteRange', slot: deps.getState().activeSlotId, from: range.from, to: range.to, timeMode: 'keep' });
			return;
		}
		openCutPopover(range.from, range.to);
	}

	function toggleSplit(): void {
		splitArmed = !splitArmed;
		closePopover();
		refresh();
	}

	// ---- cut popover (default keep; real gap shown; close/synthesize labels from time-gap via edit-gap-labels) ----

	function openCutPopover(from: number, to: number): void {
		const slot = deps.activeSlot();
		if (!slot || !cutBtn) return;
		closePopover();
		const labels = cutGapLabels(slot.track.channels, from, to, slot.track.count);
		const pts = to - from + 1;

		popover = document.createElement('div');
		popover.className = 'track-edit-popover';
		// eslint-disable-next-line no-unsanitized/property -- all interpolations are numeric-derived: pts is a count, formatGap/labels.* are formatter output over integers (no user text)
		popover.innerHTML = `
			<div class="track-edit-popover-title">Remove ${pts} pts</div>
			<div class="track-edit-popover-gap">Time gap left behind: ${formatGap(labels.realGapSeconds)}</div>
			<label class="track-edit-popover-opt"><input type="radio" name="trackEditCutMode" value="keep" checked> Keep real gap <span class="track-edit-popover-note">timestamps untouched</span></label>
			<label class="track-edit-popover-opt"><input type="radio" name="trackEditCutMode" value="close"> Close gap <span class="track-edit-popover-note">junction = ${labels.closeLabel}</span></label>
			<label class="track-edit-popover-opt"><input type="radio" name="trackEditCutMode" value="synthesize"> Synthesize from pace <span class="track-edit-popover-note">junction = ~${labels.synthesizeLabel}</span></label>
			<div class="track-edit-popover-actions">
				<button type="button" class="btn-cockpit btn-cockpit-ghost btn-cockpit-sm" data-role="cancel">Cancel</button>
				<button type="button" class="btn-cockpit btn-cockpit-primary btn-cockpit-sm" data-role="apply">Apply cut</button>
			</div>`;

		const rect = cutBtn.getBoundingClientRect();
		Object.assign(popover.style, {
			position: 'absolute',
			top: `${rect.bottom + window.scrollY + 6}px`,
			left: `${rect.left + window.scrollX}px`,
		});
		document.body.appendChild(popover);

		popover.querySelector('[data-role="cancel"]')?.addEventListener('click', closePopover);
		popover.querySelector('[data-role="apply"]')?.addEventListener('click', () => {
			const mode = (popover?.querySelector('input[name="trackEditCutMode"]:checked') as HTMLInputElement | null)?.value as TimeMode | undefined;
			applyCut(from, to, mode ?? 'keep', labels.closeGapSeconds, labels.synthesizeGapSeconds);
		});
	}

	function applyCut(from: number, to: number, mode: TimeMode, closeGap: number, synthGap: number): void {
		const slot = deps.getState().activeSlotId;
		const op: EditOp = mode === 'keep'
			? { op: 'deleteRange', slot, from, to, timeMode: 'keep' }
			: { op: 'deleteRange', slot, from, to, timeMode: mode, gapSeconds: mode === 'close' ? closeGap : synthGap };
		closePopover();
		deps.applyOp(op);
	}

	function closePopover(): void {
		popover?.remove();
		popover = null;
	}

	// ---- state refresh (enablement matrix + readouts) ----

	function refresh(): void {
		const state = deps.getState();
		const outcome = deps.currentOutcome();
		const slot = deps.activeSlot();
		const count = slot ? slot.track.count : 0;
		const capped = state.cursor >= TRACK_EDIT_LIMITS.maxOps;
		const range = selectionRange(selection);
		const kept = range ? range.to - range.from + 1 : 0;
		const remaining = range ? count - kept : 0;
		const whole = isWholeTrack(selection, count);
		const liveSlots = outcome.ok ? outcome.slots.length : 0;

		setDisabledAttr(cropBtn, disabled || capped || !range || whole || kept < MIN);
		setDisabledAttr(cutBtn, disabled || capped || !range || remaining < MIN);
		setDisabledAttr(splitBtn, disabled || capped);
		setDisabledAttr(undoBtn, disabled || !canUndo(state));
		setDisabledAttr(redoBtn, disabled || !canRedo(state));
		setDisabledAttr(saveBtn, disabled || liveSlots === 0);
		setDisabledAttr(discardBtn, disabled);
		splitBtn?.classList.toggle('is-armed', splitArmed);

		if (opCounterEl) opCounterEl.textContent = `ops ${opCount(state)}/${TRACK_EDIT_LIMITS.maxOps}`;
		if (slotBadgeEl) slotBadgeEl.textContent = liveSlots > 1 ? `${liveSlots} tracks · editing #${state.activeSlotId}` : '';
		updateReadout(capped);
	}

	function updateReadout(capped: boolean): void {
		if (hintTimer) return; // a transient flash owns the hint line right now
		const range = selectionRange(selection);
		if (readoutEl) {
			if (range) {
				const dist = deps.activeSlot()?.track.channels.distance;
				const km = dist ? `${((dist[range.from] ?? NaN) / 1000).toFixed(2)}–${((dist[range.to] ?? NaN) / 1000).toFixed(2)} km · ` : '';
				readoutEl.textContent = `Selection: ${km}${selectionPointCount(selection)} pts`;
			} else {
				readoutEl.textContent = selection.anchor !== null ? 'Selection: start set' : 'No selection';
			}
		}
		if (hintEl) {
			hintEl.textContent = capped
				? 'edit limit reached — save or discard'
				: splitArmed ? 'click chart or map to split'
					: range ? 'Crop keeps the selection · Cut removes it'
						: selection.anchor !== null ? 'click chart or map to set end'
							: 'click chart or map to set start';
		}
	}

	function flashHint(message: string): void {
		if (!hintEl) return;
		hintEl.textContent = message;
		clearTimeout(hintTimer);
		hintTimer = setTimeout(() => { hintTimer = undefined; refresh(); }, 2500);
	}

	function afterApply(): void {
		selection = emptySelection();
		paintSelection();
		refresh();
	}

	function setDisabled(next: boolean): void {
		disabled = next;
		refresh();
	}

	// ---- key handling (E13): popover → split arm → selection; never exits ----

	function onKeydown(e: KeyboardEvent): void {
		if (e.key !== 'Escape') return;
		if (popover) { closePopover(); e.stopPropagation(); return; }
		if (splitArmed) { splitArmed = false; refresh(); e.stopPropagation(); return; }
		if (selection.anchor !== null) { selection = emptySelection(); paintSelection(); refresh(); e.stopPropagation(); }
	}

	// ---- wire ----

	const onUndoClick = (): void => deps.undo();
	const onRedoClick = (): void => deps.redo();
	const onSaveClick = (): void => deps.requestSave();
	const onDiscardClick = (): void => deps.requestDiscard();
	const onCloseClick = (): void => deps.requestClose();
	cropBtn?.addEventListener('click', onCrop);
	cutBtn?.addEventListener('click', onCut);
	splitBtn?.addEventListener('click', toggleSplit);
	undoBtn?.addEventListener('click', onUndoClick);
	redoBtn?.addEventListener('click', onRedoClick);
	saveBtn?.addEventListener('click', onSaveClick);
	discardBtn?.addEventListener('click', onDiscardClick);
	closeBtn?.addEventListener('click', onCloseClick);
	document.addEventListener('keydown', onKeydown, true);

	refresh();

	return {
		onOrdinalClick,
		isToolArmed: () => splitArmed || popover !== null,
		refresh,
		afterApply,
		setDisabled,
		destroy(): void {
			closePopover();
			clearTimeout(hintTimer);
			document.removeEventListener('keydown', onKeydown, true);
			cropBtn?.removeEventListener('click', onCrop);
			cutBtn?.removeEventListener('click', onCut);
			splitBtn?.removeEventListener('click', toggleSplit);
			undoBtn?.removeEventListener('click', onUndoClick);
			redoBtn?.removeEventListener('click', onRedoClick);
			saveBtn?.removeEventListener('click', onSaveClick);
			discardBtn?.removeEventListener('click', onDiscardClick);
			closeBtn?.removeEventListener('click', onCloseClick);
		},
	};
}

function setDisabledAttr(btn: HTMLButtonElement | null, value: boolean): void {
	if (btn) btn.disabled = value;
}
