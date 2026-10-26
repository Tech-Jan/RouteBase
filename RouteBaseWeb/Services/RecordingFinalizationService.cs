using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <inheritdoc />
public class RecordingFinalizationService : IRecordingFinalizationService, IElevationRefreshService
{
    private readonly RouteBaseDbContext _context;
    private readonly IActivityImportService _importService;
    private readonly IActivityCommandService _command;
    private readonly IElevationService _elevationService;
    private readonly IGpsNormalizationService _normalization;
    private readonly IActivityTimezoneResolver _timezoneResolver;
    private readonly ITrackReader _reader;
    private readonly TrackContentStore _content;
    private readonly RecordingStagingStore _stagingStore;
    private readonly IAutoReviewService _autoReview;
    private readonly UserCacheInvalidator _invalidator;
    private readonly ITrackDeriveQueue _derives;
    private readonly ILogger<RecordingFinalizationService> _logger;

    public RecordingFinalizationService(
        RouteBaseDbContext context,
        IActivityImportService importService,
        IActivityCommandService command,
        IElevationService elevationService,
        IGpsNormalizationService normalization,
        IActivityTimezoneResolver timezoneResolver,
        ITrackReader reader,
        TrackContentStore content,
        RecordingStagingStore stagingStore,
        IAutoReviewService autoReview,
        UserCacheInvalidator invalidator,
        ITrackDeriveQueue derives,
        ILogger<RecordingFinalizationService> logger)
    {
        _context = context;
        _importService = importService;
        _command = command;
        _elevationService = elevationService;
        _normalization = normalization;
        _timezoneResolver = timezoneResolver;
        _reader = reader;
        _content = content;
        _stagingStore = stagingStore;
        _autoReview = autoReview;
        _invalidator = invalidator;
        _derives = derives;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(string sessionId)
    {
        var session = await _context.RecordingSessions.FindAsync(sessionId);
        if (session is null)
        {
            _logger.LogWarning("FinalizeAsync called for unknown session {SessionId}", sessionId);
            return;
        }

        if (session.Status == RecordingSessionStatus.Finalized)
        {
            _logger.LogDebug("Session {SessionId} already finalized, skipping", sessionId);
            return;
        }

        // CONC-P2: the compaction delete is watermark-bounded, but a batch that passed the 'recording' gate
        // before StopSession committed can still land a staging row ABOVE that watermark during the slow,
        // out-of-transaction DEM step below — one the delete then leaves for ContentTrackReader to concat onto
        // the finalized activity on every read, forever (inflated distance / a visible backtrack). Holding a
        // lock across the external-I/O DEM step is out — it would serialise the phone's 5 s POST path, the same
        // trade the CONC-P1 read fix refused. So instead each attempt re-verifies INSIDE the delete transaction
        // (FinalizePointsAsync) that no row sits above the watermark it compacted; a racing arrival rolls the
        // attempt back and re-reads. It has settled by then — the session already left 'recording', so no NEW
        // append can start, and at most the one in-flight batch that beat the stop is outstanding — so the next
        // attempt compacts it. Bias mirrors CONC-P1: a duplicated tail is repairable, deleting uncompacted
        // points is not — so the final attempt proceeds regardless (see FinalizePointsAsync), leaving any
        // still-racing row uncompacted (repairable) rather than looping forever or risking data loss.
        const int finalizeAttempts = 3;
        for (int attempt = 0; attempt < finalizeAttempts; attempt++)
        {
            // Load the whole track (compacted blob + staged tail, concatenated by the reader in ordinal order)
            // together with the watermark that read actually merged.
            // ORDER BY seq ≡ old GPS_ID order; for a recording that is also timestamp order — do not re-sort.
            var (points, stagingWatermark) = await ReadTrackWithMergedWatermarkAsync(session.TrackId);

            if (points.Count == 0)
            {
                await FinalizeEmptySessionAsync(session, sessionId);
                return;
            }

            // DEM elevation for live recordings (GPX imports already carry device altitude). Applied
            // unconditionally so a live finalize matches RefreshElevationAsync exactly: DEM → smooth.
            // External read-only I/O — deliberately outside the write transaction below.
            await _elevationService.ApplyDemElevationAsync(points);

            // One transaction over the whole write phase: blob PutAsync, staging delete, stats/tz/adjudicator
            // UPDATEs and the session save all commit together or roll back together. Without it, a crash after
            // the blob write but before the staging delete left the tail in BOTH stores — the reader concat
            // then duplicated it on the cleanup-retry re-run, baking duplicated geometry into the blob. RF-01.
            await using var tx = await _context.Database.BeginTransactionAsync();

            GpsNormalizationStats stats;
            try
            {
                stats = await FinalizePointsAsync(
                    session.TrackId, points, StatsScope.FullFinalize, stagingWatermark,
                    trimTrailing: true, rebuildDistanceChannel: true,
                    failOnStagingRace: attempt < finalizeAttempts - 1);
            }
            catch (FinalizeStagingRacedException ex)
            {
                // A batch landed above the watermark during the DEM step. Roll back (await using disposes the
                // uncommitted tx, undoing the blob write too) and re-read — the arrival has settled now, so the
                // next attempt compacts it into the blob instead of stranding it in staging.
                _logger.LogWarning(
                    "Session {SessionId}: {Reason}; rolling back finalize attempt {Attempt} to re-read",
                    sessionId, ex.Message, attempt + 1);
                continue;
            }

            // Prefer the coordinate-based IANA lookup (DST-correct, works even if the client sent no
            // offset — e.g. the web recorder never does). Fall back to the client-supplied offset, then UTC.
            var resolvedOffset = stats.StartUtc.HasValue
                ? await _timezoneResolver.ResolveOffsetAsync(stats.StartLat, stats.StartLng, stats.StartUtc.Value, session.UserId)
                : null;
            var tzOffset = resolvedOffset
                ?? (session.TimezoneOffsetMinutes.HasValue ? TimeSpan.FromMinutes(session.TimezoneOffsetMinutes.Value) : TimeSpan.Zero);
            var startDateLocal = stats.StartUtc.HasValue ? stats.StartUtc.Value + tzOffset : (DateTime?)null;
            var tzHours = (float)tzOffset.TotalHours;

            // CS8604: EF Core explicitly handles null elements as SQL NULL in params object[].
#pragma warning disable CS8604
            await _context.Database.ExecuteSqlRawAsync(Sql.ActivityRecordingSql.UpdateStartLocationSql,
                stats.StartUtc,
                startDateLocal,
                tzHours,
                stats.StartLat,
                stats.StartLng,
                stats.EndLat,
                stats.EndLng,
                session.TrackId.Value);
#pragma warning restore CS8604

            // RF-10: PointCount was the raw append count; outlier-drop + trailing-stationary trim reduced the
            // stored point list, so sync it to what the blob actually holds (the Record row's point count).
            session.PointCount = points.Count;
            session.Status = RecordingSessionStatus.Finalized;
            await _context.SaveChangesAsync();

            // Auto-detect adjudicator (review owner): a too-short / too-tiny auto ride is almost certainly a
            // false trigger (walk to the car, phone jostle) → discarded so it never surfaces, and its session
            // routed into the abandoned-purge (2-day trash window) instead of lingering as a broken 'Done' entry
            // whose View link 404s. A manual recording (not source='auto-recording'), a genuine ride (above the
            // floors), and a user-confirmed row (CR4-02: pending→discarded only) are all left untouched.
            await _autoReview.AdjudicateAfterFinalizeAsync(session.TrackId, session.ActivityType);

            // Auto-approve (opt-out, AutoApproveRecordings): with the recorder's start/stop trusted, a
            // genuine auto ride goes straight into circulation rather than waiting in the review queue.
            // AFTER the adjudicator and inside the same transaction, so a ride it just discarded stays
            // discarded — the awaiting-review guard both statements share is what enforces that ordering.
            await _autoReview.AutoApproveAfterFinalizeAsync(session.TrackId, session.UserId);

            await tx.CommitAsync();

            // AS-01 + SA-08/SVC-06: finalize rewrote stats and geometry and moved the head, so cancel the owner's
            // change token to evict every viewer's cached slices/bulk-tracks/track-coords of the mid-ride state —
            // on EVERY finalize, not just the adjudicator's discard branch. After the commit, never inside the tx:
            // an in-tx eviction (the old adjudicator behaviour) would let a concurrent read re-cache pre-commit rows.
            _invalidator.InvalidateUser(session.UserId);

            // A recorded ride joins its segments the same way an imported one does, instead of waiting for the
            // user to run Analyze All. Post-commit like the eviction above, and for the same reason: the match
            // runs in its own scope and must not read pre-commit rows.
            await EnqueueSegmentMatchIfReleasedAsync(session.TrackId, session.UserId, DeriveReason.Recording);

            _logger.LogInformation(
                "Finalized session {SessionId}: {Points} points, {Distance:F0} m, {Ascent:F0} m ascent",
                sessionId, points.Count, stats.TotalDistanceMeters, stats.TotalAscentMeters);
            return;
        }
    }

    /// <summary>
    /// Finalize a session whose read produced no GPS points. A 0-point MANUAL live recording has no data
    /// worth keeping (RF-06) so it is hard-deleted; a 0-point AUTO recording is abandoned and routed into the
    /// trash-purge window (RF-01). Split out of <see cref="FinalizeAsync"/> so its retry loop stays readable.
    /// </summary>
    private async Task FinalizeEmptySessionAsync(RecordingSession session, string sessionId)
    {
        // RF-06: a 0-point MANUAL live recording has no data worth keeping. Its shell is inserted
        // lifecycle='released', so leaving it would sit NULL-stats in every activity list for up
        // to 7 days (until the abandoned purge). Delete it now — shell + session row + staging + blob —
        // mirroring the auto-recording discard below. (Auto shells are 'pending', hidden from lists, and
        // are kept as restorable trash instead; only the always-visible manual shell is a live-list ghost.)
        var isManualShell = await _context.Activities
            .AnyAsync(a => a.TrackId == session.TrackId && a.Source == ActivitySources.LiveRecording);
        if (isManualShell)
        {
            _logger.LogWarning(
                "Session {SessionId} has no GPS points (manual recording); deleting empty shell", sessionId);
            // Detach first: the core drops the RecordingSession row via raw SQL, so a tracked entity
            // would trigger an EF client-cascade double-delete (mirrors DeleteAbandonedSessionAsync).
            _context.Entry(session).State = EntityState.Detached;
            await _command.DeleteActivityCoreAsync(session.TrackId, session.UserId);
            return;
        }

        _logger.LogWarning("Session {SessionId} has no GPS points; marking abandoned", sessionId);

        // One transaction over all three writes: a crash mid-branch would otherwise leave a 0-point
        // auto row abandoned-but-still-'pending' (7-day instead of 2-day cleanup). RF-01.
        await using var zeroTx = await _context.Database.BeginTransactionAsync();
        session.Status = RecordingSessionStatus.Abandoned;
        await _context.SaveChangesAsync();

        // A 0-point auto-recording (indoor false start that never got a usable GPS fix) must not
        // linger as 'pending' in the review queue. The review owner discards it (its NULL stats fall
        // below every floor) and stamps stopped_at to route it into the 2-day trash-purge window —
        // the same pairing the short-ride branch uses. Session is already 'abandoned' above (0 points),
        // which the owner re-asserts idempotently.
        bool zeroDiscarded = await _autoReview.AdjudicateAfterFinalizeAsync(session.TrackId, session.ActivityType);
        await zeroTx.CommitAsync();
        // Evict AFTER commit (SA-08 / SVC-06): inside the window a concurrent read re-caches the
        // doomed pre-commit review-queue slice.
        if (zeroDiscarded) _invalidator.InvalidateUser(session.UserId);
    }

    /// <inheritdoc />
    public async Task<ElevationRefreshResult?> RefreshElevationAsync(TrackId trackId)
    {
        // EL-10: a non-terminal session still owns the staging tail — compact-and-delete here would
        // destroy points appended after the snapshot. Imports have no session row → null → proceed.
        var blockingStatus = await _context.RecordingSessions
            .Where(s => s.TrackId == trackId && s.Status != RecordingSessionStatus.Finalized
                                             && s.Status != RecordingSessionStatus.Abandoned)
            .Select(s => s.Status)
            .FirstOrDefaultAsync();
        if (blockingStatus is not null)
            throw new InvalidOperationException(
                $"Track {trackId} has a recording session in status '{blockingStatus}'; finish or discard it before refreshing elevation.");

        var (points, stagingWatermark) = await ReadTrackWithMergedWatermarkAsync(trackId);

        if (points.Count == 0)
        {
            _logger.LogWarning("RefreshElevationAsync called for track {TrackId} with no GPS points", trackId);
            return null;
        }

        int replaced = await _elevationService.ApplyDemElevationAsync(points);

        // Own transaction (not nested — FinalizeAsync's tx never reaches here) so the stats UPDATE, blob
        // PutAsync and staging delete this path performs commit or roll back atomically too. RF-01.
        await using var tx = await _context.Database.BeginTransactionAsync();
        await FinalizePointsAsync(trackId, points, StatsScope.ElevationOnly, stagingWatermark, applyAltitudeSmoothing: true);
        await tx.CommitAsync();

        // Altitude changed, so every effort's ascent computed from the old geometry is now wrong. Match-only,
        // not a full derive: lat/lng are untouched, so the bbox and the candidate-segment set are unchanged and
        // the per-(track, segment) re-match is enough to refresh the aggregates.
        var owner = await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == trackId).Select(a => a.UserId).FirstOrDefaultAsync();
        if (owner is not null)
            await EnqueueSegmentMatchIfReleasedAsync(trackId, owner, DeriveReason.ElevationRefresh);

        _logger.LogInformation(
            "Refreshed DEM elevation for track {TrackId}: {Replaced}/{Points} points replaced",
            trackId, replaced, points.Count);
        return new ElevationRefreshResult(points.Count, replaced);
    }

