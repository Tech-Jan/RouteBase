/**
 * edit-api.ts — the SINGLE client touch point for the TrackEdit HTTP API (A2 §14 reconciliation). Every
 * wrapper targets a LANDED endpoint on Api/TrackEditApiController.cs and returns the frozen DTOs from
 * edit-session-api.ts (the TS mirror; contract changes are atomic with the C# there, invariant 8). Expected
 * failures arrive as a typed TrackEditErrorBody with an HTTP status — surfaced as an `EditApiError` the
 * controller/queue branch on (409 cursorConflict / 410-class gone / staleTrack / stillRecording …).
 */

import type {
	CreateSessionRequest, SessionStateResponse, SessionSourcesResponse, SourceTrackDto,
	AppendOpsRequest, MoveCursorRequest, SavePreflightResponse, SaveRequest, SaveResponse,
	CommitSummaryResponse, RevertResponse, TrackEditErrorBody, SlotCountClaim, SaveMode,
	TrackSuggestionsResponse,
} from './edit-session-api';
import { CHANNEL_SCALES, type ChannelName, type SlotTrack } from './apply-script';
import type { EditOp } from './edit-ops';
// TODO(openapi-review): D29 — each fetch site's response type is now bound to its endpoint via ResponseOf;
// the readable DTO return annotation is kept and cross-checked (a wrong path/verb fails tsc). See ledger.
import type { ResponseOf } from '../generated-helpers/endpoint-types';

/** A typed API failure. `code` is a TrackEditErrors/TrackEditApiErrors string; status is the HTTP code. */
export class EditApiError extends Error {
	constructor(
		public readonly status: number,
		public readonly code: string,
		public readonly opIndex?: number,
		public readonly activityId?: number,
	) {
		super(`edit-api ${status} ${code}`);
		this.name = 'EditApiError';
	}
}

const JSON_HEADERS = { 'Content-Type': 'application/json' } as const;

async function request<T>(url: string, init?: RequestInit): Promise<T> {
	const response = await fetch(url, init);
	if (!response.ok) throw await toError(response);
	if (response.status === 204) return undefined as T;
	const text = await response.text();
	return (text ? JSON.parse(text) : undefined) as T;
}

async function toError(response: Response): Promise<EditApiError> {
	const body = (await response.json().catch(() => null)) as TrackEditErrorBody | null;
	// TODO(openapi-review): D23 — the wire writes opIndex/activityId as explicit nulls (generated shape);
	// EditApiError keeps undefined-optional fields, so bridge null → undefined here.
	return new EditApiError(
		response.status, body?.code ?? 'httpError', body?.opIndex ?? undefined, body?.activityId ?? undefined);
}

// ---- session lifecycle ----

// NOTE: createSession/getSession/appendOps/moveCursor return SessionStateResponse, and fetchTrackSuggestions/
// searchTracks return TrackSuggestionsResponse — both DELIBERATELY hand-written (D15): the generated shapes
// carry the loose `op?` EditOp union and null-vs-undefined nullability that break the narrowing/guard
// consumers. Binding these to ResponseOf would re-introduce exactly the generated shape D15 rejected, so
// they keep the hand DTO annotation (D6 is bounded by D15 — see ledger D29).
export function createSession(activityIds: number[]): Promise<SessionStateResponse> {
	const body: CreateSessionRequest = { activityIds };
	return request('/api/edit-sessions', { method: 'POST', headers: JSON_HEADERS, body: JSON.stringify(body) });
}

export function getSession(sessionId: string): Promise<SessionStateResponse> {
	return request(`/api/edit-sessions/${encodeURIComponent(sessionId)}`);
}

/** Session sources (§14.1); `ids` (D-A4-6) narrows to a subset — add-track fetches only its new source. */
export function getSources(sessionId: string, ids?: number[]): Promise<SessionSourcesResponse> {
	const query = ids && ids.length ? `?ids=${ids.join(',')}` : '';
	return request<ResponseOf<'/api/edit-sessions/{id}/sources', 'get'>>(
		`/api/edit-sessions/${encodeURIComponent(sessionId)}/sources${query}`);
}

/** Add-track suggestions (A4 §3.1): proximity ∪ same-day over the owner's confirmed activities. */
export function fetchTrackSuggestions(
	sessionId: string, endLat?: number, endLng?: number, localDate?: string,
): Promise<TrackSuggestionsResponse> {
	const params = new URLSearchParams();
	if (endLat !== undefined) params.set('endLat', String(endLat));
	if (endLng !== undefined) params.set('endLng', String(endLng));
	if (localDate) params.set('localDate', localDate);
	const query = params.toString();
	return request(`/api/edit-sessions/${encodeURIComponent(sessionId)}/track-suggestions${query ? `?${query}` : ''}`);
}

