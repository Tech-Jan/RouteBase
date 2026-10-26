/**
 * append-dialog.ts — the "Append a track" dialog (A4 §4): pick a source slot and a target slot, see the REAL
 * time gap between them BEFORE choosing a timeMode (keep default), then apply through the NORMAL optimistic
 * path (all sources are client-known, so the twin validates — no write-then-apply here). The junction seconds
 * for close/synthesize come STRAIGHT from time-gap.ts (invariant 9, never re-derived). The pickers exclude each
 * other's selection so self-append is unreachable (engine `selfAppend` is the backstop). `buildAppendModel` is
 * pure/exported for tests (V13–V17).
 */

import { firstPresent, lastPresent, type ReplaySlot } from './apply-script';
import { must } from '../core/invariant';
import type { TimeMode } from './edit-ops';
import { appendCloseGap, appendSynthesizeGap } from './time-gap';
import { formatGap, formatDuration } from './edit-gap-labels';
import { el, actionButton, radio, buildDialog, type DialogHandle } from './track-edit-dom';

export interface AppendModel {
	targetId: number;
	sourceId: number;
	/** first-present(source.time) − last-present(target.time); null when either side has no timestamps. */
	realGapSeconds: number | null;
	gapLabel: string;
	closeGapSeconds: number;
	closeLabel: string;
	synthesizeGapSeconds: number;
	synthesizeLabel: string;
	/** close/synthesize offered only when both sides carry present timestamps (else keep only). */
	timeOptionsEnabled: boolean;
	defaultTimeMode: TimeMode;
	/** live slot ids offered per picker, excluding the other picker's current selection (M2). */
	targetOptions: number[];
	sourceOptions: number[];
}

export function buildAppendModel(slots: ReplaySlot[], targetId: number, sourceId: number): AppendModel {
	const liveIds = slots.map((s) => s.slotId).sort((a, b) => a - b);
	const target = slots.find((s) => s.slotId === targetId)?.track.channels ?? {};
	const source = slots.find((s) => s.slotId === sourceId)?.track.channels ?? {};

	const lastTarget = lastPresent(target.time);
	const firstSource = firstPresent(source.time);
	const timeOptionsEnabled = lastTarget !== null && firstSource !== null;
	const realGapSeconds = timeOptionsEnabled ? firstSource - lastTarget : null;

	const closeGapSeconds = target.time ? appendCloseGap(target.time) : 0;
	const synthesizeGapSeconds = appendSynthesizeGap(target, source);

	return {
		targetId,
		sourceId,
		realGapSeconds,
		gapLabel: realGapSeconds === null ? 'no timestamps' : formatGap(realGapSeconds),
		closeGapSeconds,
		closeLabel: formatDuration(closeGapSeconds),
		synthesizeGapSeconds,
		synthesizeLabel: formatDuration(synthesizeGapSeconds),
		timeOptionsEnabled,
		defaultTimeMode: 'keep',
		targetOptions: liveIds.filter((id) => id !== sourceId),
		sourceOptions: liveIds.filter((id) => id !== targetId),
	};
}

export interface AppendDialogDeps {
	slots: ReplaySlot[];
	labelFor(slotId: number): string;
	/** Default target = the active slot (A4 §4). */
	defaultTargetId: number;
	applyAppend(target: number, source: number, timeMode: TimeMode, gapSeconds?: number): void;
}

