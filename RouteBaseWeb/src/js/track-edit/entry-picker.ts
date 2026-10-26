/**
 * entry-picker.ts — the Activities-page entry picker (A5, own vite entry, self-boots like shape-filter).
 *
 * Two flows, both produce ONLY `loadActivity` ops through the landed A2 endpoints (invariant 10 — zero
 * editor-core/save/undo/revert code touched):
 *   Flow A (no `?pickFor`): the map-toolbar "Open N in editor" button → POST create session → redirect.
 *   Flow B (`?pickFor={sessionId}`): a sticky confirm bar → append the highlighted tracks → redirect back.
 *
 * The current overview highlight IS the selection (no per-track ticking): read via the ONE overview seam
 * (getOverviewSelectionBridge — one-directional, overview never imports this module). API calls go through
 * edit-api (read-only, D-A5-7); all pure logic lives in entry-picker-core. `navigate`/`api`/`search` are
 * injected so the flows unit-test without a real page. Ownership is NOT client-filtered — the server is the
 * authority (invariant 6); a 404/409 names the offender when the error body carries an activityId.
 */

import { getOverviewSelectionBridge, type OverviewSelectionBridge } from '../overview/activities-overview';
import { getOverviewPickBridge, type OverviewPickBridge } from '../overview/overview-pick-bridge';
import { createSession, getSession, appendOps, EditApiError } from './edit-api';
import { TRACK_EDIT_API_ERRORS, type SessionStateResponse } from './edit-session-api';
import type { EditOp } from './edit-ops';
import {
	parseOverviewDate, orderForSession, effectiveLoadedIds, slot0ActivityId, pickableIds,
	pickerBarState, flowAState, editorPickBarState, buildEditorUrl, readPickForParam, readEditorPickParam,
	PICK_FOR_PARAM, type PickerBarState,
} from './entry-picker-core';
import { el, actionButton, showToast as showDomToast } from './track-edit-dom';

const TOAST_MS = 6000;

const showToast = (message: string): void =>
	showDomToast(message, { className: 'entry-picker-toast', role: 'status', ms: TOAST_MS });

export interface EntryPickerApi {
	createSession(activityIds: number[]): Promise<SessionStateResponse>;
	getSession(sessionId: string): Promise<SessionStateResponse>;
	appendOps(sessionId: string, baseCursor: number, ops: EditOp[]): Promise<SessionStateResponse>;
}

export interface EntryPickerDeps {
	bridge: OverviewSelectionBridge;
	/** Editor pick set (checkbox selection). Front door reads/observes/clears the explicit picks here. */
	picks: OverviewPickBridge;
	api: EntryPickerApi;
	navigate(url: string): void;
	/** location.search captured at boot — Flow B reads `pickFor`; strip-on-gone reuses it. */
	search: string;
}

export function initEntryPicker(deps: EntryPickerDeps): void {
	const pickFor = readPickForParam(deps.search);
	if (pickFor !== null) { void initFlowB(deps, pickFor); return; }
	if (readEditorPickParam(deps.search)) { initFlowAPicker(deps); return; } // nav front door
	initFlowA(deps);
}

/** Create a session from the highlighted tracks and redirect into the editor. Shared by the map-toolbar
 * button (Flow A) and the front-door bar — the ONLY create+redirect path (invariant 10). Throws the
 * EditApiError up so each caller renders its own busy/toast handling. */
async function createAndRedirect(deps: EntryPickerDeps, ids: number[]): Promise<void> {
	const ordered = orderForSession(ids, startEpochOf(deps.bridge)); // D-A5-1; slot 0 = earliest start
	const session = await deps.api.createSession(ordered);
	const slot0 = slot0ActivityId(session.script, session.cursor); // server-authoritative first load
	if (slot0 === null) throw new Error('session has no load op');
	deps.navigate(buildEditorUrl(slot0, session.sessionId));
}

// ── Flow A: map-toolbar button → create session → redirect ─────────────────

