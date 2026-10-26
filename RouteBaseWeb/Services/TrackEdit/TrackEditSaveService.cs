using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sql;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>Save/preflight result: an error (with its HTTP kind) or the success payload.</summary>
public sealed record SaveResult(TrackEditErrorKind? ErrorKind, TrackEditErrorBody? Error, SaveResponse? Response)
{
    public static SaveResult Fail(TrackEditErrorKind kind, TrackEditErrorBody body) => new(kind, body, null);
    public static SaveResult Ok(SaveResponse response) => new(null, null, response);
}

/// <summary>
/// The Replace / Save-as-copy pipeline (A2 §4 + §14.4). Two phases forced by the import/delete paths owning
/// their own transactions: phase 1 = one EF tx {session flip → commit row → per-source snapshot → blob put →
/// SYNC stats/bbox/start-time → consume → badge}; phase 2 = creations (each internally atomic) + created-id
/// backfill + derive enqueue. Invariant 1 (client claims + version stamps verified, replay counts checked)
/// and invariant 5 (snapshot before overwrite, same tx) hold — a mismatch aborts with no writes.
/// </summary>
public sealed class TrackEditSaveService
{
    private readonly EditSessionStore _sessions;
    private readonly TrackEditCommitStore _commits;
    private readonly TrackContentStore _content;
    private readonly RecordingStagingStore _staging;
    private readonly IActivityImportService _import;
    private readonly IActivityCommandService _command;
    private readonly IActivityTimezoneResolver _resolver;
    private readonly IGpsNormalizationService _normalization;
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;
    private readonly UserCacheInvalidator _invalidator;
    private readonly OperationQueue _queue;
    private readonly ILogger<TrackEditSaveService> _logger;

    private readonly TimeProvider _clock;

    public TrackEditSaveService(
        EditSessionStore sessions, TrackEditCommitStore commits,
        TrackContentStore content, RecordingStagingStore staging,
        IActivityImportService import, IActivityCommandService command, IActivityTimezoneResolver resolver,
        IGpsNormalizationService normalization, RouteBaseDbContext context, IVisibilityService visibility,
        UserCacheInvalidator invalidator, OperationQueue queue, ILogger<TrackEditSaveService> logger,
        TimeProvider clock)
    {
        _clock = clock;
        _logger = logger;
        _sessions = sessions;
        _commits = commits;
        _content = content;
        _staging = staging;
        _import = import;
        _command = command;
        _resolver = resolver;
        _normalization = normalization;
        _context = context;
        _visibility = visibility;
        _invalidator = invalidator;
        _queue = queue;
    }

