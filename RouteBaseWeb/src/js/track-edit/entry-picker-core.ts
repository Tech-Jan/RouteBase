/**
 * entry-picker-core.ts — pure logic for the Activities-page entry picker (A5). No DOM, no fetch: it
 * turns an overview highlight set + a session snapshot into ordered id lists, button/bar state, and the
 * two navigation URLs. Both flows only ever PRODUCE `loadActivity` ops through the landed A2 endpoints
 * (invariant 10) — this module knows nothing about editor core, save, undo, or revert.
 *
 * Read-only imports only (edit-ops.ts is frozen). Ordering is D-A5-1: chronological by activity start
 * (earliest = slot 0), unknown starts last, ties broken by id-ascending — deterministic everywhere.
 */

import type { EditOp, EditScript } from './edit-ops';
import { TRACK_EDIT_LIMITS } from './track-edit-errors';

/** Query-string key for Flow B; the single grep key — never built from fragments (matches
 * edit-mode-controller.ts's inlined literal, C1). */
export const PICK_FOR_PARAM = 'pickFor';

/** Query-string key for the nav front door: `/Activities?editorPick=1` opens the Activities page as the
 * track-editor entry, where the sticky bar turns a highlight into a new session (Flow A, no `?pickFor`). */
export const EDITOR_PICK_PARAM = 'editorPick';

// ── date parsing (D-A5-1 ordering key) ─────────────────────────────────────

const OVERVIEW_DATE_RE = /^(\d{2})\.(\d{2})\.(\d{4}) (\d{2}):(\d{2})$/;

/** Parse the overview GPS-cache date (`"dd.MM.yyyy HH:mm"`, ActivitiesApiController) to a comparable
 * epoch (ms, UTC-interpreted — only relative order matters). Null for empty/garbage/undefined. */
export function parseOverviewDate(date: string | null | undefined): number | null {
	if (!date) return null;
	const m = OVERVIEW_DATE_RE.exec(date);
	if (!m) return null;
	const [, dd, mm, yyyy, hh, min] = m;
	const day = Number(dd), month = Number(mm), hour = Number(hh), minute = Number(min);
	if (month < 1 || month > 12 || day < 1 || day > 31 || hour > 23 || minute > 59) return null;
	return Date.UTC(Number(yyyy), month - 1, day, hour, minute);
}

// ── ordering (D-A5-1) ──────────────────────────────────────────────────────

/** Order ids for a session: dated ids ascending by start (earliest = index 0 = slot 0), ties by
 * id-ascending; unknown-start ids appended last, id-ascending. Pure — `startEpochOf` is injected
 * (no GPS-cache import), so it unit-tests mock-free. */
export function orderForSession(ids: number[], startEpochOf: (id: number) => number | null): number[] {
	const dated: Array<{ id: number; t: number }> = [];
	const undated: number[] = [];
	for (const id of ids) {
		const t = startEpochOf(id);
		if (t === null) undated.push(id);
		else dated.push({ id, t });
	}
	dated.sort((a, b) => a.t - b.t || a.id - b.id);
	undated.sort((a, b) => a - b);
	return [...dated.map((d) => d.id), ...undated];
}

// ── session-script projections ─────────────────────────────────────────────

/** Activity ids loaded within the EFFECTIVE script `ops[0..cursor)` (the undone tail is NOT in-session
 * — re-picking a tail id is legal; append truncates it, A1 §2.7). Non-load ops ignored. */
export function effectiveLoadedIds(script: EditScript, cursor: number): number[] {
	const ids: number[] = [];
	const end = Math.min(cursor, script.ops.length);
	for (let i = 0; i < end; i++) {
		const op = script.ops[i];
		if (op?.op === 'loadActivity') ids.push(op.activityId);
	}
	return ids;
}

/** Slot-0 activity id: first `loadActivity` of the effective script, else the full script's first load
 * (E-EP15: cursor = 0 → whole-script fallback), else null when the script loads nothing. */
export function slot0ActivityId(script: EditScript, cursor: number): number | null {
	const firstLoad = (ops: EditOp[]): number | null => {
		for (const op of ops) if (op.op === 'loadActivity') return op.activityId;
		return null;
	};
	const effective = firstLoad(script.ops.slice(0, Math.max(0, cursor)));
	return effective ?? firstLoad(script.ops);
}

// ── selection diff ─────────────────────────────────────────────────────────

/** Pickable ids = highlight minus what's already in the session, INPUT ORDER preserved (D-A5-1 order is
 * applied only at POST-build time via orderForSession). null/[] highlight → [] (D-A5-9: never select-all). */
export function pickableIds(highlight: number[] | null, inSession: number[]): number[] {
	if (!highlight || highlight.length === 0) return [];
	const inSet = new Set(inSession);
	return highlight.filter((id) => !inSet.has(id));
}

// ── Flow A button state (§2) ───────────────────────────────────────────────

export interface FlowAState {
	/** Hidden entirely when the highlight is null/[] (D-A5-9). */
	hidden: boolean;
	enabled: boolean;
	label: string;
	/** `title` attribute — set only for the over-cap disabled state. */
	title: string | null;
}

/** Flow-A "Open N in editor" button state from the effective highlight. Hidden when nothing is
 * selected; enabled for 1..maxSlots; visible-but-disabled (over-cap title) beyond maxSlots. */
