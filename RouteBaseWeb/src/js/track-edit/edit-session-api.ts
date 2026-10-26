/**
 * edit-session-api.ts — the TrackEdit HTTP DTO surface (Services/TrackEdit/TrackEditApiModels.cs).
 *
 * Types only (no vite entry). Response DTOs are re-exported from generated/contracts — the source of
 * truth, so drift fails at gen/tsc instead of silently. Request DTOs referencing the frozen EditOp
 * chain stay hand-written (invariant 8); plain request DTOs may come from generated since D26
 * relaxed request-only schemas to deserialize-required (resolves the old D15 concern). The
 * `op` polymorphism + EditScript/TrackVersionStamp/EditSessionState stay the frozen EditCore mirror
 * (edit-ops.ts); SessionStateResponse keeps its script/status refinement by building on it, and
 * SuggestedActivityDto/TrackSuggestionsResponse are generated re-exports since G1 (the wire
 * writes explicit nulls; consumers guard `!= null`).
 */

import type { EditOp, EditSessionState } from './edit-ops';
import type {
  SlotCount, ActivityRef, PlannedActivity, SavedSlot, FailedSlot,
  SavePreflightResponse, SaveResponse, CommitSummaryResponse, RevertResponse,
  SourceTrackDto, SessionSourcesResponse, TrackEditErrorBody,
  CreateSessionRequest, MoveCursorRequest, SlotCountClaim,
  SaveRequest as SaveRequestWire,
} from '../generated/contracts';

// Response DTOs — generated from the C# records; re-exported so this module stays the slice's single DTO
// surface. `mode`/`saveMode` widen to `string` here (the client's SaveMode refinement lives below).
// TrackEditErrorBody (4xx) is generated too — the wire writes opIndex/activityId as explicit nulls;
// edit-api.ts's toError bridges those to the undefined-optional EditApiError fields.
export type {
  SlotCount, ActivityRef, PlannedActivity, SavedSlot, FailedSlot,
  SavePreflightResponse, SaveResponse, CommitSummaryResponse, RevertResponse,
  SourceTrackDto, SessionSourcesResponse, TrackEditErrorBody,
};

export type SaveMode = 'replace' | 'copy';

// Request DTOs — generated since D26 (their C# `required` members survive the request relaxation, so the
// shapes stay honest). SaveRequest re-narrows `mode` to the SaveMode refinement; AppendOpsRequest stays
// hand-written — its `ops` is the frozen EditOp chain (D15), which the generated split-union would break.
export type { CreateSessionRequest, MoveCursorRequest, SlotCountClaim };
export type SaveRequest = Omit<SaveRequestWire, 'mode'> & { mode: SaveMode };
export interface AppendOpsRequest { baseCursor: number; ops: EditOp[] }

/** Full own-session state. Kept hand-written: it builds on the frozen EditCore EditSessionState (its
 *  `script.ops` discriminated union + `status` refinement drive replay/entry consumers that the generated
 *  split-union — optional `op?`, required `gapSeconds` — would not type). SlotCount is generated. */
export interface SessionStateResponse extends EditSessionState {
  slotCounts: SlotCount[];
  updatedAt: string;
}

/** Mirror of C# EditResumeHint — the three values the resume banner prints (F-1). It arrives as the Details
 * page's data-resume-* attributes, NOT over HTTP (D11): a ride with no open edit makes no request at all. The
 * full session is fetched only when the user clicks Resume (getSession). */
export interface ResumeHint {
  sessionId: string;
  editCount: number;
  updatedAt: string;
}

/** One add-track suggestion / search result (A4 §3.2); mirror of C# SuggestedActivityDto. Scalar-only.
 * Kept hand-written (D15). MVC has no DefaultIgnoreCondition, so the nullable C# fields arrive as
 * EXPLICIT nulls, never omitted (the generated mirror types them required `null | number`) — hence the
 * `| null` here, and consumers guard with `!= null`; a bare `!== undefined` let wire nulls through and
 * rendered "0.0 km" for a NULL distance. */
export type { SuggestedActivityDto, TrackSuggestionsResponse } from '../generated/contracts';

// The 4xx TrackEditErrorBody shape is generated (re-exported above); only the code-string vocabulary
// below stays hand-written — it is a value set, not a wire shape.
export const TRACK_EDIT_API_ERRORS = {
  noActivities: 'noActivities',
  sessionNotActive: 'sessionNotActive',
  cursorConflict: 'cursorConflict',
  cursorOutOfRange: 'cursorOutOfRange',
  stillRecording: 'stillRecording',
  alreadyCheckedOut: 'alreadyCheckedOut',
  activityNotEditable: 'activityNotEditable',
  noTrackData: 'noTrackData',
  staleTrack: 'staleTrack',
  invalidSourceIds: 'invalidSourceIds',
  replayMismatch: 'replayMismatch',
  nothingToSave: 'nothingToSave',
  alreadyReverted: 'alreadyReverted',
  supersededCommit: 'supersededCommit',
  snapshotMissing: 'snapshotMissing',
  inputsPurged: 'inputsPurged',
} as const;