/** Add-track search (A4 §3.1): name substring and/or date; at least one required (server 400 otherwise). */
export function searchTracks(sessionId: string, q?: string, date?: string): Promise<TrackSuggestionsResponse> {
	const params = new URLSearchParams();
	if (q) params.set('q', q);
	if (date) params.set('date', date);
	return request(`/api/edit-sessions/${encodeURIComponent(sessionId)}/track-search?${params.toString()}`);
}

export function appendOps(
	sessionId: string, baseCursor: number, ops: EditOp[], keepalive: boolean,
): Promise<SessionStateResponse> {
	const body: AppendOpsRequest = { baseCursor, ops };
	return request(`/api/edit-sessions/${encodeURIComponent(sessionId)}/ops`,
		{ method: 'POST', headers: JSON_HEADERS, body: JSON.stringify(body), keepalive });
}

export function moveCursor(sessionId: string, cursor: number, keepalive: boolean): Promise<SessionStateResponse> {
	const body: MoveCursorRequest = { cursor };
	return request(`/api/edit-sessions/${encodeURIComponent(sessionId)}/cursor`,
		{ method: 'POST', headers: JSON_HEADERS, body: JSON.stringify(body), keepalive });
}

export function savePreflight(sessionId: string, mode: SaveMode): Promise<SavePreflightResponse> {
	return request<ResponseOf<'/api/edit-sessions/{id}/save-preflight', 'get'>>(
		`/api/edit-sessions/${encodeURIComponent(sessionId)}/save-preflight?mode=${mode}`);
}

export function saveSession(
	sessionId: string, mode: SaveMode, expectedCounts: SlotCountClaim[],
): Promise<SaveResponse> {
	const body: SaveRequest = { mode, expectedCounts };
	return request<ResponseOf<'/api/edit-sessions/{id}/save', 'post'>>(
		`/api/edit-sessions/${encodeURIComponent(sessionId)}/save`,
		{ method: 'POST', headers: JSON_HEADERS, body: JSON.stringify(body) });
}

export function discardSession(sessionId: string): Promise<void> {
	return request(`/api/edit-sessions/${encodeURIComponent(sessionId)}/discard`, { method: 'POST' });
}

// ---- commit / revert (badge) ----

export function getCommitSummary(commitId: number): Promise<CommitSummaryResponse> {
	return request<ResponseOf<'/api/edit-commits/{commitId}', 'get'>>(`/api/edit-commits/${commitId}`);
}

export function revertCommit(commitId: number): Promise<RevertResponse> {
	return request<ResponseOf<'/api/edit-commits/{commitId}/revert', 'post'>>(
		`/api/edit-commits/${commitId}/revert`, { method: 'POST' });
}

// ---- sources dequantization (wire ints → preview twin Float64Array space) ----

export interface DequantizedSources {
	/** activityId → dequantized SlotTrack, the preview state's `sources` map. */
	slots: Map<number, SlotTrack>;
	/** activityId → tz offset hours (preview time strings). */
	tzByActivity: Map<number, number>;
	/** activityId → display name (chip tooltips / slot labels, A4 §2.3). */
	nameByActivity: Map<number, string>;
}

/** Absolute quantized codec-grid ints (null = missing, absent key = whole-NULL channel) → dequantized values
 * via CHANNEL_SCALES, exactly like the fixture loader. No per-point objects. */
export function dequantizeSources(response: SessionSourcesResponse): DequantizedSources {
	const slots = new Map<number, SlotTrack>();
	const tzByActivity = new Map<number, number>();
	const nameByActivity = new Map<number, string>();
	for (const source of response.sources) {
		slots.set(source.activityId, dequantizeSource(source));
		tzByActivity.set(source.activityId, source.tzOffsetHours);
		nameByActivity.set(source.activityId, source.name);
	}
	return { slots, tzByActivity, nameByActivity };
}

function dequantizeSource(source: SourceTrackDto): SlotTrack {
	const channels: SlotTrack['channels'] = {};
	for (const [name, values] of Object.entries(source.channels)) {
		if (!values) continue;
		const scale = CHANNEL_SCALES[name as ChannelName];
		const out = new Float64Array(source.count);
		for (let i = 0; i < source.count; i++) {
			const q = values[i];
			out[i] = q === null || q === undefined ? NaN : q * scale;
		}
		channels[name as ChannelName] = out;
	}
	return { count: source.count, channels };
}
