using RouteBaseWeb.Models;
using System.Diagnostics.CodeAnalysis;
namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// HTTP DTOs for the TrackEdit API (A2 §2.1 + §14 deltas). All (de)serialization uses ASP.NET's
/// <c>JsonSerializerDefaults.Web</c> (camelCase) — the polymorphic <see cref="EditOp"/> flows through the
/// frozen contract. Contract changes here are atomic with the TS mirror <c>edit-session-api.ts</c>
/// (invariant 8). Additive over the frozen EditCore contracts — nothing here changes an existing type.
/// </summary>
public sealed record CreateSessionRequest(IReadOnlyList<int> ActivityIds);          // 1..MaxSlots

/// <summary>Truncate-then-append (A1 §2.7): <c>BaseCursor</c> must equal the stored cursor.</summary>
public sealed record AppendOpsRequest(int BaseCursor, IReadOnlyList<EditOp> Ops);

public sealed record MoveCursorRequest(int Cursor);

/// <summary>Save request: mode ∈ {replace, copy}; per-live-slot claimed point counts (invariant-1 verify).</summary>
public sealed record SaveRequest(string Mode, IReadOnlyList<SlotCountClaim> ExpectedCounts);

public sealed record SlotCountClaim(int SlotId, int PointCount);

/// <summary>Full session state (own-session API). <c>SlotCounts</c> = the server replay's per-slot counts
/// (the client cross-checks its preview). <c>UpdatedAt</c> (§14.2) drives A3's resume banner.</summary>
public sealed record SessionStateResponse(
    string SessionId, EditScript Script, int Cursor,
    IReadOnlyList<TrackVersionStamp> Stamps,
    // Stamped enum must equal the frozen edit-ops union 'active' | 'saved' | 'discarded' exactly.
    [property: RouteBaseWeb.Models.WireValues(typeof(RouteBaseWeb.Models.ActivityEditSessionStatuses))] string Status,
    IReadOnlyList<SlotCount> SlotCounts, DateTimeOffset UpdatedAt);

public sealed record SlotCount(int SlotId, int PointCount);

/// <summary>Everything the resume banner prints (F-1) — session id, edit count, "2 d ago". Server-RENDERED,
/// not fetched: Details emits it as data-resume-* attributes, so a ride with no open edit costs zero extra
/// HTTP requests. The full session is loaded only when the user clicks Resume. TS mirror: <c>ResumeHint</c>
/// in edit-session-api.ts (invariant 8).</summary>
public sealed record EditResumeHint(string SessionId, int EditCount, DateTimeOffset UpdatedAt);

public sealed record ActivityRef(int TrackId, string Name);

/// <summary>A yet-to-be-created activity's pre-save name (per the naming rule).</summary>
public sealed record PlannedActivity(int SlotId, string Name);

/// <summary>Pure dry-run of the save mapping (D-A2-5): the exact activities a Replace/Copy would update,
/// trash and create. <c>BlockedBy</c> ∈ {nothingToSave, sessionNotActive, null}.</summary>
public sealed record SavePreflightResponse(
    string Mode, IReadOnlyList<ActivityRef> Updated,
    IReadOnlyList<ActivityRef> Trashed, IReadOnlyList<PlannedActivity> Created, string? BlockedBy);

public sealed record SavedSlot(int SlotId, int TrackId, string Name, bool Created);

/// <summary>A slot whose phase-2 creation failed AFTER phase 1 committed: its sources are already trashed and
/// the commit exists, so reverting the commit restores them. Reported, never swallowed — a 200 that silently
/// dropped a ride would be worse than the failure.</summary>
public sealed record FailedSlot(int SlotId, string Name);

public sealed record SaveResponse(
    int CommitId, IReadOnlyList<SavedSlot> Slots, bool DeriveQueued,
    IReadOnlyList<FailedSlot>? FailedSlots = null);

public sealed record CommitSummaryResponse(
    int CommitId, string SaveMode, DateTimeOffset CommittedAt,
    IReadOnlyList<ActivityRef> Replaced, IReadOnlyList<ActivityRef> Trashed,
    IReadOnlyList<ActivityRef> Created, DateTimeOffset? RevertedAt, bool Revertible, string? BlockedBy);

public sealed record RevertResponse(int CommitId, int Restored, int Untrashed, int Deleted, bool DeriveQueued);