function initFlowA(deps: EntryPickerDeps): void {
	const btn0 = document.getElementById('trackEditOpenBtn') as HTMLButtonElement | null;
	if (!btn0) return; // no map toolbar (TotalCount 0)
	const btn = btn0; // narrowed rebind for the closures below (G3)
	const label = btn.querySelector('.act-map-mode-label') as HTMLElement | null;
	let busy = false;   // suppress the grid's 150 ms re-pushes from re-enabling a button mid-POST
	let lastKey = '';

	function render(): void {
		if (busy) return;
		const state = flowAState(deps.bridge.getHighlightIds());
		const key = `${state.hidden}|${state.enabled}|${state.label}|${state.title ?? ''}`;
		if (key === lastKey) return; // shallow guard: grid pushes a fresh array even when unchanged (§2)
		lastKey = key;
		btn.hidden = state.hidden;
		btn.disabled = !state.enabled;
		btn.title = state.title ?? 'Open the selected tracks in the track editor';
		if (label && !state.hidden) label.textContent = state.label;
	}

	async function open(): Promise<void> {
		const ids = deps.bridge.getHighlightIds();
		if (!ids || ids.length === 0) return; // button hidden in this state anyway
		busy = true;
		btn.disabled = true;
		try {
			await createAndRedirect(deps, ids);
		} catch (err) {
			busy = false;
			lastKey = ''; // force a re-render out of the busy freeze
			showToast(createErrorMessage(deps.bridge, err));
			render();
		}
	}

	deps.bridge.onHighlightChanged(() => render()); // fires immediately with the current value
	btn.addEventListener('click', () => void open());
}

// ── Flow A front door (nav "Track Editor" → ?editorPick=1) ─────────────────
//
// A first-time visitor never sees the map-toolbar button (it only appears once a highlight exists), so the
// front door lands here with an always-visible sticky bar. Unified selection rule: what "Open" opens is
// whatever is HIGHLIGHTED on the map — the effective highlight (marks ?? paint ?? filter). So painting alone
// selects, and checking rows pins an explicit override (the grid's editor-pick column feeds the top-priority
// 'pick' source). Clear empties the checkbox marks (then the selection falls back to the paint/filter). In
// this mode the map-toolbar button stays hidden (initFlowA is never wired).
function initFlowAPicker(deps: EntryPickerDeps): void {
	const bar = mountConfirmBar(); // reuses the Flow-B bar chrome (.entry-picker-bar); Add btn relabelled to Open
	const clearBtn = actionButton('Clear', 'btn-cockpit-ghost');
	clearBtn.classList.add('entry-picker-clear');
	bar.cancelBtn.insertAdjacentElement('beforebegin', clearBtn);
	let busy = false;

	function render(): void {
		if (busy) return;
		const state = editorPickBarState(deps.bridge.getHighlightIds(), deps.picks.getPickedIds().length > 0);
		bar.msg.textContent = `Track editor — ${state.message}`;
		bar.addBtn.textContent = state.openLabel;
		bar.addBtn.disabled = !state.enabled;
		bar.reason.textContent = state.reason ?? '';
		clearBtn.hidden = !state.canClear;
	}

	async function open(): Promise<void> {
		const ids = deps.bridge.getHighlightIds();
		if (!ids || !editorPickBarState(ids, false).enabled) return; // hasMarks irrelevant to enablement
		busy = true;
		bar.addBtn.disabled = true;
		try {
			await createAndRedirect(deps, ids);
		} catch (err) {
			busy = false;
			showToast(createErrorMessage(deps.bridge, err));
			render();
		}
	}

	// Highlight changes cover paint AND marks (marking writes the 'pick' highlight source → fires this);
	// onPickedChanged additionally keeps canClear in exact lockstep with the mark set.
	deps.bridge.onHighlightChanged(() => render()); // fires immediately with the current value
	deps.picks.onPickedChanged(() => render());
	bar.addBtn.addEventListener('click', () => void open());
	clearBtn.addEventListener('click', () => deps.picks.clearPicks());
	bar.cancelBtn.addEventListener('click', () => deps.navigate('/Activities')); // leave the front door
}

// ── Flow B: pickFor confirm bar → append ops → redirect back ───────────────