export function flowAState(highlight: number[] | null): FlowAState {
	const n = highlight?.length ?? 0;
	if (n === 0) return { hidden: true, enabled: false, label: '', title: null };
	const label = `Open ${n} in editor`;
	if (n > TRACK_EDIT_LIMITS.maxSlots) {
		return { hidden: false, enabled: false, label, title: `max ${TRACK_EDIT_LIMITS.maxSlots} tracks` };
	}
	return { hidden: false, enabled: true, label, title: null };
}

// ── Flow A front-door bar state (nav "Track Editor" → ?editorPick=1) ────────

export interface EditorPickBarState {
	/** N = highlighted (== selected) track count: marks if any, else the painted/filtered highlight. */
	selectedCount: number;
	enabled: boolean;
	openLabel: string;
	message: string;
	/** Inline hint when disabled (0 selected / over cap); null when enabled. */
	reason: string | null;
	/** Show the "Clear" affordance while explicit checkbox marks exist (Clear empties them). */
	canClear: boolean;
}

/** Front-door bar state: always visible (unlike flowAState's hidden-at-0 map button), so the entry is
 * discoverable before any track is picked. Unified rule: the selection is whatever is highlighted on the map
 * (`selected` = the effective highlight = marks ?? paint ?? filter) — so painting alone opens, and checking
 * rows pins an explicit override. `hasMarks` drives the Clear affordance (Clear empties the checkbox marks).
 * Enabled for 1..maxSlots; 0 → the hint; over cap → reason. */
export function editorPickBarState(selected: number[] | null, hasMarks: boolean): EditorPickBarState {
	const n = selected?.length ?? 0;
	let reason: string | null = null;
	if (n === 0) reason = 'check rows or paint the map to select tracks';
	else if (n > TRACK_EDIT_LIMITS.maxSlots) reason = `max ${TRACK_EDIT_LIMITS.maxSlots} tracks`;
	return {
		selectedCount: n,
		enabled: reason === null,
		openLabel: n > 0 ? `Open ${n} in editor` : 'Open in editor',
		message: n === 1 ? '1 track selected' : `${n} tracks selected`,
		reason,
		canClear: hasMarks,
	};
}

// ── Flow B confirm-bar state (§3.2) ────────────────────────────────────────

export interface PickerBarState {
	/** N = pickable count (highlight − inSession). */
	selectedCount: number;
	/** How many highlighted ids are already loaded in the session (drives the `· M already in session`). */
	alreadyInSession: number;
	enabled: boolean;
	addLabel: string;
	message: string;
	/** Inline reason when disabled; null when enabled. */
	reason: string | null;
}

export interface PickerBarInput {
	highlight: number[] | null;
	inSession: number[];
	/** Server-replayed slot count (slotCounts.length) — includes split-off slots id-subtraction misses. */
	slotCount: number;
	/** Effective cursor = append baseCursor; maxOps headroom is `cursor + N`. */
	cursor: number;
}

/** Confirm-bar state: counts, Add enablement (1 ≤ N ≤ free slots AND cursor+N ≤ maxOps), and the
 * inline disabled reason. `free = maxSlots − slotCount` counts split-off slots correctly (T-EP13). */
export function pickerBarState(input: PickerBarInput): PickerBarState {
	const { highlight, inSession, slotCount, cursor } = input;
	const pickable = pickableIds(highlight, inSession);
	const n = pickable.length;
	const inSet = new Set(inSession);
	const alreadyInSession = (highlight ?? []).filter((id) => inSet.has(id)).length;
	const free = TRACK_EDIT_LIMITS.maxSlots - slotCount;

	let message = `${n} selected`;
	if (alreadyInSession > 0) message += ` · ${alreadyInSession} already in session`;

	let reason: string | null = null;
	if (!highlight || highlight.length === 0) reason = 'filter or paint on the map to select';
	else if (n === 0) reason = 'all selected are already in the session';
	else if (n > free) reason = `only ${free} slots free`;
	else if (cursor + n > TRACK_EDIT_LIMITS.maxOps) reason = 'session edit limit reached';

	return { selectedCount: n, alreadyInSession, enabled: reason === null, addLabel: `Add ${n}`, message, reason };
}

// ── navigation URLs ────────────────────────────────────────────────────────

/** Editor URL after create/append: `/Activities/Details/{slot0}?editSession={sessionId}` (A3 §1.1). */
export function buildEditorUrl(slot0ActivityIdValue: number, sessionId: string): string {
	return `/Activities/Details/${slot0ActivityIdValue}?editSession=${encodeURIComponent(sessionId)}`;
}

/** Flow-B picker URL the editor's add-track dialog links to (C1; matches edit-mode-controller.ts:147). */
export function buildActivitiesPickerUrl(sessionId: string): string {
	return `/Activities?${PICK_FOR_PARAM}=${encodeURIComponent(sessionId)}`;
}

/** Read `?pickFor=` from a location search string; null when absent or empty (Flow-A-only mode). */
export function readPickForParam(search: string): string | null {
	const value = new URLSearchParams(search).get(PICK_FOR_PARAM);
	return value ? value : null;
}

/** True when opened via the nav front door (`?editorPick=1`) — mounts the Flow-A picker bar. */
export function readEditorPickParam(search: string): boolean {
	return new URLSearchParams(search).get(EDITOR_PICK_PARAM) === '1';
}
