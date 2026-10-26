using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Services;

/// <inheritdoc />
public class RecordingService : IRecordingService
{
    private readonly RouteBaseDbContext _context;
    private readonly IActivityImportService _importService;
    private readonly RecordingStagingStore _stagingStore;
    private readonly ITrackReader _reader;
    private readonly IActivityCommandService _command;
    private readonly RecordingFinalizationCoordinator _finalization;
    private readonly ILogger<RecordingService> _logger;
    private readonly TimeProvider _clock;

    // Per-session append serialization (RS-04). Striped async locks: process-wide (matches the in-process
    // rate limiter's single-instance premise), bounded memory, no per-sessionId eviction lifecycle. Two
    // in-flight appends for one session (client timeout-retry racing the original) run under the same lock,
    // so the dedup re-read + startSeq computation can't interleave into a PK collision / double-insert.
    private static readonly SemaphoreSlim[] AppendLocks =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static SemaphoreSlim AppendLockFor(string sessionId) =>
        AppendLocks[(sessionId.GetHashCode(StringComparison.Ordinal) & 0x7fffffff) % AppendLocks.Length];

    public RecordingService(
        RouteBaseDbContext context,
        IActivityImportService importService,
        RecordingStagingStore stagingStore,
        ITrackReader reader,
        IActivityCommandService command,
        RecordingFinalizationCoordinator finalization,
        ILogger<RecordingService> logger,
        TimeProvider clock)
    {
        _context = context;
        _importService = importService;
        _stagingStore = stagingStore;
        _reader = reader;
        _command = command;
        _finalization = finalization;
        _logger = logger;
        _clock = clock;
    }

    // An active AUTO session whose last append is older than this is a dead session (the phone idle-stops
    // at 8 min and grace-finalizes at ~23 min) — a new start must not 409 into it. Rescuing here is the
    // server-side net for a phone whose stop never arrived (killed process / failed stop call): the old
    // behavior made the reconnecting client append into it hours later (a 2h12m in-activity hole).
    private static readonly TimeSpan StaleAutoStartThreshold = TimeSpan.FromMinutes(30);

