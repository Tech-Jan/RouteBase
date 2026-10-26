/**
 * edit-mode-controller.ts — enter/exit orchestration for Details edit mode. Owns the live EditorState, the
 * write-through queue, the toolbar, and the render trigger; wires the click hook, the URL `?editSession`
 * param, and the bfcache / style-switch re-assert hooks (E14/E15). All geometry goes through the pure state
 * module (twin-validated) and the reuse render pipeline — no forks. `getEditorContext()` is the A4/A5 seam:
 * new slot sources only ever produce ops through the same `applyOp` + queue (extension recipe (b)).
 */

import {
	fromSession, countSeedOps, appendOp, undo, redo, replay, activeSlot, setActiveSlot, opCount,
	type EditorState, type StateWithOutcome,
} from './edit-session-state';
import { asyncCallback } from '../core/async-listener';
import { EditWriteQueue, type WriteJob, type WriteOptions } from './edit-write-queue';
import {
	createSession, getSession, getSources, appendOps, moveCursor, savePreflight, saveSession, discardSession,
	dequantizeSources, fetchTrackSuggestions, searchTracks, EditApiError,
} from './edit-api';
import { openAddTrackDialog, buildQueryPoint } from './add-track-dialog';
import { openAppendDialog } from './append-dialog';
import type { SlotCountClaim, SaveMode, SaveResponse } from './edit-session-api';
import { TRACK_EDIT_API_ERRORS } from './edit-session-api';
import { openSaveDialog, openDiscardDialog, openStaleDialog } from './edit-dialogs';
import { saveTarget } from './edit-save-target';
import { buildActivitiesPickerUrl } from './entry-picker-core';
import { showToast } from './track-edit-dom';
import { renderPreview } from './edit-preview-render';
import { createToolbar, type ToolbarDeps, type ToolbarHandle } from './edit-toolbar';
import { slotGenealogy } from './slot-genealogy';
import { createSlotTools, type SlotToolsHandle } from './slot-tools';
import { initSlotOverlay, clearSlotOverlay } from './slot-overlay';
import { TRACK_EDIT_LIMITS, trackEditErrors } from './track-edit-errors';
import type { ReplayOutcome } from './apply-script';
import type { EditOp } from './edit-ops';
import { closeAllActionPanels, setOnChartClick } from './activity-access';
import { getMaplibreMap } from '../core/map-state';
import {
	showDesyncBanner, removeDesyncBanner, injectKpiChip, setEditSessionParam, navigatePlain,
} from './edit-mode-chrome';

export interface EditModeConfig {
	activityId: number;
	/** Fallback preview tz (mirror of the config island); per-source tz from the sources endpoint wins. */
	tzOffsetHours: number;
}

/** A4/A5 extension handle: append/add-track go through the SAME applyOp + queue. */
export interface EditorContext {
	getState(): EditorState;
	applyOp(op: EditOp): { ok: boolean; code?: string };
	rerender(): void;
	/** Switch the edited slot (view-state only — never an op; A4 §2.4). */
	setActiveSlot(slotId: number): void;
	/** True while a tool is armed / a popover is open (A4 D-A4-10). */
	isToolArmed(): boolean;
	/** Load an activity as a new slot — the ONE write-then-apply path (A4 D-A4-4). */
	addTrack(activityId: number): Promise<void>;
}

let editing = false;
let config: EditModeConfig | null = null;
let state: EditorState | null = null;
let outcome: ReplayOutcome = { ok: true, slots: [] };
let tzByActivity = new Map<number, number>();
let nameByActivity = new Map<number, string>();
let queue: EditWriteQueue | null = null;
let toolbar: ToolbarHandle | null = null;
let slotTools: SlotToolsHandle | null = null;

/** Enter edit mode: create a session (or resume the given one), fetch + dequantize sources, build state,
 * mount the toolbar, swap the preview in, and arm the click hook. */