/// <summary>Typed error body: <c>Code</c> is a <see cref="TrackEditErrors"/>/<see cref="TrackEditApiErrors"/>
/// string; <c>OpIndex</c> pins a replay failure, <c>ActivityId</c> pins a per-source guard/stamp failure.
/// <c>Message</c> (API-06) is an optional human-readable supplement that brings this body into the D32
/// canonical <c>{code, message, …}</c> error family (ApiError's shape) — the frontend keys on <c>Code</c>
/// (see edit-api.ts <c>toError</c>), so it stays null unless a site attaches copy. TS mirror generated:
/// <c>TrackEditErrorBody</c> in generated/contracts.ts (wire writes the nullable fields as explicit null).</summary>
public sealed record TrackEditErrorBody(string Code, string? Message = null, int? OpIndex = null, int? ActivityId = null);

/// <summary>Stamp-verified quantized read of a session's source tracks (§14.1) — the preview twin's input.
/// Sources are in session load-op order.</summary>
public sealed record SessionSourcesResponse(IReadOnlyList<SourceTrackDto> Sources);

/// <summary>One source track as absolute quantized channels on the codec grid (§14.1): keys are the codec
/// channel names (= fixture keys), values are <c>RawQ[i] + QBias</c> (QBias ≠ 0 for time only), <c>NullQ</c>
/// → null, a whole-NULL channel → key absent. The client dequantizes with the existing CHANNEL_SCALES.
/// <c>Name</c> (D-A4-6, additive) is the owner-owned display name for chips/tooltips.</summary>
public sealed record SourceTrackDto(
    int ActivityId, string Name, double TzOffsetHours, int Count, IReadOnlyDictionary<string, long?[]> Channels);

/// <summary>One add-track suggestion / search result (A4 §3.2). Scalar-only (no per-point work).
/// <c>StartGapMeters</c> = haversine(edited-track end ↔ candidate start); null unless proximity-classed.
/// <c>NearEnd</c>/<c>SameDay</c> = the two offer classes (OR-combined); <c>AlreadyLoaded</c> = in this session.</summary>
public sealed record SuggestedActivityDto(
    int TrackId, string Name, string? ActivityType, string? StartDateLocal,
    double? TotalDistanceMeters, double? StartGapMeters, bool SameDay, bool NearEnd, bool AlreadyLoaded);

public sealed record TrackSuggestionsResponse(IReadOnlyList<SuggestedActivityDto> Suggestions);

/// <summary>API-layer error codes (additive; the frozen <see cref="TrackEditErrors"/> is untouched).
/// Grep-able string constants — never built from fragments; mirrored in edit-session-api.ts.</summary>
public static class TrackEditApiErrors
{
    public const string NoActivities         = "noActivities";       // create with an empty id list
    public const string SessionNotActive     = "sessionNotActive";
    public const string CursorConflict       = "cursorConflict";
    public const string CursorOutOfRange     = "cursorOutOfRange";
    public const string StillRecording       = "stillRecording";
    // The ride is already checked out into an open edit session (Activity.in_edit_session_id) — one open edit
    // per ride, enforced by the claim UPDATE. Also refuses revert/reset/restore while a ride is checked out:
    // moving its head under a live editor would splice the edit onto geometry it never saw.
    public const string AlreadyCheckedOut    = "alreadyCheckedOut";
    public const string ActivityNotEditable  = "activityNotEditable"; // owned but pending/discarded/edit-consumed
    public const string NoTrackData          = "noTrackData";
    public const string StaleTrack           = "staleTrack";
    public const string InvalidSourceIds     = "invalidSourceIds"; // sources ?ids= not a subset of the session stamps
    public const string ReplayMismatch       = "replayMismatch";
    public const string NothingToSave        = "nothingToSave";
    public const string AlreadyReverted      = "alreadyReverted";
    public const string SupersededCommit     = "supersededCommit";
    public const string SnapshotMissing      = "snapshotMissing";
    // A merge cannot be undone once its inputs are gone — un-merging means putting them BACK, and there is
    // nothing left to put back. Derived (invariant 4): no revert_blocked column, just the missing rows.
    public const string InputsPurged         = "inputsPurged";
}

public static class TrackEditSaveModes
{
    public const string Replace = "replace";
    public const string Copy = "copy";

    public static bool IsValid([NotNullWhen(true)] string? mode) => mode is Replace or Copy;
}
