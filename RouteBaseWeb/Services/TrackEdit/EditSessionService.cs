using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sql;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>How the controller should map a service error to an HTTP status.</summary>
public enum TrackEditErrorKind { NotFound, Conflict, BadRequest, Unprocessable }

/// <summary>A session mutation/read result: exactly one of an error (with its HTTP kind) or a response.</summary>
public sealed record SessionActionResult(
    TrackEditErrorKind? ErrorKind, TrackEditErrorBody? Error, SessionStateResponse? Response)
{
    public static SessionActionResult Fail(TrackEditErrorKind kind, TrackEditErrorBody body) => new(kind, body, null);
    public static SessionActionResult NotFound() => new(TrackEditErrorKind.NotFound, null, null);
    public static SessionActionResult Ok(SessionStateResponse response) => new(null, null, response);
    /// <summary>Success with NO body — the idempotent-discard "already gone" answer (controller sends {}).</summary>
    public static SessionActionResult OkEmpty() => new(null, null, null);
}

/// <summary>Discard/sources results: an error kind + body, or the (typed) success payload.</summary>
public sealed record SessionSourcesResult(
    TrackEditErrorKind? ErrorKind, TrackEditErrorBody? Error, SessionSourcesResponse? Response);

/// <summary>
/// Owns the edit-session lifecycle (active → saved | discarded) and the write-through rules (A1 §2.7).
/// Every geometric check is a replay-engine check run server-side over reader-cached decodes (D-A2-2 — one
/// validation authority, no duplicated logic); version stamps are computed from the raw blob bytes at load
/// time (invariant 1). Owner-scoped throughout — a foreign/missing session is 404 (never leak existence).
/// Returns typed results; the controller maps them to HTTP. Save/revert live in their own services (I2c).
/// </summary>
public sealed class EditSessionService
{
    private readonly EditSessionStore _sessions;
    private readonly TrackContentStore _content;
    private readonly RecordingStagingStore _staging;
    private readonly ITrackReader _reader;
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;
    private readonly TimeProvider _clock;

    public EditSessionService(
        EditSessionStore sessions, TrackContentStore content, RecordingStagingStore staging,
        ITrackReader reader, RouteBaseDbContext context, IVisibilityService visibility,
        TimeProvider clock)
    {
        _sessions = sessions;
        _content = content;
        _staging = staging;
        _reader = reader;
        _context = context;
        _visibility = visibility;
        _clock = clock;
    }

    // ---- create / append / cursor / discard ----

    public async Task<SessionActionResult> CreateAsync(CreateSessionRequest request, CancellationToken ct = default)
    {
        var ids = request.ActivityIds ?? [];
        if (ids.Count == 0) return Bad(TrackEditApiErrors.NoActivities);
        if (ids.Count > TrackEditLimits.MaxSlots) return Bad(TrackEditErrors.SlotCapExceeded);
        // Reject a duplicate id up front (same code+status the replay would produce) — avoids fetching + SHA-256
        // hashing a multi-MB blob once per copy before replay rejects (R-17.3).
        if (ids.Distinct().Count() != ids.Count) return Bad(TrackEditErrors.DuplicateLoad);

        var stamps = new List<TrackVersionStamp>(ids.Count);
        var ops = new List<EditOp>(ids.Count);
        foreach (int id in ids)
        {
            var (kind, error, stamp) = await GuardAndStampAsync(id, ct);
            // GuardAndStamp's contract: error ⇒ kind, else stamp. `?? throw` keeps a broken
            // invariant a NAMED failure instead of a null-forgiven NRE (H2).
            if (error is not null)
                return SessionActionResult.Fail(
                    kind ?? throw new InvalidOperationException("GuardAndStamp returned an error without a kind"), error);
            stamps.Add(stamp ?? throw new InvalidOperationException("GuardAndStamp returned neither error nor stamp"));
            ops.Add(new LoadActivityOp(id));
        }

        var script = new EditScript(1, ops);
        var outcome = await ReplayEffectiveAsync(script, ct);
        if (outcome.Error is not null)
            return Bad(outcome.Error.Code); // create-time engine codes (duplicateLoad, slotCapExceeded…) → 400

        var now = _clock.GetUtcNow().UtcDateTime;
        var snapshot = new EditSessionSnapshot(
            Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture), _visibility.GetCurrentUserId(), script, ops.Count,
            stamps, ActivityEditSessionStatuses.Active, now, now);