export async function enterEditMode(cfg: EditModeConfig, resumeSessionId?: string): Promise<void> {
	if (editing) return;
	// Hold the guard SYNCHRONOUSLY, before the first await: enter does async work (create/resume the session,
	// fetch + dequantize sources) and a second entry landing during that window would otherwise pass the
	// check above and build a SECOND session + toolbar. Released in the catch so a failed enter can retry.
	editing = true;
	closeAllActionPanels(); // frees the onChartClick hook from the segment picker (E22)
	try {
		// The entry only ever hands over an id (the banner's hint carries no script) — the ONE full-session
		// load happens here, on Resume, and nowhere else (F-1).
		const session = resumeSessionId
			? await getSession(resumeSessionId)
			: await createSession([cfg.activityId]);
		const { slots, tzByActivity: tz, nameByActivity: names } = dequantizeSources(await getSources(session.sessionId));

		config = cfg;
		tzByActivity = tz;
		nameByActivity = names;
		const initial = fromSession(session, slots, countSeedOps(session.script.ops));
		state = initial.state;
		outcome = initial.outcome; // fromSession already replayed — don't replay again (R-08)
		queue = new EditWriteQueue(sendJob, handleDesync);

		document.body.classList.add('track-edit-active'); // body class drives toolbar/KPI show/hide (track-edit.css)
		injectKpiChip();
		setEditSessionParam(session.sessionId);
		toolbar = createToolbar(deps);
		slotTools = createSlotTools(slotToolsDeps);
		initSlotOverlay({ setActiveSlot: doSetActiveSlot, isToolArmed: () => toolbar?.isToolArmed() ?? false });
		render();
		setOnChartClick((_distance, ordinal) => toolbar?.onOrdinalClick(ordinal));
		installReassertHooks();
		// `editing` was set true synchronously at the top (the re-entrancy guard) — nothing to set here.
	} catch (err) {
		editing = false; // enter failed — release the guard so the user can retry
		handleEnterError(err);
	}
}

/**
 * Tear the editor down in-page (TE-04). Every exit navigates today, so the listeners/layers would die with the
 * document anyway — but this is the only reason the toolbar/overlay teardown code exists, it satisfies the
 * add/removeEventListener pairing rule, and it makes a future in-page exit safe. Idempotent.
 */
export function exitEditMode(): void {
	if (!editing) return;
	editing = false; // before the removes: onBeforeUnload/onPageShow read it and must now no-op
	toolbar?.destroy();
	clearSlotOverlay();
	removeReassertHooks();
	removeDesyncBanner();
	document.body.classList.remove('track-edit-active');
	state = null;
	queue = null;
	toolbar = null;
	slotTools = null;
}

export function getEditorContext(): EditorContext | null {
	if (!editing || !state) return null;
	return {
		getState: () => state as EditorState,
		applyOp,
		rerender: () => { if (state) { outcome = replay(state); render(); toolbar?.refresh(); } },
		setActiveSlot: doSetActiveSlot,
		isToolArmed: () => toolbar?.isToolArmed() ?? false,
		addTrack,
	};
}

// ---- add-track (D-A4-4: the ONE write-then-apply path) ----

/** Load an activity as a new slot. The twin can't validate a source it doesn't have, so this flow WRITES the
 * `loadActivity` op first (server guards + stamps + engine-validates), fetches the new source, then ADOPTS the
 * server's authoritative script/cursor. On failure nothing was optimistically applied → no desync freeze; the
 * caller (dialog) maps the error. Recipe-(b)-pure: the only mutation is a loadActivity through the ops endpoint. */
async function addTrack(activityId: number): Promise<void> {
	if (!state) throw new EditApiError(410, TRACK_EDIT_API_ERRORS.sessionNotActive);
	if (queue?.isFrozen()) throw new EditApiError(409, TRACK_EDIT_API_ERRORS.cursorConflict);
	await queue?.flush(); // drain in-flight optimistic writes so the direct append sees the true cursor
	if (!state) throw new EditApiError(410, TRACK_EDIT_API_ERRORS.sessionNotActive);

	const session = await appendOps(state.sessionId, state.cursor, [{ op: 'loadActivity', activityId }], false);
	const { slots, tzByActivity: tz, nameByActivity: names } = dequantizeSources(await getSources(session.sessionId, [activityId]));
	for (const [id, track] of slots) state.sources.set(id, track);
	for (const [id, v] of tz) tzByActivity.set(id, v);
	for (const [id, v] of names) nameByActivity.set(id, v);

	const rebuilt = fromSession(session, state.sources, countSeedOps(session.script.ops));
	state = rebuilt.state;
	outcome = rebuilt.outcome; // fromSession already replayed (R-08)
	const newId = outcome.ok && outcome.slots.length > 0
		? Math.max(...outcome.slots.map((s) => s.slotId)) : state.activeSlotId;
	const switched = setActiveSlot(state, newId, outcome); // D-A4-5: the just-added track becomes active
	if (switched) { state = switched.state; outcome = switched.outcome; }
	render();
	toolbar?.afterApply(); // R-07: clear any selection carried from the previous active slot
}