    /// <summary>Pure dry-run of the mapping delegated to the session service in I2a; this service owns only
    /// the write path.</summary>
    public async Task<SaveResult> SaveAsync(
        string sessionId, string mode, IReadOnlyList<SlotCountClaim> claims, CancellationToken ct = default)
    {
        string userId = _visibility.GetCurrentUserId();

        // 1. Session owned + active; mode valid.
        var session = await _sessions.GetAsync(sessionId, ct);
        if (session is null || session.UserId != userId) return NotFound();
        if (session.Status != ActivityEditSessionStatuses.Active) return Conflict(TrackEditApiErrors.SessionNotActive);
        if (!TrackEditSaveModes.IsValid(mode)) return Bad("badMode");

        var effectiveOps = session.Script.Ops.Take(session.Cursor).ToList();
        // Only verify stamps for sources the effective script actually loads — an undone/truncated-away load's
        // stamp must not stale-block the save (R-04). Redo-tail sources stay stamped for resume (GetSourcesAsync).
        var effectiveLoadIds = effectiveOps.OfType<LoadActivityOp>().Select(o => o.ActivityId).ToHashSet();

        // 2. Per-source re-guard: still exists+owned+confirmed, no staging tail.
        foreach (var stamp in session.Stamps.Where(s => effectiveLoadIds.Contains(s.ActivityId)))
        {
            var lifecycle = await _visibility.OwnedActivities(_context.Activities)
                .Where(a => a.TrackId == new TrackId(stamp.ActivityId)).Select(a => a.Lifecycle).FirstOrDefaultAsync(ct);
            if (lifecycle != LifecycleStates.Released) return StaleTrack(stamp.ActivityId);
            if (await _staging.ExistsAsync(new TrackId(stamp.ActivityId), ct)) return StillRecording(stamp.ActivityId);
        }

        // 3. Stamp validation (invariant 1): re-hash + count-check the SAME bytes we then decode for replay.
        // (The pre-edit bytes no longer need keeping — the version they belong to still exists under its own
        // hash, so history needs no copy taken here.)
        var sources = new Dictionary<int, DecodedTrack>();
        foreach (var stamp in session.Stamps.Where(s => effectiveLoadIds.Contains(s.ActivityId)))
        {
            var bytes = await _content.GetHeadDataAsync(new TrackId(stamp.ActivityId), ct);
            if (bytes is null || bytes.Length < 8) return StaleTrack(stamp.ActivityId);
            if (TrackEditContracts.ComputeBlobSha256(bytes) != stamp.BlobSha256) return StaleTrack(stamp.ActivityId);
            if (TrackEditContracts.BlobHeaderPointCount(bytes) != stamp.PointCount) return StaleTrack(stamp.ActivityId);
            sources[stamp.ActivityId] = TrackBlobCodec.Decode(bytes);
        }

        // 4. Replay the effective script over the verified bytes.
        var effectiveScript = new EditScript(1, effectiveOps);
        var outcome = EditReplayEngine.ReplayScript(effectiveScript, sources);
        if (outcome.Error is not null)
            return SaveResult.Fail(TrackEditErrorKind.Unprocessable,
                new TrackEditErrorBody(outcome.Error.Code, OpIndex: outcome.Error.OpIndex));
        var slotById = outcome.Slots.ToDictionary(s => s.SlotId);

        // 5. Map slots → activities.
        var rootMeta = await RootMetaAsync(effectiveOps, ct);
        var names = rootMeta.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        var plan = SaveMapper.Map(effectiveOps, mode, names);
        if (plan.BlockedBy is not null) return Bad(plan.BlockedBy);

        // 6. Verify client claims cover exactly the live slots (invariant 1). A duplicate slotId is malformed
        // input that can never cover exactly the live slots → same replayMismatch bucket (not a 500 from ToDictionary).
        var claimBySlot = new Dictionary<int, int>();
        foreach (var c in claims)
            if (!claimBySlot.TryAdd(c.SlotId, c.PointCount)) return Conflict(TrackEditApiErrors.ReplayMismatch);
        if (claimBySlot.Count != slotById.Count ||
            slotById.Values.Any(s => !claimBySlot.TryGetValue(s.SlotId, out var pc) || pc != s.Track.Count))
            return Conflict(TrackEditApiErrors.ReplayMismatch);

        // 7. Encode replaced slots before opening the tx.
        var encoded = plan.Updated.ToDictionary(u => u.SlotId, u => TrackBlobCodec.Encode(slotById[u.SlotId].Track));

        var now = _clock.GetUtcNow().UtcDateTime;
        var replacedIds = plan.Updated.Select(u => u.ActivityId).ToList();
        var trashedIds = plan.Trashed.Select(t => t.ActivityId).ToList();

        // 8. Phase 1 — one transaction.
        CommitId commitId;
        await using (var tx = await _context.Database.BeginTransactionAsync(ct))
        {
            // Flip conditional on the cursor we read: an append committed while we hashed/replayed drifts the
            // cursor, so this finds 0 rows and we abort rather than dropping that edit with a 200 (R-02).
            if (!await _sessions.TrySetStatusAsync(sessionId, ActivityEditSessionStatuses.Active, ActivityEditSessionStatuses.Saved, now, expectedCursor: session.Cursor, ct: ct))
            {
                await tx.RollbackAsync(ct);
                var latest = await _sessions.GetAsync(sessionId, ct);
                return latest is { Status: ActivityEditSessionStatuses.Active }
                    ? Conflict(TrackEditApiErrors.CursorConflict)     // still active, cursor drifted under us
                    : Conflict(TrackEditApiErrors.SessionNotActive);  // saved/discarded/gone (double-save loser)
            }

            // CHECK IN: the ride is no longer checked out — the revision this save is about to append IS the
            // check-in. The session row stays (status 'saved'; the commit references it), so the pointer has to
            // be released explicitly rather than by the FK's delete cascade.
            await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.ReleaseEditSessionSql, [sessionId], ct);

            commitId = await _commits.InsertAsync(userId, now, mode, sessionId, effectiveScript, ct);

            foreach (var u in plan.Updated)
            {
                // Append the edited content as the track's NEW version and move its head. No snapshot step:
                // the version this one supersedes is still there, under its own hash — that IS the history.
                // Stamping commitId on the version is also what makes "what did this commit replace?" a query.
                var track = slotById[u.SlotId].Track;
                var stats = await TrackEditScalarStats.WriteAsync(
                    _context, _normalization, _resolver, new TrackId(u.ActivityId), track, userId, ct);
                await _content.AppendVersionAsync(
                    new TrackId(u.ActivityId), encoded[u.SlotId], track.Count,
                    new TrackVersionStats(stats.TotalDistanceMeters, stats.TotalAscentMeters, stats.TimeTotalSeconds),
                    commitId, ct: ct);
            }

            foreach (int id in trashedIds)
            {
                // The guarded UPDATE is the transition; the log row is its audit twin (same tx, only when the
                // guard actually moved a row) — merge-consume must answer "why did my ride vanish?" like every
                // other transition (ActivityLifecycleSql).
                if (await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.ConsumeSourceSql, commitId.Value, id, userId) == 1)
                    await _context.Database.ExecuteSqlRawAsync(ActivityLifecycleSql.InsertLogSql,
                        id, userId, LifecycleStates.Released, LifecycleStates.Trashed, LifecycleReasons.EditMerge, commitId.Value);
            }
            foreach (var u in plan.Updated)
                await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.SetEditedCommitSql, commitId.Value, u.ActivityId, userId);

            await tx.CommitAsync(ct);
        }

        // 9. Post-tx cache eviction. The track decode cache needs none — it is keyed by content hash and the
        // edit wrote a NEW hash, so no entry can be stale (the old TE-05 invalidation seam is gone).
        _invalidator.InvalidateUser(userId);

        // 10. Phase 2 — creations (each internally atomic). Each one stamps this commit onto its own first
        // version as it lands (D-A2-4), so a partial phase-2 crash leaves exactly what was created on record —
        // no end-of-loop bookkeeping write that a crash could skip. A creation that THROWS is caught, not
        // propagated: phase 1 is committed (sources trashed, commit durable), so a bare 500 would hide a save
        // that half-landed — the slot is reported instead, and reverting the commit restores its sources.
        var createdIds = new List<int>();
        var createdSlots = new List<SavedSlot>();
        var failedSlots = new List<FailedSlot>();
        var trashedSet = trashedIds.ToHashSet();
        foreach (var create in plan.Created.OrderBy(c => c.SlotId))
        {
            try
            {
                var track = slotById[create.SlotId].Track;
                var meta = rootMeta.GetValueOrDefault(create.RootActivityId);

                // Lineage, read now: the parents' heads are whatever they are AFTER phase 1, so a split-off of a
                // cropped track descends from the cropped version, and a merge's inputs (only trashed, never
                // re-versioned) contribute the heads they already had.
                var parentVersionIds = new List<VersionId>();
                foreach (int parentId in create.Parents)
                    if (await _content.GetHeadVersionIdAsync(new TrackId(parentId), ct) is { } v)
                        parentVersionIds.Add(v);

                var summary = await BuildSummaryAsync(create, track, meta, userId, ct);
                var newActivity = await _import.ImportFromSummaryAsync(summary, TrackEditConverters.ToExternalPoints(track), userId);
                if (newActivity is null)
                {
                    failedSlots.Add(new FailedSlot(create.SlotId, create.Name));
                    continue;
                }
                var newId = newActivity.TrackId;
                await _command.UpdateActivityAsync(newId, null, null, meta?.Visibility);         // visibility inherit + cache evict
                await _content.StampCreatingCommitAsync(newId, commitId, ct);
                await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.SetEditedCommitSql, commitId.Value, newId.Value, userId);

                // 0 parents = an import · 1 = a split/copy · N = a merge of N rides. This is the DAG the version
                // history reads its provenance from, and the only record of what a merged ride is made of.
                if (await _content.GetHeadVersionIdAsync(new TrackId(newId.Value), ct) is { } newVersionId)
                    await _content.AddParentsAsync(newVersionId, parentVersionIds, ct);

                // Option B: the rides this creation swallowed are already trashed (phase 1) — point them at it, and
                // bring their photos across. Both only apply to parents that were actually consumed, so a Copy
                // (which trashes nothing) moves nothing.
                foreach (int parentId in create.Parents.Where(trashedSet.Contains))
                {
                    await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.SetSupersededBySql, newId.Value, parentId, userId);
                    await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.MovePhotosToMergedSql, newId.Value, parentId, userId);
                }

                createdIds.Add(newId.Value);
                createdSlots.Add(new SavedSlot(create.SlotId, newId.Value, create.Name, Created: true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Save commit {CommitId}: creating slot {SlotId} ('{Name}') failed — phase 1 is committed; " +
                    "reverting the commit restores its trashed sources", commitId, create.SlotId, create.Name);
                failedSlots.Add(new FailedSlot(create.SlotId, create.Name));
            }
        }

        // 11. Enqueue derive (best-effort; the commit is durable regardless).
        bool deriveQueued = _queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type = OperationQueue.OperationType.TrackEditDerive,
            UserId = userId,
            Payload = new TrackEditDerivePayload(userId, commitId.Value, replacedIds, createdIds, trashedIds),
        });

        var slots = plan.Updated
            .Select(u => new SavedSlot(u.SlotId, u.ActivityId, u.Name, Created: false))
            .Concat(createdSlots)
            .OrderBy(s => s.SlotId)
            .ToList();
        return SaveResult.Ok(new SaveResponse(
            commitId.Value, slots, deriveQueued, failedSlots.Count == 0 ? null : failedSlots));
    }

    // ---- created-summary builder (§4.5) ----

    private async Task<ExternalActivitySummary> BuildSummaryAsync(
        MappedCreate create, DecodedTrack track, RootActivityMeta? meta, string userId, CancellationToken ct)
    {
        // The activity does not exist yet (this summary describes a pending creation), so there is no id
        // to stamp; ComputeStats reads channels only.
        var stats = _normalization.ComputeStats(track.ToGpsDataList(trackId: new TrackId(0)));
        var sensor = TrackEditConverters.ComputeSensorStats(track);
        var offset = await TrackEditScalarStats.ResolveOffsetAsync(_resolver, stats, userId, meta?.DateTimeTimezone);
        return new ExternalActivitySummary
        {
            ExternalId = 0,
            Source = "track-edit",
            Name = create.Name,
            ActivityType = meta?.ActivityType,
            StartDateUtc = stats.StartUtc,
            StartDateLocal = stats.StartUtc.HasValue ? stats.StartUtc.Value + offset : null,
            TimezoneOffset = (float)offset.TotalHours,
            StartLat = stats.StartLat,
            StartLng = stats.StartLng,
            EndLat = stats.EndLat,
            EndLng = stats.EndLng,
            Distance = stats.TotalDistanceMeters,
            TotalTime = stats.TimeTotalSeconds,
            MovingTime = stats.TimeMovingSeconds,
            ElapsedTime = stats.TimeTotalSeconds,
            ElevationGain = stats.TotalAscentMeters,
            TotalDescent = stats.TotalDescentMeters,
            AverageSpeed = stats.SpeedAvgMs,
            MaxSpeed = stats.SpeedMaxMs,
            AverageHeartRate = sensor.HrAvg,
            MaxHeartRate = sensor.HrMax,
            AverageCadence = sensor.CadAvg,
            MaxCadence = sensor.CadMax,
            AveragePower = sensor.PowAvg,
            MaxPower = sensor.PowMax,
            Calories = null,
            Description = null,
        };
    }

    private sealed record RootActivityMeta(string? Name, string? ActivityType, string Visibility, float? DateTimeTimezone);

    private async Task<IReadOnlyDictionary<int, RootActivityMeta>> RootMetaAsync(
        IReadOnlyList<EditOp> ops, CancellationToken ct)
    {
        var rootIds = ops.OfType<LoadActivityOp>().Select(o => o.ActivityId).Distinct().ToList();
        if (rootIds.Count == 0) return new Dictionary<int, RootActivityMeta>();
        var rootTrackIds = rootIds.Select(i => new TrackId(i)).ToList();
        return await _context.Activities.AsNoTracking()
            .Where(a => rootTrackIds.Contains(a.TrackId))
            .ToDictionaryAsync(
                a => a.TrackId.Value,
                a => new RootActivityMeta(a.TrackName, a.ActivityType, a.Visibility, a.DateTimeTimezone), ct);
    }

    private static SaveResult NotFound() => new(TrackEditErrorKind.NotFound, null, null);
    private static SaveResult Conflict(string code) => SaveResult.Fail(TrackEditErrorKind.Conflict, new TrackEditErrorBody(code));
    private static SaveResult Bad(string code) => SaveResult.Fail(TrackEditErrorKind.BadRequest, new TrackEditErrorBody(code));
    private static SaveResult StaleTrack(int activityId) =>
        SaveResult.Fail(TrackEditErrorKind.Conflict, new TrackEditErrorBody(TrackEditApiErrors.StaleTrack, ActivityId: activityId));
    private static SaveResult StillRecording(int activityId) =>
        SaveResult.Fail(TrackEditErrorKind.Conflict, new TrackEditErrorBody(TrackEditApiErrors.StillRecording, ActivityId: activityId));
}
