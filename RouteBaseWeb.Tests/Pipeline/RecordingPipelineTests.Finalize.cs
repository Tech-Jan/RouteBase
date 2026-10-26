using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Pipeline;

public partial class RecordingPipelineTests
{
    [Fact]
    public async Task AppendPoints_ConcurrentDuplicateBatches_InsertOnce()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        var points = MakeGpsPoints(3);
        var svcA = NewRecordingServiceOnFreshContext();
        var svcB = NewRecordingServiceOnFreshContext();

        // Same ClientSequence from two contexts (client timeout-retry racing the original). The striped
        // lock serializes them; the second sees the committed LastSequence and dedups to [].
        var results = await Task.WhenAll(
            svcA.AppendPointsAsync(session.SessionId, MakeBatch(points, seq: 1), UserId),
            svcB.AppendPointsAsync(session.SessionId, MakeBatch(points, seq: 1), UserId));

        Assert.All(results, r => Assert.NotNull(r));
        Assert.Equal(3, await StagingCountAsync(session.TrackId)); // inserted once, no PK collision

        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal(3, persisted.PointCount);
    }

    [Fact]
    public async Task AppendPoints_ConcurrentDistinctBatches_BothInsertDenseSeq()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        var svcA = NewRecordingServiceOnFreshContext();
        var svcB = NewRecordingServiceOnFreshContext();

        // Distinct sequences racing. The lock guarantees each computes startSeq = current count, so the
        // staged seqs are always a dense collision-free 0..N-1 range regardless of which wins the lock
        // (dedup may correctly drop the lower seq if the higher lands first — hence count is 2 or 4, but
        // never a collided/gapped set).
        await Task.WhenAll(
            svcA.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(2), seq: 1), UserId),
            svcB.AppendPointsAsync(
                session.SessionId,
                MakeBatch(MakeGpsPoints(2, lat0: 47.0 + 2 * PointStepLat, startSecond: 2), seq: 2), UserId));

        var seqs = await StagedSeqsAsync(session.TrackId);
        Assert.True(seqs.Count is 2 or 4, $"expected 2 or 4 staged rows, got {seqs.Count}");
        Assert.Equal(Enumerable.Range(0, seqs.Count).Select(i => (long)i).ToList(), seqs);
    }

    [Fact]
    public async Task StagingNextSeq_EqualsCountWhileDense_AndContinuesPastPartialDelete()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        var staging = new RecordingStagingStore(_context);

        Assert.Equal(0, await staging.NextSeqAsync(session.TrackId)); // empty → first append starts at 0

        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        Assert.Equal(5, await staging.NextSeqAsync(session.TrackId));
        Assert.Equal(await staging.CountAsync(session.TrackId), await staging.NextSeqAsync(session.TrackId));

        // A partial compaction delete (SVC-02) can leave late arrivals above the watermark. The next
        // ordinal must continue past the survivors — a COUNT would restart inside their seq range.
        await staging.DeleteUpToSeqAsync(session.TrackId, 2);
        Assert.Equal(5, await staging.NextSeqAsync(session.TrackId));
    }

    // ── RS-05: atomic append (transactional staging + bbox + session save) ──────

    [Fact]
    public async Task AppendPoints_BboxFailure_RollsBackWholeBatch()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        // Fault-inject the bbox upsert (mid-append, after the staging INSERT). The transaction must roll
        // the staging rows back and leave LastSequence unadvanced so the client's retry re-inserts cleanly.
        var faulty = NewRecordingServiceOnFreshContext(new ThrowingBboxImportService());

        await Assert.ThrowsAnyAsync<Exception>(() =>
            faulty.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(3)), UserId));

        Assert.Equal(0, await StagingCountAsync(session.TrackId));
        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal(-1, persisted.LastSequence);
        Assert.Equal(0, persisted.PointCount);
    }

    // ── AS-01: finalize evicts the owner-token caches ───────────────────────────

    [Fact]
    public async Task Finalize_GenuineRide_InvalidatesOwnerCaches()
    {
        // A manual ride is 'released' and list-visible DURING the recording, so slices/bulk-track cache
        // entries can hold mid-ride geometry and NULL stats. Finalize rewrites both — it must cancel the
        // owner's change token (AS-01) or every viewer keeps the stale entries until their sliding TTL.
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        var token = _finalizeInvalidator.GetChangeToken(UserId);
        await _finalizationService.FinalizeAsync(session.SessionId);

        Assert.True(token.HasChanged);
    }

    // ── StopSession ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopSession_SetsStatusToStopped()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        var stopped = await _recordingService.StopSessionAsync(session.SessionId, UserId);

        Assert.NotNull(stopped);
        Assert.Equal("stopped", stopped.Status);
        Assert.NotNull(stopped.StoppedAt);
    }

    [Fact]
    public async Task StopSession_IsIdempotent()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        // Second call must not throw and must return the already-stopped session.
        var result = await _recordingService.StopSessionAsync(session.SessionId, UserId);
        Assert.NotNull(result);
        Assert.Equal("stopped", result.Status);
    }

    // ── Finalization ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FinalizeAsync_SetsStatusToFinalized()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var persisted = await _context.RecordingSessions.FindAsync(session.SessionId);
        Assert.Equal("finalized", persisted!.Status);
    }

    [Fact]
    public async Task FinalizeAsync_ComputesPositiveDistance()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        // 10 points, ~0.001° apart in lat → ~111 m each → ~999 m total
        await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(MakeGpsPoints(10)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.NotNull(activity?.TotalDistance);
        Assert.True(activity.TotalDistance > 0);
    }

    [Fact]
    public async Task FinalizeAsync_ComputesPositiveAscent()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        // Altitude rises 10 m per point → 5 points → 40 m gain (noise filter may suppress small deltas)
        await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(MakeGpsPoints(5, alt0: 1000.0)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.NotNull(activity?.TotalAscent);
        Assert.True(activity.TotalAscent > 0);
    }

    [Fact]
    public async Task FinalizeAsync_TrimsTrailingStationaryTail()
    {
        // A live recording that over-ran the activity: 2 min of riding, then the rider parked while the
        // phone kept recording until its idle watchdog fired. Finalize must trim the park so total time
        // reflects the arrival, not the sit (the file-8 inflated-total-time bug).
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        var moving = MakeGpsPoints(120);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(moving, seq: 1), UserId);

        // 4 min parked at the last moving position, continuing in time.
        var lastLat = moving[^1].Lat; var lastLng = moving[^1].Lng; var lastT = moving[^1].TimestampUtc;
        var tail = Enumerable.Range(0, 240).Select(i => new RecordingGpsPoint
        {
            Lat = lastLat, Lng = lastLng, Altitude = 1000.0, Speed = 0.0,
            TimestampUtc = lastT.AddSeconds(1 + i),
        }).ToList();
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(tail, seq: 2), UserId);

        await _recordingService.StopSessionAsync(session.SessionId, UserId);
        await _finalizationService.FinalizeAsync(session.SessionId);

        // Untrimmed would be 360 points / ~359 s total; trimmed keeps the ride plus a short transition,
        // so both are far smaller. Distance is unaffected (the tail added ~0 m).
        var stored = await BlobPointCountAsync(session.TrackId);
        Assert.True(stored < 150, $"expected trimmed tail, got {stored} points");

        // RF-10: session.PointCount is synced to the trimmed count the blob actually holds, not the raw
        // append count (70) — the Record row can no longer over-claim points.
        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal(stored, persisted.PointCount);

        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.NotNull(activity?.TimeTotal);
        Assert.True(activity!.TimeTotal < 150, $"expected trimmed total time, got {activity.TimeTotal}s");
        Assert.True(activity.TotalDistance > 0);
    }

    [Fact]
    public async Task FinalizeAsync_SetsStartAndEndCoords()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        var points = MakeGpsPoints(5, lat0: 47.0, lng0: 11.0);
        await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(points), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.NotNull(activity);
        Assert.InRange(activity.StartGpsLat!.Value, 46.9, 47.1);
        Assert.InRange(activity.StartGpsLong!.Value, 10.9, 11.1);
        Assert.InRange(activity.EndGpsLat!.Value, 46.9, 47.5);
    }

    [Fact]
    public async Task FinalizeAsync_NoPoints_AutoRecording_SetsStatusToAbandoned()
    {
        // Auto-detected false start: no fix ever captured. The auto branch keeps the session (abandoned,
        // routed to trash) — unlike a manual 0-point shell, which RF-06 deletes outright (test below).
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(autoDetected: true), UserId);
        Assert.NotNull(session);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal("abandoned", persisted.Status);
    }

    [Fact]
    public async Task FinalizeAsync_NoPoints_AutoRecording_IsDiscardedAndRoutedToTrash()
    {
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(autoDetected: true), UserId);
        Assert.NotNull(session);
        // Indoor false start: never captured a usable GPS fix.
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        // Session abandoned + stamped stopped_at → routes into the 2-day trash-purge window.
        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal("abandoned", persisted.Status);
        Assert.NotNull(persisted.StoppedAt);
        // Activity discarded so it never surfaces in the pending review queue.
        var activity = await _context.Activities.AsNoTracking()
            .FirstAsync(a => a.TrackId == session.TrackId);
        Assert.Equal(LifecycleStates.Trashed, activity.Lifecycle);
    }

    [Fact]
    public async Task FinalizeAsync_NoPoints_ManualRecording_ShellDeleted()
    {
        // RF-06: a 0-point manual recording has no data to keep and its 'confirmed' shell would sit
        // NULL-stats in every activity list for 7 days — so it is hard-deleted at finalize (shell +
        // session row gone), instead of the old plain-abandoned shell that lingered until the purge.
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(autoDetected: false), UserId);
        Assert.NotNull(session);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var activityGone = !await _context.Activities.AsNoTracking()
            .AnyAsync(a => a.TrackId == session.TrackId);
        var sessionGone = !await _context.RecordingSessions.AsNoTracking()
            .AnyAsync(s => s.SessionId == session.SessionId);
        Assert.True(activityGone, "manual 0-point Activity shell should be deleted");
        Assert.True(sessionGone, "manual 0-point session row should be deleted");
    }

    // ── RF-02: finalize_failed resolve transitions (Retry / Discard) ────────────────

    // Seeds a session parked at the terminal finalize_failed status.
    private async Task<RecordingSession> SeedFinalizeFailedAsync()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE RecordingSession SET status = 'finalize_failed' WHERE session_id = {0}", session!.SessionId);
        _context.ChangeTracker.Clear();
        return session;
    }

    [Fact]
    public async Task RetryFinalizeFailedAsync_FromFinalizeFailed_ReArmsToStopped()
    {
        var session = await SeedFinalizeFailedAsync();

        var ok = await _recordingService.RetryFinalizeFailedAsync(session.SessionId, UserId);

        Assert.True(ok);
        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal("stopped", persisted.Status);   // re-armed for the coordinator to pick up
        Assert.NotNull(persisted.StoppedAt);
    }

    [Fact]
    public async Task RetryFinalizeFailedAsync_WrongStatus_ReturnsFalse()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.StopSessionAsync(session!.SessionId, UserId);   // 'stopped', not finalize_failed

        Assert.False(await _recordingService.RetryFinalizeFailedAsync(session.SessionId, UserId));
    }

    [Fact]
    public async Task RetryFinalizeFailedAsync_WrongOwner_ReturnsFalse()
    {
        var session = await SeedFinalizeFailedAsync();
        Assert.False(await _recordingService.RetryFinalizeFailedAsync(session.SessionId, "someone-else"));
    }

    [Fact]
    public async Task MarkFinalizeFailedAsync_ParksSession()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.StopSessionAsync(session!.SessionId, UserId);

        var ok = await _recordingService.MarkFinalizeFailedAsync(session.SessionId, UserId);

        Assert.True(ok);
        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal("finalize_failed", persisted.Status);
    }

    [Fact]
    public async Task AbandonSessionAsync_FromFinalizeFailed_SetsAbandoned()
    {
        // Discard path: abandoning a finalize_failed session routes it to the 7-day purge (data kept).
        var session = await SeedFinalizeFailedAsync();

        var result = await _recordingService.AbandonSessionAsync(session.SessionId, UserId);

        Assert.NotNull(result);
        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal("abandoned", persisted.Status);
        Assert.NotNull(persisted.StoppedAt);
    }

    // ── RF-01: transactional finalize (rollback + idempotent retry) ─────────────────

    [Fact]
    public async Task FinalizeAsync_BboxFailure_RollsBackAllWrites()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        // The bbox upsert (last write in FinalizePointsAsync, after blob PutAsync + staging delete) throws.
        var faulty = NewFinalizationServiceWith(new ThrowingBboxImportService());
        await Assert.ThrowsAnyAsync<Exception>(() => faulty.FinalizeAsync(session.SessionId));

        // Everything the write phase touched rolled back together.
        Assert.Equal(5, await StagingCountAsync(session.TrackId));   // staged tail still present
        Assert.Equal(0, await BlobRowCountAsync(session.TrackId));   // no blob row
        var activity = await _context.Activities.AsNoTracking().FirstAsync(a => a.TrackId == session.TrackId);
        Assert.Null(activity.TotalDistance);                        // stats never persisted
        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal("stopped", persisted.Status);                  // not finalized → retried next cycle
    }

    [Fact]
    public async Task FinalizeAsync_RetryAfterFailure_ProducesCorrectPointCount()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        // First finalize fails and rolls back (staging intact, no blob).
        var faulty = NewFinalizationServiceWith(new ThrowingBboxImportService());
        await Assert.ThrowsAnyAsync<Exception>(() => faulty.FinalizeAsync(session.SessionId));

        // Retry with the working service: the tail is compacted exactly once — no duplicated seam.
        await _finalizationService.FinalizeAsync(session.SessionId);

        Assert.Equal(5, await BlobPointCountAsync(session.TrackId));  // not 10 (duplicated tail pre-fix)
        Assert.Equal(0, await StagingCountAsync(session.TrackId));    // staging compacted away
        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.NotNull(activity?.TotalDistance);
        var persisted = await _context.RecordingSessions.FindAsync(session.SessionId);
        Assert.Equal("finalized", persisted!.Status);
    }

    // ── CONC-P2: a batch landing during the out-of-tx DEM step must be compacted, not stranded ───

    [Fact]
    public async Task FinalizeAsync_LateAppendDuringDemFetch_IsCompactedNotStranded()
    {
        // Contract change (was FinalizeAsync_LateAppendDuringDemFetch_SurvivesToNextCycle, which pinned the
        // OLD behaviour: the late rows were left in staging and the session finalized anyway). A finalized
        // session has NO next cycle, so those stranded rows are the CONC-P2 corruption — ContentTrackReader
        // concats them onto the saved activity on every read, forever. The new contract: the late DEM-window
        // append is compacted THIS cycle (re-verify inside the delete tx rolls the attempt back; the re-read
        // picks the settled row up), so staging is drained and the activity reads back with no stray point.
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        // Two points "arrive" (via the elevation-fetch side effect) after FinalizeAsync has snapshotted
        // staging and read its point list — the exact CONC-P2 window, ABOVE the watermark. Fires once, like a
        // real in-flight batch that beat the stop.
        var lateGps = MakeGpsPoints(2, startSecond: 100)
            .Select(p => new ExternalGpsPoint { Lat = p.Lat, Lng = p.Lng, Altitude = p.Altitude, Speed = p.Speed, TimestampUtc = p.TimestampUtc })
            .ToList();
        var lateElevation = new LateAppendingElevationService(new RecordingStagingStore(_context), session.TrackId, lateGps);

        var finalizer = NewFinalizationServiceWithElevation(lateElevation);
        await finalizer.FinalizeAsync(session.SessionId);

        // Staging is drained — the delete reached exactly what the (re-)read compacted. This is the fix:
        // no row survives above the watermark to be concatenated forever.
        Assert.Equal(0, await StagingCountAsync(session.TrackId));

        // And the late batch really made it into the blob (> the 5 that existed at first-read time). A lower
        // bound on purpose: what finalize trims off the end is unrelated behaviour for this test to pin.
        var blob = await BlobPointCountAsync(session.TrackId);
        Assert.True(blob > 5, $"the late DEM-window batch must be compacted, not dropped — blob has {blob}");

        // The user-visible symptom this protects against: the saved activity must read back with no stray
        // point (pre-fix, the late rows sat in staging above the watermark, so every read re-added them).
        var readBack = await _reader.GetTrackAsync(session.TrackId);
        Assert.Equal(blob, readBack!.Count);

        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal("finalized", persisted.Status);
    }

    [Fact]
    public async Task FinalizeAsync_LateAppendThatNeverSettles_FallsBackToRepairableDuplication()
    {
        // The fallback side of the CONC-P2 bias. If a batch keeps landing above the watermark on every attempt
        // (pathological — real appends can't, the session has left 'recording'), the final attempt proceeds
        // rather than looping. The safe side must hold: the uncompacted rows are NOT deleted (a captured point
        // must never be lost), leaving a repairable duplication — fixable by a re-finalize — and the session
        // still finalizes.
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        var lateGps = MakeGpsPoints(1, lat0: 47.5, startSecond: 100)
            .Select(p => new ExternalGpsPoint { Lat = p.Lat, Lng = p.Lng, Altitude = p.Altitude, Speed = p.Speed, TimestampUtc = p.TimestampUtc })
            .ToList();
        // fireOnce:false → appends on EVERY DEM call, so the watermark can never catch up: the "cannot settle" case.
        var neverSettles = new LateAppendingElevationService(
            new RecordingStagingStore(_context), session.TrackId, lateGps, fireOnce: false);

        var finalizer = NewFinalizationServiceWithElevation(neverSettles);
        await finalizer.FinalizeAsync(session.SessionId); // must not throw or hang

        // Safe side held: the uncompacted late rows were left in staging, never deleted (no data loss).
        Assert.True(await StagingCountAsync(session.TrackId) > 0, "unsettled rows must be preserved, not destroyed");
        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal("finalized", persisted.Status);
    }

    // ── CONC-P1: a batch landing DURING the read must not end up in BOTH blob and staging ───

    [Fact]
    public async Task FinalizeAsync_AppendDuringTheTrackRead_IsCompactedExactlyOnce()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        // Two points land inside the read itself — later than the watermark snapshot, earlier than the
        // reader's staging query, so they ARE compacted. Pre-fix, the delete only reached the pre-read
        // watermark, so they stayed in staging too and every later read re-appended them.
        var lateGps = MakeGpsPoints(2, startSecond: 100)
            .Select(p => new ExternalGpsPoint { Lat = p.Lat, Lng = p.Lng, Altitude = p.Altitude, Speed = p.Speed, TimestampUtc = p.TimestampUtc })
            .ToList();
        var reader = new MidReadAppendingTrackReader(_reader, new RecordingStagingStore(_context), session.TrackId, lateGps);

        var finalizer = NewFinalizationServiceWithElevation(new NoOpElevationService(), reader);
        await finalizer.FinalizeAsync(session.SessionId);

        // Staging is drained — the delete reached exactly what the read compacted. This is the fix.
        Assert.Equal(0, await StagingCountAsync(session.TrackId));

        // And the mid-read batch really did make it into the blob (> the 5 that existed at snapshot time).
        // A lower bound on purpose: what finalize trims off the end is unrelated behaviour this test
        // must not pin.
        var blob = await BlobPointCountAsync(session.TrackId);
        Assert.True(blob > 5, $"the mid-read batch must be compacted, not dropped — blob has {blob}");

        // The user-visible symptom this protects against: the saved activity must not read back with a
        // duplicated tail (pre-fix, the late rows sat in BOTH the blob and staging, so every read re-added them).
        var readBack = await _reader.GetTrackAsync(session.TrackId);
        Assert.Equal(blob, readBack!.Count);
    }

    // ── Abandoned-session hygiene (delete-leak fix) ─────────────────────────────────

}