    /// <inheritdoc />
    public async Task<RecordingSession?> StartSessionAsync(StartRecordingRequest request, string userId)
    {
        // Rescue-before-transaction: finalization runs its own scopes/connections and must not contend
        // with an open write transaction (SQLite single-writer).
        await FinalizeStaleAutoSessionAsync(userId);

        // One transaction (RS-02): pins the connection so last_insert_rowid() is read on the same handle
        // that did the INSERT (rowid is per-connection), AND makes the Activity shell + session row atomic —
        // no orphan stats-less shell if a write fails partway. The partial unique active-session index
        // (DbSchemaManager) is the race-proof backstop for the check-then-insert below.
        await using var tx = await _context.Database.BeginTransactionAsync();
        try
        {
            // Enforce single active session per user — prevents ghost livestreams. Re-checked inside the tx.
            var existing = await GetActiveSessionAsync(userId);
            if (existing is not null)
            {
                _logger.LogWarning(
                    "User {UserId} tried to start a session but already has active session {SessionId}",
                    userId, existing.SessionId);
                return null; // await using rolls back — nothing was written yet.
            }

            var sessionId = Guid.NewGuid().ToString();
            var now = _clock.GetUtcNow().UtcDateTime;

            // Insert the Activity shell — stats are NULL until finalization. An auto-detected session lands
            // in-work/awaiting-review (hidden from lists until the user approves it in the review queue); a
            // manual live recording lands released, so the ride is watchable on the map/list as it records.
            var source = request.AutoDetected ? ActivitySources.AutoRecording : ActivitySources.LiveRecording;
            var (lifecycle, reason) = request.AutoDetected
                ? (LifecycleStates.InWork,   LifecycleReasons.AwaitingReview)
                : (LifecycleStates.Released, LifecycleReasons.Recording);
            // The auto-detect client sends no name, so the shell would otherwise carry the manual default
            // and read as a hand-started ride. A name the client DID send always wins.
            var trackName = request.AutoDetected && request.TrackName == StartRecordingRequest.DefaultTrackName
                ? StartRecordingRequest.AutoDetectedTrackName
                : request.TrackName;
            await _context.Database.ExecuteSqlRawAsync(Sql.ActivityRecordingSql.InsertShellSql,
                trackName,
                request.ActivityType,
                now,
                source,
                lifecycle,
                reason,
                userId,
                request.Visibility);

            var trackIdResult = await _context.Database
                .SqlQueryRaw<int>("SELECT last_insert_rowid() as Value")
                .ToListAsync();
            var trackId = trackIdResult.First();

            var session = new RecordingSession
            {
                SessionId = sessionId,
                TrackId = new TrackId(trackId),
                UserId = userId,
                Status = RecordingSessionStatus.Recording,
                ActivityType = request.ActivityType,
                StartedAt = now,
                Visibility = request.Visibility,
                ClientVersion = request.ClientVersion,
                TimezoneOffsetMinutes = request.TimezoneOffsetMinutes,
            };

            _context.RecordingSessions.Add(session);
            await _context.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation(
                "Started recording session {SessionId} → track_ID {TrackId} for user {UserId}",
                sessionId, trackId, userId);

            return session;
        }
        catch (Exception ex) when (
            (ex.GetBaseException() as Microsoft.Data.Sqlite.SqliteException) is { SqliteErrorCode: 19 } sq
            && sq.Message.Contains("IX_RecordingSession_ActiveUser", StringComparison.Ordinal))
        {
            // Concurrent start lost the race on the unique active-session index — same contract as the
            // pre-check. await using rolls the tx back, removing the Activity shell too. Matched on the
            // INDEX, not on bare SQLITE_CONSTRAINT (SVC-07): code 19 also covers FK/NOT NULL/CHECK, and a
            // genuine schema failure must surface as the 500 it is, not masquerade as a permanent 409
            // (IsSeqUniqueViolation precedent).
            _logger.LogWarning("User {UserId} lost a concurrent start race for a new session", userId);
            return null;
        }
    }

