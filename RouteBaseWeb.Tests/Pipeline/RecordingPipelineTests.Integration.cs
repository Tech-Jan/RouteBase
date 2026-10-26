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
    public async Task DeleteAbandonedSession_PurgesShellSessionAndStaging()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        await _recordingService.AbandonSessionAsync(session.SessionId, UserId);

        var deleted = await _recordingService.DeleteAbandonedSessionAsync(session.SessionId, UserId);

        Assert.True(deleted);
        // No orphan Activity shell, no dangling session row, no staged GPS (AsNoTracking → hits the DB,
        // not the change tracker, which still holds the pre-delete session entity).
        Assert.Equal(0, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == session.TrackId));
        Assert.Equal(0, await _context.RecordingSessions.AsNoTracking().CountAsync(s => s.SessionId == session.SessionId));
        Assert.Equal(0, await StagingCountAsync(session.TrackId));
    }

    [Fact]
    public async Task DeleteAbandonedSession_NonAbandoned_ReturnsFalse()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        // Still 'recording' — not abandoned, so delete is refused and nothing is purged.
        Assert.False(await _recordingService.DeleteAbandonedSessionAsync(session.SessionId, UserId));
        Assert.Equal(1, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == session.TrackId));
    }

    // ── Full pipeline ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullPipeline_ActivityAppearsWithStats_AfterFinalization()
    {
        // 1. Start
        var session = await _recordingService.StartSessionAsync(
            new StartRecordingRequest
            {
                ActivityType  = "Ride",
                TrackName     = "Pipeline Test Ride",
                Visibility    = "public",
                ClientVersion = "test-1.0",
            },
            UserId);
        Assert.NotNull(session);

        // 2. Append two batches; the second continues the first in space and time (lat0 + 5·step,
        //    startSecond: 5) so the 10-point track stays contiguous and timestamps stay monotonic.
        await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(MakeGpsPoints(5), seq: 1), UserId);
        await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(MakeGpsPoints(5, lat0: 47.0 + 5 * PointStepLat, startSecond: 5), seq: 2), UserId);

        // 3. Stop
        var stopped = await _recordingService.StopSessionAsync(session.SessionId, UserId);
        Assert.Equal("stopped", stopped!.Status);

        // 4. Finalize
        await _finalizationService.FinalizeAsync(session.SessionId);

        // 5. Verify Activity row has stats
        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.NotNull(activity);
        Assert.Equal("Ride", activity.ActivityType);
        Assert.Equal("Pipeline Test Ride", activity.TrackName);
        Assert.Equal("live-recording", activity.Source);
        Assert.True(activity.TotalDistance > 0, "Expected positive distance");
        Assert.True(activity.TimeTotal > 0, "Expected positive time_total");

        // 6. Points compacted into the track blob (staging deleted on finalize)
        var gpsCount = await BlobPointCountAsync(session.TrackId);
        Assert.Equal(10, gpsCount);
    }

    // ── ConceptRefactors S4: Activity field-ownership pins (CR4-01..04) ────────────

    [Fact]
    public async Task FinalizeAsync_WritesFullServerStats()
    {
        // P1 pin (S4): a live finalize authors the FULL server-derived stat set, and time_elapsed
        // mirrors time_total (RF-04 DECIDED: the timer stays wall-span). Must survive CR4-01.
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(MakeGpsPoints(5), seq: 1), UserId);
        await _recordingService.AppendPointsAsync(
            session.SessionId,
            MakeBatch(MakeGpsPoints(5, lat0: 47.0 + 5 * PointStepLat, alt0: 1050.0, startSecond: 5), seq: 2),
            UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var activity = await _context.Activities.AsNoTracking()
            .FirstAsync(a => a.TrackId == session.TrackId);
        Assert.True(activity.TotalDistance > 0);
        Assert.True(activity.TimeTotal > 0);
        Assert.Equal(activity.TimeTotal, activity.TimeElapsed);   // wall-span timer pin
        Assert.True(activity.TimeMoving > 0);
        Assert.True(activity.SpeedAvg > 0);
        Assert.True(activity.SpeedMax > 0);
        Assert.NotNull(activity.TotalAscent);
        Assert.NotNull(activity.TotalDescent);
    }

    [Fact]
    public async Task RefreshElevation_RecomputesAscentDescent()
    {
        // P2 pin (S4): the elevation columns are refresh-owned — a refresh recomputes ascent/descent
        // from the smoothed altitude profile, replacing the imported summary value.
        var summary = new ExternalActivitySummary
        {
            ExternalId = 424242,
            Name = "Imported Zigzag",
            ActivityType = "Run",
            Source = "gpx-import",
            ElevationGain = 99999,
        };
        var baseUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var points = Enumerable.Range(0, 12).Select(i => new ExternalGpsPoint
        {
            Lat = 47.0 + i * PointStepLat,
            Lng = 11.0,
            Altitude = 1000 + (i % 2) * 15.0,   // zig-zag, ≥ 10 m per step (clears the 2 m noise filter)
            Speed = 5,
            Distance = i * 4.5,
            TimestampUtc = baseUtc.AddSeconds(i),
        }).ToList();
        var imported = await _importService.ImportFromSummaryAsync(summary, points, UserId);
        Assert.NotNull(imported);

        await _finalizationService.RefreshElevationAsync(imported!.TrackId);

        var activity = await _context.Activities.AsNoTracking()
            .FirstAsync(a => a.TrackId == imported.TrackId);
        Assert.NotNull(activity.TotalAscent);
        Assert.NotEqual(99999.0, activity.TotalAscent!.Value);   // recomputed, not the import sentinel
        Assert.True(activity.TotalDescent >= 0);
    }

    [Fact]
    public async Task RefreshElevation_PreservesDeviceMotionScalars()
    {
        // N1 (CR4-01): an elevation refresh on an IMPORTED track recomputes ascent/descent only —
        // the device-authored motion scalars (wheel distance, timer times, avg/max speed) must survive.
        // Pre-fix, RefreshElevationAsync's shared 8-field UPDATE stomped them with GPS recomputes.
        var summary = new ExternalActivitySummary
        {
            ExternalId = 434343,
            Name = "Imported Device Stats",
            ActivityType = "Ride",
            Source = "fit-import",
            Distance = 99999,
            TotalTime = 1111,
            ElapsedTime = 2222,
            MovingTime = 900,
            AverageSpeed = 9.9,
            MaxSpeed = 19.9,
            ElevationGain = 55555,
            TotalDescent = 44444,
        };
        var baseUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var points = Enumerable.Range(0, 3).Select(i => new ExternalGpsPoint
        {
            Lat = 47.0 + i * PointStepLat,   // haversine ~9 m total, ≪ the device 99999 m
            Lng = 11.0,
            Altitude = 1000 + i * 15.0,
            Speed = 5,
            Distance = i * 9.0,
            TimestampUtc = baseUtc.AddSeconds(i),
        }).ToList();
        var imported = await _importService.ImportFromSummaryAsync(summary, points, UserId);
        Assert.NotNull(imported);

        await _finalizationService.RefreshElevationAsync(imported!.TrackId);

        var activity = await _context.Activities.AsNoTracking()
            .FirstAsync(a => a.TrackId == imported.TrackId);
        // Device-authored motion scalars unchanged (time_total = TotalTime per GI-02 import mapping).
        Assert.Equal(99999.0, activity.TotalDistance);
        Assert.Equal(1111.0, activity.TimeTotal);
        Assert.Equal(2222.0, activity.TimeElapsed);
        Assert.Equal(900.0, activity.TimeMoving);
        Assert.Equal(9.9, activity.SpeedAvg);
        Assert.Equal(19.9, activity.SpeedMax);
        // Elevation columns recomputed (refresh-owned) — the import sentinels are gone.
        Assert.NotNull(activity.TotalAscent);
        Assert.NotEqual(55555.0, activity.TotalAscent!.Value);
        Assert.NotNull(activity.TotalDescent);
        Assert.NotEqual(44444.0, activity.TotalDescent!.Value);
    }

    [Fact]
    public async Task FinalizeAsync_Adjudicator_DoesNotDiscardConfirmedAutoRide()
    {
        // N2 (CR4-02): the adjudicator's only legal transition is pending→discarded — a short auto
        // ride the user already confirmed must stay confirmed and its session must finalize normally
        // (pre-fix it was flipped back to discarded and routed to the 2-day trash purge).
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(autoDetected: true), UserId);
        Assert.NotNull(session);
        // Short ride: ~4 s moving / ~18 m — well under the adjudicator's thresholds.
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);
        // User confirms the pending row before finalize runs (e.g. while parked finalize_failed).
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity SET lifecycle = 'released', lifecycle_reason = 'user' WHERE track_ID = {0}",
            session.TrackId.Value);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var activity = await _context.Activities.AsNoTracking()
            .FirstAsync(a => a.TrackId == session.TrackId);
        Assert.Equal(LifecycleStates.Released, activity.Lifecycle);   // not stomped back to trashed
        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal("finalized", persisted.Status);              // not abandoned/trash-routed
    }

    [Fact]
    public async Task FinalizeAsync_Adjudicator_DiscardsShortPendingAutoRide()
    {
        // Positive companion to the N2 pin: a still-pending short auto ride IS discarded by the
        // adjudicator and its session routed into the 2-day trash window (session abandoned + stopped_at
        // stamped). Pins the pending→discarded + session-abandon pairing that B1-S3 moves into the
        // review owner, so the move is proven behavior-preserving.
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(autoDetected: true), UserId);
        Assert.NotNull(session);
        // ~4 s moving / ~18 m — well under the adjudicator's 800 m / 300 s floors.
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var activity = await _context.Activities.AsNoTracking()
            .FirstAsync(a => a.TrackId == session.TrackId);
        Assert.Equal(LifecycleStates.Trashed, activity.Lifecycle);
        var persisted = await _context.RecordingSessions.AsNoTracking()
            .FirstAsync(s => s.SessionId == session.SessionId);
        Assert.Equal("abandoned", persisted.Status);
        Assert.NotNull(persisted.StoppedAt);
    }

    [Fact]
    public async Task RefreshElevation_ActiveSession_Throws_StagingIntact()
    {
        // N3 (CR4-04 / EL-10): refresh while a non-terminal session owns the staging tail must be
        // refused — a compact-and-delete here would destroy points appended after the snapshot.
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(5)), UserId);
        Assert.Equal(5, await StagingCountAsync(session.TrackId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _finalizationService.RefreshElevationAsync(session.TrackId));

        Assert.Equal(5, await StagingCountAsync(session.TrackId));   // staged tail untouched
        Assert.Equal(0, await BlobRowCountAsync(session.TrackId));   // nothing compacted
    }
}