export function openAppendDialog(deps: AppendDialogDeps): DialogHandle {
	const liveIds = deps.slots.map((s) => s.slotId).sort((a, b) => a - b);
	let targetId = liveIds.includes(deps.defaultTargetId) ? deps.defaultTargetId : must(liveIds[0], 'append dialog opens only with live slots');
	// source = most recently created OTHER live slot (highest id ≠ target).
	let sourceId = liveIds.filter((id) => id !== targetId).at(-1) ?? targetId;
	let timeMode: TimeMode = 'keep';

	const { backdrop, body: dialog, close } = buildDialog('Append a track', 'track-edit-append-dialog');

	const targetSelect = document.createElement('select');
	const sourceSelect = document.createElement('select');
	const pickers = el('div', 'track-edit-append-pickers');
	pickers.append(labelled('Append', sourceSelect), labelled('onto', targetSelect));
	dialog.appendChild(pickers);

	const gapLine = el('div', 'track-edit-append-gap');
	dialog.appendChild(gapLine);

	const modes = el('div', 'track-edit-append-modes');
	const keepOpt = radio('trackEditAppendMode', 'keep', true, '', 'track-edit-append-opt');
	const closeOpt = radio('trackEditAppendMode', 'close', false, '', 'track-edit-append-opt');
	const synthOpt = radio('trackEditAppendMode', 'synthesize', false, '', 'track-edit-append-opt');
	modes.append(keepOpt.label, closeOpt.label, synthOpt.label);
	dialog.appendChild(modes);

	const cancelBtn = actionButton('Cancel', 'btn-cockpit-ghost');
	const appendBtn = actionButton('Append', 'btn-cockpit-primary');
	const actions = el('div', 'track-edit-dialog-actions');
	actions.append(cancelBtn, appendBtn);
	dialog.appendChild(actions);

	function refresh(): void {
		const model = buildAppendModel(deps.slots, targetId, sourceId);
		fillOptions(sourceSelect, model.sourceOptions, sourceId, deps.labelFor);
		fillOptions(targetSelect, model.targetOptions, targetId, deps.labelFor);

		gapLine.textContent = `Time gap between them: ${model.gapLabel}`;
		must(keepOpt.label.lastChild, 'radio() builds a trailing text node').textContent = ' Keep real gap — timestamps untouched';
		must(closeOpt.label.lastChild, 'radio() builds a trailing text node').textContent = ` Close gap — junction becomes ${model.closeLabel}`;
		must(synthOpt.label.lastChild, 'radio() builds a trailing text node').textContent = ` Synthesize from pace — junction becomes ~${model.synthesizeLabel}`;
		closeOpt.input.disabled = !model.timeOptionsEnabled;
		synthOpt.input.disabled = !model.timeOptionsEnabled;
		if (!model.timeOptionsEnabled && timeMode !== 'keep') { timeMode = 'keep'; keepOpt.input.checked = true; }
	}

	targetSelect.addEventListener('change', () => {
		targetId = Number(targetSelect.value);
		if (sourceId === targetId) sourceId = liveIds.filter((id) => id !== targetId).at(-1) ?? sourceId;
		refresh();
	});
	sourceSelect.addEventListener('change', () => {
		sourceId = Number(sourceSelect.value);
		if (targetId === sourceId) targetId = liveIds.filter((id) => id !== sourceId).at(0) ?? targetId;
		refresh();
	});
	for (const opt of [keepOpt, closeOpt, synthOpt]) {
		opt.input.addEventListener('change', () => { if (opt.input.checked) timeMode = opt.input.value as TimeMode; });
	}
	cancelBtn.addEventListener('click', close);
	appendBtn.addEventListener('click', () => {
		const model = buildAppendModel(deps.slots, targetId, sourceId);
		const gapSeconds = timeMode === 'close' ? model.closeGapSeconds
			: timeMode === 'synthesize' ? model.synthesizeGapSeconds : undefined;
		close();
		deps.applyAppend(targetId, sourceId, timeMode, gapSeconds);
	});

	refresh();
	return { el: backdrop, close };
}

// ---- DOM helpers ----

function labelled(text: string, select: HTMLSelectElement): HTMLElement {
	const wrap = el('label', 'track-edit-append-picker');
	wrap.append(document.createTextNode(`${text} `), select);
	return wrap;
}

function fillOptions(
	select: HTMLSelectElement, ids: number[], selected: number, labelFor: (id: number) => string,
): void {
	select.replaceChildren();
	for (const id of ids) {
		const opt = document.createElement('option');
		opt.value = String(id);
		opt.textContent = `#${id} ${labelFor(id)}`;
		opt.selected = id === selected;
		select.appendChild(opt);
	}
}