        // CHECK OUT the ride (slot 0) in the same transaction as the session insert: the session row must exist
        // before the FK can point at it, and the ride must be claimable or neither may land.
        await using var tx = await _context.Database.BeginTransactionAsync(ct);
        await _sessions.CreateAsync(snapshot, ct);
        if (!await ClaimAsync(ids[0], snapshot.SessionId, ct))
        {
            await tx.RollbackAsync(ct);
            return Conflict(TrackEditApiErrors.AlreadyCheckedOut);
        }
        await tx.CommitAsync(ct);
        return SessionActionResult.Ok(ToResponse(snapshot, SlotCountsOf(outcome)));
    }

    /// <summary>
    /// The lock, as one statement: claim the ride only if it is free. The <c>IS NULL</c> guard is what makes it
    /// atomic — a read-then-write would let two tabs both see "free" and open two sessions on the same ride,
    /// which would then race at save. 0 rows ⇒ someone already has it checked out.
    /// (The UI never hits this: Details renders the resume banner first, so Edit on a checked-out ride offers
    /// Resume/Discard, never an error.)
    /// </summary>
    private async Task<bool> ClaimAsync(int activityId, string sessionId, CancellationToken ct) =>
        await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.ClaimEditSessionSql,
            [sessionId, activityId, _visibility.GetCurrentUserId()], ct) == 1;

    public async Task<SessionActionResult> AppendOpsAsync(
        string sessionId, AppendOpsRequest request, CancellationToken ct = default)
    {
        var session = await GetOwnedSessionAsync(sessionId, ct);
        if (session is null) return SessionActionResult.NotFound();
        if (session.Status != ActivityEditSessionStatuses.Active) return Conflict(TrackEditApiErrors.SessionNotActive);
        if (request.BaseCursor != session.Cursor) return Conflict(TrackEditApiErrors.CursorConflict);

        var effectiveOps = session.Script.Ops.Take(request.BaseCursor).Concat(request.Ops ?? []).ToList();
        if (effectiveOps.Count > TrackEditLimits.MaxOps) return Bad(TrackEditErrors.ScriptTooLong);

        // Guard + stamp any activity newly loaded by these ops (existing stamps are never re-hashed).
        var stamps = session.Stamps.ToList();
        foreach (int newId in effectiveOps.OfType<LoadActivityOp>().Select(o => o.ActivityId).Distinct())
        {
            if (stamps.Any(s => s.ActivityId == newId)) continue;
            var (kind, error, stamp) = await GuardAndStampAsync(newId, ct);
            if (error is not null)
                return SessionActionResult.Fail(
                    kind ?? throw new InvalidOperationException("GuardAndStamp returned an error without a kind"), error);
            stamps.Add(stamp ?? throw new InvalidOperationException("GuardAndStamp returned neither error nor stamp"));
        }
        // Prune stamps for loads a truncating append dropped — effectiveOps IS the new full script, so an
        // undone-away load's stamp dies with its op (R-04: an orphan stamp must not stale-block a later save).
        var usedIds = effectiveOps.OfType<LoadActivityOp>().Select(o => o.ActivityId).ToHashSet();
        stamps = stamps.Where(s => usedIds.Contains(s.ActivityId)).ToList();

        var script = new EditScript(1, effectiveOps);
        var outcome = await ReplayEffectiveAsync(script, ct);
        if (outcome.Error is not null)
            return SessionActionResult.Fail(TrackEditErrorKind.Unprocessable,
                new TrackEditErrorBody(outcome.Error.Code, OpIndex: outcome.Error.OpIndex));

        var now = _clock.GetUtcNow().UtcDateTime;
        // Conditional on BaseCursor: a racing append that already moved the cursor makes this write a 0-row
        // no-op → cursorConflict (client freezes+resyncs), never a silent lost update.
        if (!await _sessions.UpdateScriptCursorStampsAsync(
                sessionId, script, effectiveOps.Count, stamps, now, expectedCursor: request.BaseCursor, ct: ct))
            return Conflict(TrackEditApiErrors.CursorConflict);
        var updated = session with { Script = script, Cursor = effectiveOps.Count, Stamps = stamps, UpdatedAt = now };
        return SessionActionResult.Ok(ToResponse(updated, SlotCountsOf(outcome)));
    }

    public async Task<SessionActionResult> MoveCursorAsync(
        string sessionId, MoveCursorRequest request, CancellationToken ct = default)
    {
        var session = await GetOwnedSessionAsync(sessionId, ct);
        if (session is null) return SessionActionResult.NotFound();
        if (session.Status != ActivityEditSessionStatuses.Active) return Conflict(TrackEditApiErrors.SessionNotActive);
        if (request.Cursor < 0 || request.Cursor > session.Script.Ops.Count)
            return Bad(TrackEditApiErrors.CursorOutOfRange);

        var now = _clock.GetUtcNow().UtcDateTime;
        // Persist cursor only — redo tail (ops[cursor..]) stays for a later redo (E26 truncation is append's job).
        // Conditional on the cursor we read: a racing move/append that changed it → cursorConflict, not a stomp.
        if (!await _sessions.UpdateScriptCursorStampsAsync(
                sessionId, session.Script, request.Cursor, session.Stamps, now, expectedCursor: session.Cursor, ct: ct))
            return Conflict(TrackEditApiErrors.CursorConflict);
        var moved = session with { Cursor = request.Cursor, UpdatedAt = now };
        return SessionActionResult.Ok(ToResponse(moved, CountEffective(moved, request.Cursor)));
    }

    /// <summary>
    /// Abort (§14.6): DELETE the session row. That is the whole operation — the FK's ON DELETE SET NULL
    /// releases the ride's <c>in_edit_session_id</c>, so the check-out ends with the row that recorded it, and
    /// discarded rows stop accumulating forever. No data was ever touched (invariant 3).
    ///
    /// Idempotent: a re-discard finds nothing → 200. Only a SAVED session refuses (409) — its commit references
    /// it. A concurrent save that flips the status between our read and the delete makes the guarded delete a
    /// 0-row no-op, and we report the same 409 rather than deleting the commit's provenance.
    /// </summary>
    public async Task<SessionActionResult> DiscardAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await _sessions.GetAsync(sessionId, ct);
        if (session is null) return SessionActionResult.OkEmpty(); // already gone — controller sends {}
        if (session.UserId != _visibility.GetCurrentUserId()) return SessionActionResult.NotFound();
        if (session.Status == ActivityEditSessionStatuses.Saved) return Conflict(TrackEditApiErrors.SessionNotActive);

        if (!await _sessions.DeleteAsync(sessionId, ct))
            return Conflict(TrackEditApiErrors.SessionNotActive); // a save flipped it to 'saved' under us
        return SessionActionResult.OkEmpty();
    }

    // ---- reads: get / preflight / sources ----

    public async Task<SessionActionResult> GetAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await GetOwnedSessionAsync(sessionId, ct);
        if (session is null) return SessionActionResult.NotFound();
        return SessionActionResult.Ok(ToResponse(session, CountEffective(session, session.Cursor)));
    }

    /// <summary>The resume banner's three values for one ride, or null when the owner has no open edit on it.
    /// Read from the ride's checkout pointer: Details already has the Activity, so the hint costs the pointer
    /// plus one PK row of the session. Owner-scoped like every other read here.</summary>
    public async Task<EditResumeHint?> GetResumeHintAsync(int activityId, CancellationToken ct = default)
    {
        string? sessionId = await CheckedOutSessionIdAsync(activityId, ct);
        return sessionId is null
            ? null
            : await _sessions.FindResumeHintAsync(_visibility.GetCurrentUserId(), sessionId, ct);
    }

    /// <summary>The session this ride is checked out into, owner-scoped (null = free, or not the caller's).</summary>
    private Task<string?> CheckedOutSessionIdAsync(int activityId, CancellationToken ct) =>
        _visibility.OwnedActivities(_context.Activities)
            .Where(a => a.TrackId == new TrackId(activityId))
            .Select(a => a.InEditSessionId)
            .FirstOrDefaultAsync(ct);

    /// <summary>Pure dry-run of the save mapping (D-A2-5). Returns null when the session is missing/foreign
    /// (404); a non-active session yields a response with <c>BlockedBy = sessionNotActive</c>.</summary>
    public async Task<SavePreflightResponse?> PreflightAsync(string sessionId, string mode, CancellationToken ct = default)
    {
        var session = await GetOwnedSessionAsync(sessionId, ct);
        if (session is null) return null;
        if (session.Status != ActivityEditSessionStatuses.Active)
            return new SavePreflightResponse(mode, [], [], [], TrackEditApiErrors.SessionNotActive);

        var effectiveOps = session.Script.Ops.Take(session.Cursor).ToList();
        var rootIds = effectiveOps.OfType<LoadActivityOp>().Select(o => o.ActivityId).Distinct().ToList();
        var names = await ActivityMetaQueries.NamesAsync(_context, rootIds.Select(i => new TrackId(i)), ct);
        var plan = SaveMapper.Map(effectiveOps, mode, names.ToDictionary(kv => kv.Key.Value, kv => kv.Value));
        return new SavePreflightResponse(
            mode,
            plan.Updated.Select(u => new ActivityRef(u.ActivityId, u.Name)).ToList(),
            plan.Trashed.Select(t => new ActivityRef(t.ActivityId, t.Name)).ToList(),
            plan.Created.Select(c => new PlannedActivity(c.SlotId, c.Name)).ToList(),
            plan.BlockedBy);
    }

    /// <summary>Stamp-verified quantized read of the session's sources (§14.1): each source blob is re-hashed
    /// against its stamp (mismatch/absent → 409 staleTrack + activityId, the early-staleness signal), decoded,
    /// and serialized as absolute quantized channels for the preview twin. Sources in load-op order. Optional
    /// <paramref name="ids"/> (D-A4-6, csv ints) narrows to a subset of the stamped sources — add-track fetches
    /// only its ONE new source; a non-stamped id → 400 invalidSourceIds.</summary>
    public async Task<SessionSourcesResult> GetSourcesAsync(
        string sessionId, string? ids = null, CancellationToken ct = default)
    {
        var session = await GetOwnedSessionAsync(sessionId, ct);
        if (session is null) return new SessionSourcesResult(TrackEditErrorKind.NotFound, null, null);

        var wanted = ParseIdsFilter(ids, session.Stamps.Select(s => s.ActivityId));
        if (wanted is null && ids is not null && !string.IsNullOrWhiteSpace(ids))
            return new SessionSourcesResult(TrackEditErrorKind.BadRequest,
                new TrackEditErrorBody(TrackEditApiErrors.InvalidSourceIds), null);

        var selected = session.Stamps.Where(s => wanted is null || wanted.Contains(s.ActivityId)).ToList();
        var trackIds = selected.Select(s => s.ActivityId).ToList();
        var metaByTrack = await ActivityMetaQueries.NameAndTzAsync(_context, trackIds.Select(i => new TrackId(i)).ToList(), ct);
        var sources = new List<SourceTrackDto>(selected.Count);
        foreach (var stamp in selected)
        {
            var bytes = await _content.GetHeadDataAsync(new TrackId(stamp.ActivityId), ct);
            if (bytes is null || TrackEditContracts.ComputeBlobSha256(bytes) != stamp.BlobSha256)
                return new SessionSourcesResult(TrackEditErrorKind.Conflict,
                    new TrackEditErrorBody(TrackEditApiErrors.StaleTrack, ActivityId: stamp.ActivityId), null);

            var meta = metaByTrack.GetValueOrDefault(new TrackId(stamp.ActivityId));
            var track = TrackBlobCodec.Decode(bytes);
            sources.Add(new SourceTrackDto(
                stamp.ActivityId,
                meta.Name ?? $"Activity {stamp.ActivityId}",
                meta.TzHours,
                track.Count,
                TrackEditConverters.ToAbsoluteQuantizedChannels(track)));
        }
        return new SessionSourcesResult(null, null, new SessionSourcesResponse(sources));
    }

    /// <summary>Parse a csv-int <c>ids</c> filter to a validated subset of <paramref name="stamped"/>; null when
    /// absent (whole session) or invalid (a non-int part or an id outside the session's stamps → caller 400).</summary>
    private static HashSet<int>? ParseIdsFilter(string? ids, IEnumerable<int> stamped)
    {
        if (string.IsNullOrWhiteSpace(ids)) return null;
        var stampedSet = stamped.ToHashSet();
        var wanted = new HashSet<int>();
        foreach (var part in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var id) || !stampedSet.Contains(id)) return null;
            wanted.Add(id);
        }
        return wanted.Count == 0 ? null : wanted;
    }

    // ---- guards + helpers ----

    /// <summary>Per-activity load guard (A2 §3), in order: owned+exists (404-class), confirmed
    /// (else activityNotEditable), no staging tail (else stillRecording), blob present (else noTrackData).
    /// On success returns the version stamp from the SAME bytes it hashed (invariant 1).</summary>
    private async Task<(TrackEditErrorKind? Kind, TrackEditErrorBody? Error, TrackVersionStamp? Stamp)>
        GuardAndStampAsync(int activityId, CancellationToken ct)
    {
        var lifecycle = await _visibility.OwnedActivities(_context.Activities)
            .Where(a => a.TrackId == new TrackId(activityId))
            .Select(a => a.Lifecycle)
            .FirstOrDefaultAsync(ct);
        if (lifecycle is null) return (TrackEditErrorKind.NotFound, new TrackEditErrorBody("notFound"), null);
        if (lifecycle != LifecycleStates.Released)
            return (TrackEditErrorKind.Conflict,
                new TrackEditErrorBody(TrackEditApiErrors.ActivityNotEditable, ActivityId: activityId), null);
        if (await _staging.ExistsAsync(new TrackId(activityId), ct))
            return (TrackEditErrorKind.Conflict,
                new TrackEditErrorBody(TrackEditApiErrors.StillRecording, ActivityId: activityId), null);

        var bytes = await _content.GetHeadDataAsync(new TrackId(activityId), ct);
        if (bytes is null || bytes.Length < 8)
            return (TrackEditErrorKind.Conflict,
                new TrackEditErrorBody(TrackEditApiErrors.NoTrackData, ActivityId: activityId), null);

        var stamp = new TrackVersionStamp(
            activityId, TrackEditContracts.BlobHeaderPointCount(bytes), TrackEditContracts.ComputeBlobSha256(bytes));
        return (null, null, stamp);
    }

    private async Task<ReplayOutcome> ReplayEffectiveAsync(EditScript script, CancellationToken ct)
    {
        var sources = new Dictionary<int, DecodedTrack>();
        foreach (var op in script.Ops)
            if (op is LoadActivityOp load && !sources.ContainsKey(load.ActivityId))
            {
                var track = await _reader.GetTrackAsync(new TrackId(load.ActivityId), ct);
                if (track is not null) sources[load.ActivityId] = track;
            }
        return EditReplayEngine.ReplayScript(script, sources);
    }

    private async Task<EditSessionSnapshot?> GetOwnedSessionAsync(string sessionId, CancellationToken ct)
    {
        var session = await _sessions.GetAsync(sessionId, ct);
        return session is not null && session.UserId == _visibility.GetCurrentUserId() ? session : null;
    }

    /// <summary>Read-path slot counts (TE-01): pure index arithmetic over the session's STAMPED point counts —
    /// no blob read, no decode, no per-point allocation. The full geometric replay stays on the write paths
    /// (create/append/save), which must see channel values to validate geometry; a read path only ever replays
    /// a script those already accepted. An error (unreachable for an accepted script) ⇒ empty list, exactly as
    /// <see cref="SlotCountsOf"/> does.</summary>
    private static IReadOnlyList<SlotCount> CountEffective(EditSessionSnapshot s, int cursor)
    {
        var (error, slots) = EditReplayEngine.CountSlots(
            new EditScript(1, s.Script.Ops.Take(cursor).ToList()),
            s.Stamps.ToDictionary(st => st.ActivityId, st => st.PointCount));
        return error is not null ? [] : slots;
    }

    private static IReadOnlyList<SlotCount> SlotCountsOf(ReplayOutcome outcome) =>
        outcome.Error is not null
            ? []
            : outcome.Slots.Select(s => new SlotCount(s.SlotId, s.Track.Count)).ToList();

    private static SessionStateResponse ToResponse(EditSessionSnapshot s, IReadOnlyList<SlotCount> counts) =>
        new(s.SessionId, s.Script, s.Cursor, s.Stamps, s.Status, counts, s.UpdatedAt.ToUtcOffset());

    private static SessionActionResult Conflict(string code) =>
        SessionActionResult.Fail(TrackEditErrorKind.Conflict, new TrackEditErrorBody(code));

    private static SessionActionResult Bad(string code) =>
        SessionActionResult.Fail(TrackEditErrorKind.BadRequest, new TrackEditErrorBody(code));
}