    /// <summary>
    /// Queue an incremental segment match for a track whose geometry just became final.
    ///
    /// Gated on lifecycle rather than on <c>Source</c>, because one predicate then covers all three cases: a
    /// manual live recording is 'released' from its first second and matches; an auto-detected ride is still
    /// 'in-work' awaiting review and must NOT match yet; and a ride the adjudicator just discarded is 'trashed'
    /// and must not either. The auto ones get their match at approve instead (<c>AutoReviewService</c>).
    ///
    /// Matching before the ride is in circulation would not merely be early — effort reads are lifecycle-gated
    /// through <c>VisibilityService.VisibleActivities</c>, but heatmap generation filters efforts by visible
    /// USER, not by lifecycle, so a pending or discarded ride would be baked into regenerated imagery.
    /// </summary>
    private async Task EnqueueSegmentMatchIfReleasedAsync(TrackId trackId, string userId, DeriveReason reason)
    {
        var lifecycle = await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == trackId).Select(a => a.Lifecycle).FirstOrDefaultAsync();
        if (lifecycle == LifecycleStates.Released)
            _derives.EnqueueMatchOnly([trackId.Value], userId, reason);
    }

    // Which Activity column set this pipeline run may author — the ownership matrix is the per-flow
    // method set on Services/Sql/ActivityStatsSql.cs. ElevationOnly leaves the motion scalars
    // at their finalize/import-time values even though the blob geometry is re-processed; the resulting
    // micro-skew (dropped-outlier legs on a refreshed live track) is an accepted residual (CR4-03).
    private enum StatsScope { FullFinalize, ElevationOnly }

    // Thrown inside the finalize write transaction when a batch landed in staging above the watermark this
    // attempt compacted (the CONC-P2 DEM-window race). Caught by FinalizeAsync's retry loop to roll back and
    // re-read; never escapes the service.
    private sealed class FinalizeStagingRacedException(string message) : Exception(message);

    /// <summary>
    /// Reads the track and the staging watermark as ONE consistent observation.
    ///
    /// CONC-P1: snapshotting the watermark BEFORE the read (the old shape) meant any batch landing between
    /// the snapshot and the reader's staging query was compacted into the blob AND left in staging, because
    /// the delete only reached the older watermark. `ContentTrackReader` merges a staged tail whenever one
    /// exists, so those rows were re-appended on EVERY later read — permanently duplicated tail geometry.
    /// Snapshotting AFTER the read is worse: it would delete rows the blob never received.
    ///
    /// So: snapshot, read, re-snapshot. Equal watermarks prove the read saw exactly the rows up to that seq,
    /// making the delete exact. They differ only if a batch landed mid-read — retry, since finalize runs
    /// after the session stopped and the tail is settling. If it never settles, keep the PRE-read watermark:
    /// a duplicated tail is repairable, deleting points the blob never got is not.
    ///
    /// Residual (pre-existing, unchanged by this): the reader single-flights by track id, so a coalesced
    /// read that began before the first snapshot could return a slightly older tail, which this equality
    /// cannot detect. Closing that needs the reader to report the seq it merged — parked, not guessed.
    /// </summary>
    private async Task<(List<GpsData> Points, long? StagingWatermarkSeq)> ReadTrackWithMergedWatermarkAsync(TrackId trackId)
    {
        const int attempts = 3;
        long? before = null;
        List<GpsData> points = new();

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            before = (await _stagingStore.GetLastPointAsync(trackId))?.Seq;
            points = (await _reader.GetTrackAsync(trackId))?.ToGpsDataList(trackId) ?? new List<GpsData>();
            var after = (await _stagingStore.GetLastPointAsync(trackId))?.Seq;
            if (before == after) return (points, before);

            _logger.LogDebug(
                "Track {TrackId}: staging moved {Before}->{After} during the finalize read; re-reading",
                trackId, before, after);
        }

        return (points, before);
    }

    /// <summary>
    /// Core finalization logic as in-memory array ops: altitude/speed smoothing, stat computation, the
    /// Activity row update, then compaction of the processed points into the Track_Blob store (the same
    /// <see cref="List{GpsData}"/> code runs on the same values, so smoothing/DEM results are bit-identical
    /// to before — only the persistence changes). The staged tail is dropped; owner-token cache eviction
    /// is the caller's job post-commit (FinalizeAsync; the refresh-elevation endpoint does its own AA-05
    /// invalidation), and the sha-keyed decode cache needs none.
    /// DEM elevation is NOT applied here — callers that need it must apply it before calling.
    /// </summary>
    private async Task<GpsNormalizationStats> FinalizePointsAsync(
        TrackId trackId, List<GpsData> points, StatsScope statsScope, long? stagingWatermarkSeq,
        bool applyAltitudeSmoothing = true, bool trimTrailing = false, bool rebuildDistanceChannel = false,
        bool failOnStagingRace = false)
    {
        // Drop lateral GPS teleports first, so distance/pace/altitude/the stored blob are all computed
        // on cleaned points. Re-finalizing an existing track therefore also scrubs historical spikes.
        _normalization.RejectPositionOutliers(points);

        if (applyAltitudeSmoothing)
            _normalization.SmoothAltitudes(points);

        GeoUtils.SmoothSpeeds(points);

        // Live recordings always over-run the actual activity: the phone can only conclude you stopped
        // by waiting out its idle watchdog, so every auto-recording ends with minutes of the rider
        // parked. Trim that dwell so time, distance and end coordinates reflect the arrival. Runs before
        // the distance rebuild below so the parked jitter never reaches the Distance channel or the
        // stats. Imports are left as-is.
        if (trimTrailing)
            _normalization.TrimTrailingDwell(points);

        // Live-recording only: recompute the per-point cumulative distance from the cleaned coordinates
        // (RS-01 part B) so the blob's Distance channel is self-consistent — no dropped batch-boundary legs,
        // no outlier legs left baked into survivors. Live distance was always server-haversine anyway, so
        // this is the same formula. Imports are NOT rebuilt (default false): device wheel/odometer distance
        // must not be overwritten by haversine.
        if (rebuildDistanceChannel)
        {
            double cum = 0; GpsData? prevPt = null;
            foreach (var pt in points)
            {
                if (pt.GpsLat.HasValue && pt.GpsLong.HasValue)
                {
                    if (prevPt is not null)
                        cum += GeoUtils.HaversineDistance(prevPt.GpsLat!.Value, prevPt.GpsLong!.Value,
                                                          pt.GpsLat.Value, pt.GpsLong.Value);
                    prevPt = pt;
                }
                pt.Distance = cum;
            }
        }

        var stats = _normalization.ComputeStats(points);

        if (statsScope == StatsScope.FullFinalize)
            await _context.Database.ExecuteSqlRawAsync(Sql.ActivityStatsSql.UpdateAllStatsSql,
                stats.TotalDistanceMeters, stats.TotalAscentMeters, stats.TotalDescentMeters,
                stats.TimeTotalSeconds, stats.TimeMovingSeconds, stats.SpeedAvgMs, stats.SpeedMaxMs, trackId.Value);
        else
            await _context.Database.ExecuteSqlRawAsync(Sql.ActivityStatsSql.UpdateElevationStatsSql,
                stats.TotalAscentMeters, stats.TotalDescentMeters, trackId.Value);

        // Compact the processed points into the track's current content; the staged tail is now redundant.
        // A first finalize appends seq 1 (no parent); a re-finalize (elevation refresh) appends the next
        // version with the old head as its parent — the previous points stay readable, nothing is overwritten.
        var track = DecodedTrackExtensions.FromGpsDataList(points);
        var encoded = TrackBlobCodec.Encode(track);
        await _content.AppendVersionAsync(
            trackId, encoded, track.Count,
            new TrackVersionStats(stats.TotalDistanceMeters, stats.TotalAscentMeters, stats.TimeTotalSeconds));

        // CONC-P2: before dropping the compacted rows, re-verify INSIDE this transaction that nothing landed
        // above the watermark we just wrote to the blob. A batch that passed the 'recording' gate before stop
        // committed can append during the out-of-tx DEM step (a re-finalize/elevation refresh has none — the
        // EL-10 guard blocks an active session, so this is a no-op there). Deleting only up to the watermark
        // would strand that later row for the reader to concat forever. GetLastPointAsync enlists this ambient
        // transaction, so it observes the racing row.
        var lastStaged = await _stagingStore.GetLastPointAsync(trackId);
        bool racedAboveWatermark = lastStaged.HasValue
            && (stagingWatermarkSeq is null || lastStaged.Value.Seq > stagingWatermarkSeq.Value);
        if (racedAboveWatermark)
        {
            long racedSeq = lastStaged!.Value.Seq;
            if (failOnStagingRace)
                // Roll the attempt back so a fresh read compacts the settled row (FinalizeAsync's retry loop).
                throw new FinalizeStagingRacedException(
                    $"staging seq {racedSeq} landed above the finalize watermark {stagingWatermarkSeq} during finalize");

            // Final attempt: fall back to the safe side. Leave the uncompacted row in staging — the reader
            // will concat it (a repairable duplication, fixable by a re-finalize) — rather than delete it (an
            // unrepairable loss of a captured point) or loop indefinitely. Near-unreachable: at most one
            // in-flight batch can outrun the stop, so the previous attempt has already settled it.
            _logger.LogWarning(
                "Track {TrackId}: staging seq {Seq} above the finalize watermark {Watermark} did not settle across retries; leaving it uncompacted (repairable) rather than deleting it",
                trackId, racedSeq, stagingWatermarkSeq);
        }

        await _stagingStore.DeleteUpToSeqAsync(trackId, stagingWatermarkSeq);

        // Content is authoritative now (min/max unchanged by smoothing/DEM — those touch altitude/speed only).
        await _importService.UpsertActivityBboxAsync(trackId);

        return stats;
    }
}