function openAddTrack(): void {
	if (!state) return;
	const slot = activeSlot(outcome, state);
	if (!slot) return;
	const qp = buildQueryPoint(slot.track, tzForActive());
	const sessionId = state.sessionId;
	openAddTrackDialog({
		fetchSuggestions: () => fetchTrackSuggestions(sessionId, qp.endLat, qp.endLng, qp.localDate),
		search: (q, date) => searchTracks(sessionId, q, date),
		addTrack: (trackId) => addTrack(trackId).catch((err) => { mapAddTrackError(err); throw err; }),
		pickerUrl: buildActivitiesPickerUrl(sessionId),
	});
}

function mapAddTrackError(err: unknown): void {
	if (!(err instanceof EditApiError)) { showToast('Could not add the track'); return; }
	if (err.code === trackEditErrors.slotCapExceeded) showToast(`slot limit reached (${TRACK_EDIT_LIMITS.maxSlots})`);
	else if (err.code === TRACK_EDIT_API_ERRORS.noTrackData) showToast('no GPS data on that activity');
	else if (err.code === trackEditErrors.duplicateLoad) { /* dialog flips the row to "in editor" */ }
	else showToast(`Could not add the track (${err.code})`);
}

function openAppend(): void {
	if (!state || !outcome.ok || outcome.slots.length < 2) return;
	openAppendDialog({
		slots: outcome.slots,
		labelFor: slotLabel,
		defaultTargetId: state.activeSlotId,
		applyAppend: (target, source, timeMode, gapSeconds) => {
			applyOp(gapSeconds === undefined
				? { op: 'append', target, source, timeMode }
				: { op: 'append', target, source, timeMode, gapSeconds });
		},
	});
}

// ---- state mutation ----

function applyOp(op: EditOp): { ok: boolean; code?: string } {
	if (!state || queue?.isFrozen()) return { ok: false, code: 'frozen' };
	const baseCursor = state.cursor;
	// `outcome` is always the replay of `state`'s effective prefix (every assignment sets both together), so the
	// op resumes from it instead of re-replaying the whole script (PR-03).
	const result = appendOp(state, op, outcome);
	if (!result.ok) return { ok: false, code: result.error.code };
	state = result.state;
	outcome = result.outcome;
	queue?.enqueue({ kind: 'ops', baseCursor, cursor: state.cursor, ops: [op] });
	render();
	toolbar?.afterApply();
	return { ok: true };
}

function doUndo(): void {
	moveCursorLocal(undo);
}

function doRedo(): void {
	moveCursorLocal(redo);
}

function moveCursorLocal(step: (s: EditorState) => StateWithOutcome | null): void {
	if (!state || queue?.isFrozen()) return;
	const baseCursor = state.cursor;
	const r = step(state);
	if (!r) return; // clamped — no-op
	state = r.state;
	outcome = r.outcome; // undo/redo already replayed (R-08) — no second replay
	queue?.enqueue({ kind: 'cursor', baseCursor, cursor: state.cursor });
	render();
	toolbar?.afterApply();
}

function render(): void {
	if (!state) return;
	const slot = activeSlot(outcome, state);
	if (slot) renderPreview({ slot, outcome, activeSlotId: state.activeSlotId, tzOffsetHours: tzForActive() });
	slotTools?.refresh(outcome.ok ? outcome.slots : [], state.activeSlotId);
}

/** Switch editing to another slot: pure view-state (A4 §2.4) — no op, no write-through, no queue interplay.
 * Reuses the current outcome (geometry unchanged) so the switch does zero replays (R-08) and the
 * profile/charts/red-track follow the new slot. Clears any live selection (R-07: its ordinals were the old
 * slot's, so Crop/Cut on the new slot would apply them to the wrong track). */
function doSetActiveSlot(slotId: number): void {
	if (!state) return;
	const switched = setActiveSlot(state, slotId, outcome);
	if (!switched) return; // dead id or already active
	state = switched.state;
	outcome = switched.outcome;
	render();
	toolbar?.afterApply();
}