async function initFlowB(deps: EntryPickerDeps, sessionId: string): Promise<void> {
	let session: SessionStateResponse;
	try {
		session = await deps.api.getSession(sessionId);
	} catch (err) {
		if (err instanceof EditApiError && err.status === 404) sessionGone(deps, 'Edit session no longer exists.');
		else showToast('Could not open the picker.');
		return;
	}
	if (session.status !== 'active') { sessionGone(deps, 'Edit session was closed.'); return; }

	// Mutable projection — a cursorConflict re-GET refreshes these in place.
	let cursor = session.cursor;
	let inSession = effectiveLoadedIds(session.script, cursor);
	let slotCount = session.slotCounts.length;
	const returnUrl0 = editorReturnUrl(session, sessionId);
	if (returnUrl0 === null) { sessionGone(deps, 'Edit session no longer exists.'); return; } // no load op (E-EP7)
	let returnUrl = returnUrl0; // narrowed rebind for the closures below (G3)

	const bar = mountConfirmBar();
	let busy = false;

	function render(): void {
		if (busy) return;
		applyBarState(bar, pickerBarState({ highlight: deps.bridge.getHighlightIds(), inSession, slotCount, cursor }));
	}

	async function add(): Promise<void> {
		const highlight = deps.bridge.getHighlightIds();
		if (!pickerBarState({ highlight, inSession, slotCount, cursor }).enabled) return;
		const ordered = orderForSession(pickableIds(highlight, inSession), startEpochOf(deps.bridge));
		const ops: EditOp[] = ordered.map((activityId) => ({ op: 'loadActivity', activityId }));
		busy = true;
		bar.addBtn.disabled = true;
		try {
			const resp = await deps.api.appendOps(sessionId, cursor, ops); // baseCursor 0 append truncates the tail
			const slot0 = slot0ActivityId(resp.script, resp.cursor); // response-authoritative (GET slot0 may be stale)
			deps.navigate(slot0 !== null ? buildEditorUrl(slot0, sessionId) : returnUrl);
		} catch (err) {
			busy = false;
			await handleAddError(err);
		}
	}

	async function handleAddError(err: unknown): Promise<void> {
		if (err instanceof EditApiError) {
			if (err.status === 404) { bar.el.remove(); sessionGone(deps, 'Edit session no longer exists.'); return; }
			if (err.code === TRACK_EDIT_API_ERRORS.sessionNotActive) { bar.el.remove(); sessionGone(deps, 'Edit session was closed.'); return; }
			if (err.code === TRACK_EDIT_API_ERRORS.stillRecording) { showToast(createErrorMessage(deps.bridge, err)); render(); return; }
			if (err.code === TRACK_EDIT_API_ERRORS.cursorConflict) { await resync('Session changed — selection refreshed, check and press Add again.'); return; }
			showToast(`Could not add the tracks (${err.code}).`); // 422 {opIndex,code} / other — resync
			await resync(null);
			return;
		}
		showToast('Could not add the tracks.');
		render();
	}

	async function resync(message: string | null): Promise<void> {
		try {
			const fresh = await deps.api.getSession(sessionId);
			if (fresh.status !== 'active') { bar.el.remove(); sessionGone(deps, 'Edit session was closed.'); return; }
			cursor = fresh.cursor;
			inSession = effectiveLoadedIds(fresh.script, cursor);
			slotCount = fresh.slotCounts.length;
			returnUrl = editorReturnUrl(fresh, sessionId) ?? returnUrl;
			render();
			if (message) bar.reason.textContent = message;
		} catch {
			bar.el.remove();
			sessionGone(deps, 'Edit session no longer exists.');
		}
	}

	deps.bridge.onHighlightChanged(() => render()); // fires immediately
	bar.cancelBtn.addEventListener('click', () => deps.navigate(returnUrl)); // no POST (D-A5-8)
	bar.addBtn.addEventListener('click', () => void add());
}

/** Banner + strip `pickFor` → plain Activities mode (E-EP7/E-EP8). */
function sessionGone(deps: EntryPickerDeps, message: string): void {
	showBanner(message);
	const params = new URLSearchParams(deps.search);
	params.delete(PICK_FOR_PARAM);
	const qs = params.toString();
	window.history.replaceState(window.history.state, '', window.location.pathname + (qs ? `?${qs}` : ''));
}

function editorReturnUrl(session: SessionStateResponse, sessionId: string): string | null {
	const slot0 = slot0ActivityId(session.script, session.cursor);
	return slot0 === null ? null : buildEditorUrl(slot0, sessionId);
}