    /// <summary>
    /// If the user's active session is an auto-recording with no appends for
    /// <see cref="StaleAutoStartThreshold"/>, stop it and attempt finalization so a new start can proceed.
    /// Fresh sessions and manual recordings are untouched (the 409/reconnect contract stays). A finalize
    /// failure is non-fatal: the session is 'stopped', so the cleanup sweep retries within ~1 h (RF-02).
    /// </summary>
    private async Task FinalizeStaleAutoSessionAsync(string userId)
    {
        var stale = await GetActiveSessionAsync(userId);
        if (stale is null) return;

        var idleSince = stale.LastPointAt ?? stale.StartedAt;
        if (_clock.GetUtcNow().UtcDateTime - idleSince < StaleAutoStartThreshold) return;

        var isAuto = await _context.Activities
            .AnyAsync(a => a.TrackId == stale.TrackId && a.Source == ActivitySources.AutoRecording);
        if (!isAuto) return;

        _logger.LogWarning(
            "Start found stale auto session {SessionId} (idle since {IdleSince:u}); finalizing it instead of 409",
            stale.SessionId, idleSince);

        stale.Status = RecordingSessionStatus.Stopped;
        stale.StoppedAt = _clock.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync();

        try
        {
            await _finalization.FinalizeAsync(stale.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Finalize of stale auto session {SessionId} failed; cleanup sweep will retry", stale.SessionId);
        }
    }

    /// <inheritdoc />
    public async Task<List<ExternalGpsPoint>?> AppendPointsAsync(
        string sessionId, RecordingPointsBatch batch, string userId)
    {
        // Serialize appends for this session so the dedup read, startSeq count, and insert are one atomic
        // step (RS-04). The session must be re-read INSIDE the lock or the stale-read race survives.
        var gate = AppendLockFor(sessionId);
        await gate.WaitAsync();
        try
        {
            return await AppendPointsCoreAsync(sessionId, batch, userId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<ExternalGpsPoint>?> AppendPointsCoreAsync(
        string sessionId, RecordingPointsBatch batch, string userId)
    {
        var session = await _context.RecordingSessions.FindAsync(sessionId);

        if (session is null || session.UserId != userId || session.Status != RecordingSessionStatus.Recording)
            return null;

        // Deduplication: reject batches we have already processed.
        if (batch.ClientSequence <= session.LastSequence)
        {
            _logger.LogDebug(
                "Duplicate batch {Seq} for session {SessionId} (last accepted: {Last}), skipping",
                batch.ClientSequence, sessionId, session.LastSequence);
            return [];
        }

        if (batch.Points.Count == 0)
            return [];

        // Build ExternalGpsPoint list with cumulative distances.
        // Seed BOTH the running distance and the previous point from the last stored point, so the leg
        // between batches is accumulated (INC-01/RS-01): staged tail first, else the compacted blob
        // (session spanning the migration deploy), else fresh start.
        var gpsPoints = new List<ExternalGpsPoint>(batch.Points.Count);
        double cumulativeDistance;
        ExternalGpsPoint? prev;
        var lastStaged = await _stagingStore.GetLastPointAsync(session.TrackId);
        if (lastStaged is { } lp)
        {
            cumulativeDistance = lp.Distance ?? await _stagingStore.GetMaxDistanceAsync(session.TrackId) ?? 0.0;
            prev = new ExternalGpsPoint { Lat = lp.Lat, Lng = lp.Lng, Distance = lp.Distance };
        }
        else
        {
            var lastBlob = await LastBlobPointAsync(session.TrackId);
            cumulativeDistance = lastBlob?.Distance ?? 0.0;
            prev = lastBlob;
        }

        foreach (var pt in batch.Points)
        {
            if (prev is not null)
                cumulativeDistance += GeoUtils.HaversineDistance(prev.Lat, prev.Lng, pt.Lat, pt.Lng);

            gpsPoints.Add(new ExternalGpsPoint
            {
                Lat = pt.Lat,
                Lng = pt.Lng,
                Altitude = pt.Altitude,
                Speed = pt.Speed,
                Distance = cumulativeDistance,
                Pressure = pt.Pressure,
                TimestampUtc = pt.TimestampUtc,
            });

            prev = gpsPoints[^1];
        }

        // Live capture goes to the uncompacted staging tail (compacted into a blob on finalize). seq =
        // next dense ordinal (MAX+1 ≡ row count while appendable — see NextSeqAsync). The reader concats
        // staging over any blob, so no cache invalidation is needed — staging-backed decodes are never cached.
        //
        // One transaction (RS-05): the staging INSERT, bbox upsert, and the LastSequence advance must be
        // all-or-nothing. A crash after the INSERT but before SaveChangesAsync would otherwise leave points
        // stored with LastSequence NOT advanced → the client's retry of the same ClientSequence passes
        // dedup and re-appends duplicates. Rollback → the retry does a clean full re-insert.
        await using var tx = await _context.Database.BeginTransactionAsync();
        long startSeq = await _stagingStore.NextSeqAsync(session.TrackId);
        // Stamp the ordinal the INSERT is about to assign, so the returned points — which are what the
        // SignalR broadcast serializes — carry the same cursor the catch-up reads back (LV-01). Back-filled
        // rather than folded into the build loop above: startSeq is only known inside the transaction.
        for (int i = 0; i < gpsPoints.Count; i++) gpsPoints[i].Seq = startSeq + i;
        await _stagingStore.AppendAsync(session.TrackId, startSeq, gpsPoints);
        await _importService.UpsertActivityBboxAsync(session.TrackId, gpsPoints);

        // Update session metadata.
        var lastPointTime = batch.Points.Max(p => p.TimestampUtc);
        session.PointCount += gpsPoints.Count;
        session.LastPointAt = lastPointTime;
        session.LastSequence = batch.ClientSequence;
        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        return gpsPoints;
    }

    /// <inheritdoc />
    public async Task<RecordingSession?> StopSessionAsync(string sessionId, string userId)
    {
        var session = await _context.RecordingSessions.FindAsync(sessionId);

        if (session is null || session.UserId != userId)
            return null;

        if (session.Status is not (RecordingSessionStatus.Recording or RecordingSessionStatus.Paused))
            return session; // Already stopped — idempotent.

        session.Status = RecordingSessionStatus.Stopped;
        session.StoppedAt = _clock.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Stopped recording session {SessionId} (track_ID {TrackId})", sessionId, session.TrackId);

        return session;
    }

    /// <inheritdoc />
    public async Task<RecordingSession?> PauseSessionAsync(string sessionId, string userId)
    {
        var session = await _context.RecordingSessions.FindAsync(sessionId);

        if (session is null || session.UserId != userId || session.Status != RecordingSessionStatus.Recording)
            return null;

        session.Status = RecordingSessionStatus.Paused;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Paused recording session {SessionId}", sessionId);
        return session;
    }

    /// <inheritdoc />
    public async Task<RecordingSession?> ResumeSessionAsync(string sessionId, string userId)
    {
        var session = await _context.RecordingSessions.FindAsync(sessionId);

        if (session is null || session.UserId != userId || session.Status != RecordingSessionStatus.Paused)
            return null;

        session.Status = RecordingSessionStatus.Recording;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Resumed recording session {SessionId}", sessionId);
        return session;
    }

    /// <inheritdoc />
    public async Task<RecordingSession?> RetypeSessionAsync(string sessionId, string activityType, string userId)
    {
        if (string.IsNullOrWhiteSpace(activityType))
            return null;

        var session = await _context.RecordingSessions.FindAsync(sessionId);

        if (session is null || session.UserId != userId ||
            session.Status is not (RecordingSessionStatus.Recording or RecordingSessionStatus.Paused))
            return null;

        session.ActivityType = activityType;
        await _context.SaveChangesAsync();

        // Keep the Activity shell (created at start) in sync — it is what finalization and the UI
        // read. Routed through the audited command service (arch finding D) rather than a raw EF
        // write; session ownership was already checked above, so this just no-ops if the shell row
        // is somehow missing, matching the previous "if (activity is not null)" tolerance.
        await _command.UpdateActivityAsync(session.TrackId, name: null, activityType: activityType, visibility: null);

        _logger.LogInformation("Retyped session {SessionId} → {ActivityType}", sessionId, activityType);
        return session;
    }

    /// <inheritdoc />
    public async Task<RecordingSession?> GetActiveSessionAsync(string userId)
    {
        return await _context.RecordingSessions
            .Where(s => s.UserId == userId && (s.Status == RecordingSessionStatus.Recording
                                               || s.Status == RecordingSessionStatus.Paused))
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc />
    public async Task<List<RecordingSession>> GetActiveSessionsVisibleToAsync(List<string> visibleUserIds)
    {
        // "friends" and "public" are both viewable by the friend set passed in; private stays owner-only.
        // Paused counts as active (API-06): the hub lets viewers join and stay attached through a pause,
        // and the owner's own picker (GetActiveSessionAsync) includes paused — a friend at a café must
        // not vanish from the live picker mid-ride only to "reappear" on resume.
        return await _context.RecordingSessions
            .Where(s => (s.Status == RecordingSessionStatus.Recording
                         || s.Status == RecordingSessionStatus.Paused)
                && visibleUserIds.Contains(s.UserId)
                && s.Visibility != Visibilities.Private)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<RecordingSession?> GetSessionAsync(string sessionId)
    {
        return await _context.RecordingSessions.FindAsync(sessionId);
    }

    /// <inheritdoc />
    public async Task<RecordingSession?> GetLiveShareSessionAsync(TrackId trackId, string ownerUserId, CancellationToken ct)
    {
        // A session is 1:1 with its Activity shell's track_id (each start inserts a fresh shell), so this is
        // unambiguous. The Released clause is the share-READ lifecycle gate (see the interface doc): manual
        // recordings are Released from the start, so live viewing works mid-ride; auto recordings are
        // InWork until approved and a trashed ride stops serving the moment it is trashed. The owner clause
        // pins the grant's owner on BOTH rows, mirroring ShareSegmentReader.GrantedSegment.
        return await _context.RecordingSessions
            .Where(s => s.TrackId == trackId && s.UserId == ownerUserId)
            .Where(s => _context.Activities.Any(a =>
                a.TrackId == trackId
                && a.UserId == ownerUserId
                && a.Lifecycle == LifecycleStates.Released))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> HasSessionForTrackAsync(TrackId trackId, string userId, CancellationToken ct = default)
    {
        return await _context.RecordingSessions
            .AnyAsync(s => s.TrackId == trackId && s.UserId == userId, ct);
    }

    // Cap on points served for a FINALIZED live-share (the frozen final map for a late opener). Full
    // resolution is wasted on an overview map and this is the most bandwidth-sensitive, anonymous surface.
    private const int ShareFinalTrackMaxPoints = 5000;

    // Cap on points served on a MID-RIDE catch-up (afterSeq < 0 — a late opener pulling the whole staged
    // history of a still-active ride). Without it the full-resolution staging tail ships uncompressed on the
    // same anonymous surface: a long ride is tens of thousands of points (a measured 25k-point session ≈
    // 1.3 MB of JSON, held in ~3 server-side copies). Strided to the finalized branch's budget. Incremental
    // polls (afterSeq >= 0) return their small delta unstrided. perf-1.
    private const int ShareCatchUpMaxPoints = 5000;

    /// <inheritdoc />
    public async Task<List<ExternalGpsPoint>> GetLivePointsAsync(RecordingSession session, long afterSeq)
    {
        // While the session is still active (or stopped-but-not-yet-finalized) the staging tail is the live
        // source of truth — incremental after-seq exactly as the authenticated LiveView reads it.
        if (session.Status != RecordingSessionStatus.Finalized)
        {
            var live = await GetPointsAfterSeqAsync(session.SessionId, afterSeq);
            // A whole-history catch-up (afterSeq < 0) can be the entire ride — stride it to the overview
            // budget, the same shape the finalized branch serves. The true tail is always kept so the
            // client's seq cursor advances to the real head; its follow-up incremental polls then start
            // from there, not from a strided-off seq (which would re-ship the dropped tail at full res).
            return afterSeq < 0 ? StrideKeepingLast(live, ShareCatchUpMaxPoints) : live;
        }

        // Finalized: the tail is compacted into the blob, so every request — incremental or whole-history —
        // gets the frozen final track, downsampled. Incremental polls included, deliberately: a live
        // watcher whose stop+finalize landed inside one poll gap would otherwise permanently lose the final
        // stretch (the old `afterSeq >= 0 → []` froze its map at the last staged batch it saw). Seqs here
        // are synthetic blob indexes, not staging seqs — the client replaces, never seq-appends.
        var decoded = await _reader.GetTrackAsync(session.TrackId);
        var coords = TrackCoordinates.Downsample(decoded, ShareFinalTrackMaxPoints);
        var ts = session.StoppedAt ?? session.StartedAt;
        var points = new List<ExternalGpsPoint>(coords.Count);
        for (int i = 0; i < coords.Count; i++)
            points.Add(new ExternalGpsPoint { Lat = coords[i].Lat, Lng = coords[i].Lng, Seq = i, TimestampUtc = ts });
        return points;
    }

    // Stride a point list down to ~maxPoints, always retaining the last element. Same stride math as
    // TrackCoordinates.Downsample (Math.Max(1, count/maxPoints)); the extra keep-last guarantee matters here
    // because each returned point's Seq is the viewer's next-poll cursor — dropping the true tail would make
    // that cursor lag the real head. No-op when the list is already within budget.
    private static List<ExternalGpsPoint> StrideKeepingLast(List<ExternalGpsPoint> points, int maxPoints)
    {
        if (points.Count <= maxPoints) return points;
        int stride = Math.Max(1, points.Count / maxPoints);
        var result = new List<ExternalGpsPoint>(points.Count / stride + 2);
        for (int i = 0; i < points.Count; i++)
            if (i % stride == 0) result.Add(points[i]);
        // The last index survives the stride only when (Count-1) is a multiple of stride; else append it.
        if ((points.Count - 1) % stride != 0) result.Add(points[^1]);
        return result;
    }

    /// <inheritdoc />
    public async Task<List<ExternalGpsPoint>> GetPointsAfterSeqAsync(string sessionId, long afterSeq)
    {
        var session = await _context.RecordingSessions.FindAsync(sessionId);
        if (session is null) return [];

        // Live viewers poll only active sessions (points still staged). After finalize the staging tail is
        // gone (compacted to the blob) and this returns [] — accepted (the finalized track is served via
        // the normal GPS endpoints). Ordered by seq ≡ timestamp order for a recording.
        var staged = await _stagingStore.GetPointsAfterSeqAsync(session.TrackId, afterSeq);
        return staged.Select(s => new ExternalGpsPoint
        {
            Lat = s.Lat,
            Lng = s.Lng,
            Altitude = s.Altitude,
            Speed = s.Speed,
            Distance = s.Distance,
            Pressure = s.Pressure,
            Seq = s.Seq,
            // Rebuilt from the truncated epoch column — SECOND precision, unlike the millisecond stamps the
            // SignalR path echoes. Harmless now: it is display-only, and the cursor is Seq (LV-01).
            TimestampUtc = s.TsEpoch.HasValue ? DateTime.UnixEpoch.AddSeconds(s.TsEpoch.Value) : null,
        }).ToList();
    }

    // Last stored point in the compacted blob (via the reader's blob decode) — the deploy-spanning-session
    // seed for both the running distance and the previous point. Walks back to the last non-NaN Distance
    // (returning its coords + distance); if the whole Distance channel is NaN/absent, falls back to the last
    // valid-coordinate point with Distance = null → seeds prev for the boundary leg but cum stays 0 (as
    // today). Null only when the track has no blob / no coordinates yet.
    private async Task<ExternalGpsPoint?> LastBlobPointAsync(TrackId trackId)
    {
        var track = await _reader.GetTrackAsync(trackId);
        if (track is null || track.Count == 0) return null;
        if (!track.Distance.IsNull)
        {
            for (int i = track.Count - 1; i >= 0; i--)
            {
                double d = track.Distance[i];
                if (!double.IsNaN(d))
                    return new ExternalGpsPoint { Lat = track.Lat[i], Lng = track.Lng[i], Distance = d };
            }
        }
        for (int i = track.Count - 1; i >= 0; i--)
        {
            if (!double.IsNaN(track.Lat[i]) && !double.IsNaN(track.Lng[i]))
                return new ExternalGpsPoint { Lat = track.Lat[i], Lng = track.Lng[i], Distance = null };
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<RecordingSession?> AbandonSessionAsync(string sessionId, string userId)
    {
        var session = await _context.RecordingSessions.FindAsync(sessionId);

        if (session is null || session.UserId != userId)
            return null;

        // Already in a terminal state — nothing to do, return it as-is.
        if (session.Status is RecordingSessionStatus.Stopped or RecordingSessionStatus.Finalized
                           or RecordingSessionStatus.Abandoned)
            return session;

        session.Status = RecordingSessionStatus.Abandoned;
        session.StoppedAt = _clock.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Abandoned recording session {SessionId} (track_ID {TrackId}) for user {UserId}",
            sessionId, session.TrackId, userId);

        return session;
    }

    /// <inheritdoc />
    public async Task<bool> RetryFinalizeFailedAsync(string sessionId, string userId)
    {
        var session = await _context.RecordingSessions.FindAsync(sessionId);

        if (session is null || session.UserId != userId || session.Status != RecordingSessionStatus.FinalizeFailed)
            return false;

        // Re-arm for another finalize attempt: back to 'stopped' (+ fresh stopped_at) so both the caller's
        // immediate FinalizeAsync and the background cleanup treat it as a normal stuck-stopped session.
        session.Status = RecordingSessionStatus.Stopped;
        session.StoppedAt = _clock.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Re-armed finalize_failed session {SessionId} for retry", sessionId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkFinalizeFailedAsync(string sessionId, string userId)
    {
        var session = await _context.RecordingSessions.FindAsync(sessionId);

        if (session is null || session.UserId != userId)
            return false;

        session.Status = RecordingSessionStatus.FinalizeFailed;
        await _context.SaveChangesAsync();

        _logger.LogWarning("Parked session {SessionId} at finalize_failed (manual retry re-failed)", sessionId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAbandonedSessionAsync(string sessionId, string userId)
    {
        var session = await _context.RecordingSessions.FindAsync(sessionId);

        if (session is null || session.UserId != userId || session.Status != RecordingSessionStatus.Abandoned)
            return false;

        var trackId = session.TrackId;

        // Detach the tracked session first: the core drops its RecordingSession row via raw SQL, so if we
        // left it tracked EF would client-cascade-delete it again when the core removes the parent Activity
        // (double delete of an already-gone row → DbUpdateConcurrencyException).
        _context.Entry(session).State = EntityState.Detached;

        // Full clean via the shared delete core: drops the Activity shell + staged GPS + blob + bbox +
        // the RecordingSession row in one transaction. Fixes the old leak that removed only the session
        // row, orphaning the shell + staging. Ownership is already verified above, so the core is safe.
        var purged = await _command.DeleteActivityCoreAsync(trackId, userId);
        if (!purged)
        {
            // No Activity shell (already gone) — just drop the dangling session row directly.
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM RecordingSession WHERE session_id = {0}", sessionId);
        }

        _logger.LogInformation(
            "Deleted abandoned session {SessionId} (track_ID {TrackId}) for user {UserId}",
            sessionId, trackId, userId);

        return true;
    }

    /// <inheritdoc />
    public async Task<List<RecentSessionView>> GetRecentSessionsWithReviewAsync(string userId, int limit = 20)
    {
        // Session ⟕ Activity by track_ID — lifecycle/source live on Activity. LEFT JOIN so a session
        // still lists even if its shell was purged; a session normally has exactly one Activity.
        var now = _clock.GetUtcNow().UtcDateTime;
        var joined =
            from s in _context.RecordingSessions.Where(s => s.UserId == userId)
            join a in _context.Activities on s.TrackId equals a.TrackId into ga
            from a in ga.DefaultIfEmpty()
            select new RecentSessionView
            {
                SessionId     = s.SessionId,
                TrackId       = s.TrackId.Value,
                Status        = s.Status,
                ActivityType  = a != null ? (a.ActivityType ?? s.ActivityType) : s.ActivityType,
                StartedAt     = s.StartedAt,
                StoppedAt     = s.StoppedAt,
                DurationEndUtc = s.StoppedAt ?? now,
                PointCount    = s.PointCount,
                IsPending     = a != null && a.Source == ActivitySources.AutoRecording
                                && a.Lifecycle == LifecycleStates.InWork
                                && a.LifecycleReason == LifecycleReasons.AwaitingReview,
                IsTrashed     = a != null && a.Source == ActivitySources.AutoRecording && a.Lifecycle == LifecycleStates.Trashed,
                TotalDistance = a != null ? a.TotalDistance : null,
                TimeMoving    = a != null ? a.TimeMoving : null,
                TotalAscent   = a != null ? a.TotalAscent : null,
            };

        // Recent window + every pending row (so the limit never hides something awaiting review),
        // deduped by session, newest first.
        var recent  = await joined.OrderByDescending(v => v.StartedAt).Take(limit).ToListAsync();
        var pending = await joined.Where(v => v.IsPending).ToListAsync();

        return recent.Concat(pending)
            .GroupBy(v => v.SessionId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(v => v.StartedAt)
            .ToList();
    }
}