/** Active slot's time strings use its ROOT source's tz (D-A4-7 — split-offs walk genealogy to the root);
 * fallback = the Razor island's tz. */
function tzForActive(): number {
	if (!state) return config?.tzOffsetHours ?? 0;
	const slot = activeSlot(outcome, state);
	if (!slot) return config?.tzOffsetHours ?? 0;
	const rootId = slotGenealogy(state.ops.slice(0, state.cursor)).rootActivityId.get(slot.slotId);
	if (rootId !== undefined && tzByActivity.has(rootId)) return tzByActivity.get(rootId) as number;
	return config?.tzOffsetHours ?? 0;
}

/** Root-name tooltip for a chip (genealogy root → source name; "… (split)" for split tails). */
function slotLabel(slotId: number): string {
	if (!state) return `Slot ${slotId}`;
	const gen = slotGenealogy(state.ops.slice(0, state.cursor));
	const rootId = gen.rootActivityId.get(slotId);
	const base = rootId !== undefined ? (nameByActivity.get(rootId) ?? `Activity ${rootId}`) : `Slot ${slotId}`;
	return gen.splitOff.has(slotId) ? `${base} (split)` : base;
}

// ---- write-through ----

async function sendJob(job: WriteJob, options: WriteOptions): Promise<void> {
	if (!state) return;
	if (job.kind === 'ops') await appendOps(state.sessionId, job.baseCursor, job.ops ?? [], options.keepalive);
	else await moveCursor(state.sessionId, job.cursor, options.keepalive);
}

/** Desync freeze (D-U2): disable the toolbar + surface a banner. A gone/closed session (E18) offers only a
 * reload; any other failure (E10 network, E12 multi-tab 409 cursorConflict) offers Retry → resync. */
function handleDesync(reason: unknown): void {
	toolbar?.setDisabled(true);
	const gone = reason instanceof EditApiError
		&& (reason.status === 404 || reason.code === TRACK_EDIT_API_ERRORS.sessionNotActive);
	showDesyncBanner(gone, () => void resyncFromServer());
}

async function resyncFromServer(): Promise<void> {
	if (!state) return;
	try {
		const session = await getSession(state.sessionId);
		const { slots, tzByActivity: tz, nameByActivity: names } = dequantizeSources(await getSources(session.sessionId));
		tzByActivity = tz;
		nameByActivity = names;
		const resynced = fromSession(session, slots, countSeedOps(session.script.ops));
		state = resynced.state;
		outcome = resynced.outcome; // fromSession already replayed (R-08)
		queue?.resync();
		removeDesyncBanner();
		toolbar?.setDisabled(false);
		render();
		toolbar?.refresh();
	} catch {
		// still unreachable — leave the banner up for another retry
	}
}

// ---- save / discard / close (§5) ----

// One save dialog at a time. The flag is raised BEFORE the flush await, not when the dialog opens: a second
// Save click lands during that await, and two dialogs would then be live at once.
let saveDialogOpen = false;

async function saveFlow(): Promise<void> {
	if (!state || queue?.isFrozen() || saveDialogOpen) return;
	saveDialogOpen = true;
	await queue?.flush(); // E9: dialog opens only after in-flight writes drain, so preflight covers every op
	if (!state) {
		saveDialogOpen = false;
		return;
	}
	const sessionId = state.sessionId;
	openSaveDialog({
		fetchPreflight: (mode) => savePreflight(sessionId, mode),
		onConfirm: (mode) => {
			saveDialogOpen = false;
			void doSave(mode);
		},
		onCancel: () => {
			saveDialogOpen = false;
		},
	});
}

async function doSave(mode: SaveMode): Promise<void> {
	if (!state) return;
	try {
		navigateAfterSave(mode, await saveSession(state.sessionId, mode, liveCounts()));
	} catch (err) {
		if (err instanceof EditApiError && err.code === TRACK_EDIT_API_ERRORS.staleTrack) {
			openStaleDialog({ onDiscard: () => void discardAndLeave(), onKeep: () => {} }); // §6 stale path
		} else {
			showToast(err instanceof EditApiError ? `Save failed (${err.code})` : 'Save failed');
		}
	}
}

function discardFlow(): void {
	if (!state) return;
	openDiscardDialog({ editCount: opCount(state), onConfirm: () => void discardAndLeave() });
}