// ── shared pure-ish helpers ────────────────────────────────────────────────

const startEpochOf = (bridge: OverviewSelectionBridge) => (id: number): number | null =>
	parseOverviewDate(bridge.getTrackMeta(id)?.date);

/** Offender-named toast copy for a create/append failure; degrades to a generic subject/message when the
 * error body carries no activityId (C3) or isn't a typed EditApiError. */
function createErrorMessage(bridge: OverviewSelectionBridge, err: unknown): string {
	if (!(err instanceof EditApiError)) return 'Could not open the editor.';
	const who = offenderName(bridge, err.activityId);
	if (err.status === 404) return `${who} can't be edited — it isn't yours or no longer exists. Set the Owner filter to "Me".`;
	switch (err.code) {
		case TRACK_EDIT_API_ERRORS.stillRecording: return `${who} is still recording — finish the recording first.`;
		case TRACK_EDIT_API_ERRORS.noTrackData: return `${who} has no GPS track to edit.`;
		case TRACK_EDIT_API_ERRORS.activityNotEditable: return `${who} is pending review or trashed — confirm it first.`;
		case TRACK_EDIT_API_ERRORS.alreadyCheckedOut:
			return `${who} already has an edit open — open its page to resume or discard it.`;
		default: return `Could not open the editor (${err.code}).`;
	}
}

function offenderName(bridge: OverviewSelectionBridge, activityId?: number): string {
	if (activityId === undefined) return 'That track';
	return bridge.getTrackMeta(activityId)?.name ?? `#${activityId}`;
}

// ── DOM ────────────────────────────────────────────────────────────────────

interface ConfirmBar {
	el: HTMLElement; msg: HTMLElement; reason: HTMLElement;
	addBtn: HTMLButtonElement; cancelBtn: HTMLButtonElement;
}

function mountConfirmBar(): ConfirmBar {
	const bar = el('div', 'entry-picker-bar');
	bar.setAttribute('role', 'region');
	bar.setAttribute('aria-live', 'polite');
	const msg = el('div', 'entry-picker-bar-msg');
	const reason = el('div', 'entry-picker-bar-reason');
	const actions = el('div', 'entry-picker-bar-actions');
	const addBtn = actionButton('Add', 'btn-cockpit-primary');
	addBtn.classList.add('entry-picker-add');
	const cancelBtn = actionButton('Cancel', 'btn-cockpit-ghost');
	cancelBtn.classList.add('entry-picker-cancel');
	actions.append(addBtn, cancelBtn);
	bar.append(msg, reason, actions);
	document.body.appendChild(bar);
	return { el: bar, msg, reason, addBtn, cancelBtn };
}

function applyBarState(bar: ConfirmBar, state: PickerBarState): void {
	bar.msg.textContent = `Picking tracks for the edit session — ${state.message}`;
	bar.addBtn.textContent = state.addLabel;
	bar.addBtn.disabled = !state.enabled;
	bar.reason.textContent = state.reason ?? '';
}

/** One persistent banner (reused if already mounted) for the session-gone states. */
function showBanner(message: string): void {
	let banner = document.querySelector('.entry-picker-banner') as HTMLElement | null;
	if (!banner) {
		banner = el('div', 'entry-picker-banner');
		banner.setAttribute('role', 'alert');
		document.body.appendChild(banner);
	}
	banner.textContent = message;
}

// ── self-boot (shape-filter precedent) ─────────────────────────────────────

function bootstrap(): void {
	const hasPickFor = readPickForParam(window.location.search) !== null;
	const hasEditorPick = readEditorPickParam(window.location.search);
	const hasButton = document.getElementById('trackEditOpenBtn') !== null;
	if (!hasPickFor && !hasEditorPick && !hasButton) return; // not the Activities page / no picker request
	initEntryPicker({
		bridge: getOverviewSelectionBridge(),
		picks: getOverviewPickBridge(),
		api: {
			createSession,
			getSession,
			appendOps: (id, baseCursor, ops) => appendOps(id, baseCursor, ops, false),
		},
		navigate: (url) => window.location.assign(url),
		search: window.location.search,
	});
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bootstrap);
else bootstrap();