async function discardAndLeave(): Promise<void> {
	if (!state) return;
	try {
		await discardSession(state.sessionId);
	} catch {
		// idempotent — a concurrent discard already closed it
	}
	exitEditMode(); // the session is dead server-side; queued writes would only 409 → drop the unload prompt too
	navigatePlain(config?.activityId);
}

function closeFlow(): void {
	exitEditMode();
	navigatePlain(config?.activityId); // session persists server-side; the resume banner reappears next visit
}

/** D-U10: land on an activity that still exists — see saveTarget (replace-after-merge trashes the base). */
function navigateAfterSave(mode: SaveMode, response: SaveResponse): void {
	// A failed phase-2 creation is a half-landed save, not a silent success: its sources are in the trash and
	// reverting this save restores them. Blocking alert (a toast would be gone before the navigation).
	if (response.failedSlots?.length) {
		const names = response.failedSlots.map((f) => `“${f.name}”`).join(', ');
		window.alert(
			`${names} could not be created. The rides that went into it are in the trash — ` +
			`undo this save (the ride's Version history panel) to restore them.`,
		);
	}
	const target = saveTarget(mode, response, config?.activityId);
	exitEditMode(); // the save landed — the session is terminal; tear down before we navigate
	if (target.kind === 'reload') navigatePlain(config?.activityId);
	else if (target.kind === 'activity') window.location.href = `/Activities/Details/${target.trackId}`;
	else window.location.href = '/Activities';
}

function liveCounts(): SlotCountClaim[] {
	return outcome.ok ? outcome.slots.map((s) => ({ slotId: s.slotId, pointCount: s.track.count })) : [];
}

// ---- re-assert hooks (E14 bfcache, E15 style switch) ----

function installReassertHooks(): void {
	window.addEventListener('pageshow', onPageShow);
	window.addEventListener('beforeunload', onBeforeUnload);
	document.getElementById('detMapStyleSwitcher')?.addEventListener('click', onStyleSwitchClick);
}

/** The matching removes — the handlers are module-level named functions, so they detach by identity. */
function removeReassertHooks(): void {
	window.removeEventListener('pageshow', onPageShow);
	window.removeEventListener('beforeunload', onBeforeUnload);
	document.getElementById('detMapStyleSwitcher')?.removeEventListener('click', onStyleSwitchClick);
}

function onBeforeUnload(e: BeforeUnloadEvent): void {
	// E11: only prompt while a write is still in flight/queued — keepalive covers the acked ones.
	if (editing && queue?.isBusy()) {
		e.preventDefault();
		e.returnValue = '';
	}
}

function onPageShow(e: PageTransitionEvent): void {
	if (e.persisted && editing) render(); // map re-inited with the original config data — re-assert the preview
}

function onStyleSwitchClick(): void {
	const map = getMaplibreMap();
	if (map && editing) map.once('idle', () => { if (editing) render(); });
}

// ---- chrome (toolbar deps, banners, toasts, kpi chip) ----

const deps: ToolbarDeps = {
	getState: () => state as EditorState,
	currentOutcome: () => outcome,
	activeSlot: () => (state ? activeSlot(outcome, state) : null),
	applyOp,
	undo: doUndo,
	redo: doRedo,
	requestSave: asyncCallback('save flow', saveFlow),
	requestDiscard: discardFlow,
	requestClose: closeFlow,
};

const slotToolsDeps = {
	maxSlots: TRACK_EDIT_LIMITS.maxSlots,
	labelFor: slotLabel,
	onSwitch: doSetActiveSlot,
	onAddTrack: openAddTrack,
	onAppend: openAppend,
};

function handleEnterError(err: unknown): void {
	document.body.classList.remove('track-edit-active');
	if (err instanceof EditApiError) {
		if (err.code === TRACK_EDIT_API_ERRORS.stillRecording) {
			// D-U5: still recording → disable the entry button for the page's life + toast (server guard is authoritative).
			showToast('This track is still recording — finish the recording first.');
			const btn = document.getElementById('trackEditEnterBtn') as HTMLButtonElement | null;
			if (btn) btn.disabled = true;
			return;
		}
		if (err.code === TRACK_EDIT_API_ERRORS.staleTrack) {
			showToast('This track changed since the last session — reload to start a fresh edit.'); // E19
			return;
		}
		showToast(`Could not open the editor (${err.code})`);
		return;
	}
	showToast('Could not open the editor');
}

