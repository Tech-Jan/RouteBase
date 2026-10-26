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

namespace RouteBaseWeb.Tests.Pipeline;

public partial class RecordingPipelineTests
{
    // ── StartSession ────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartSession_CreatesActivityRow()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.NotNull(activity);
        Assert.Equal("Test Run", activity.TrackName);
        Assert.Equal("Run", activity.ActivityType);
        Assert.Equal("live-recording", activity.Source);
        Assert.Equal(UserId, activity.UserId);
        Assert.Equal("friends", activity.Visibility);
    }

    [Fact]
    public async Task StartSession_AutoDetected_NamesTheShellForWhatItIs()
    {
        // The phone's auto-detect start sends no name, so the request carries the manual default.
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(trackName: StartRecordingRequest.DefaultTrackName, autoDetected: true), UserId);
        Assert.NotNull(session);

        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.Equal(StartRecordingRequest.AutoDetectedTrackName, activity!.TrackName);
    }

    [Fact]
    public async Task StartSession_AutoDetected_KeepsAClientSuppliedName()
    {
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(trackName: "Commute", autoDetected: true), UserId);
        Assert.NotNull(session);

        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.Equal("Commute", activity!.TrackName);
    }

    [Fact]
    public async Task RetypeSession_UpdatesSessionAndActivity()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(activityType: "Walk"), UserId);
        Assert.NotNull(session);

        var retyped = await _recordingService.RetypeSessionAsync(session.SessionId, "Ride", UserId);

        Assert.NotNull(retyped);
        Assert.Equal("Ride", retyped.ActivityType);
        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.Equal("Ride", activity!.ActivityType);
    }

    [Fact]
    public async Task RetypeSession_WrongUser_ReturnsNull()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(activityType: "Walk"), UserId);
        Assert.NotNull(session);

        var result = await _recordingService.RetypeSessionAsync(session.SessionId, "Run", "someone-else");

        Assert.Null(result);
        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.Equal("Walk", activity!.ActivityType); // unchanged
    }

    [Fact]
    public async Task RetypeSession_BlankType_ReturnsNull()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(activityType: "Walk"), UserId);
        Assert.NotNull(session);

        var result = await _recordingService.RetypeSessionAsync(session.SessionId, "  ", UserId);

        Assert.Null(result);
    }

    [Fact]
    public async Task StartSession_CreatesRecordingSessionRow()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        var persisted = await _context.RecordingSessions.FindAsync(session.SessionId);
        Assert.NotNull(persisted);
        Assert.Equal("recording", persisted.Status);
        Assert.Equal(UserId, persisted.UserId);
        Assert.Equal(0, persisted.PointCount);
        Assert.Equal(-1, persisted.LastSequence);
    }

    // ── RS-02: transactional start + single-active invariant ─────────────────────

    [Fact]
    public async Task StartSession_SecondActiveForSameUser_ReturnsNull()
    {
        var first = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(first);

        // Second start while the first is active → null (not an exception), via the in-tx pre-check.
        var second = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.Null(second);

        var active = await _context.RecordingSessions.AsNoTracking()
            .CountAsync(s => s.UserId == UserId && (s.Status == "recording" || s.Status == "paused"));
        Assert.Equal(1, active);
    }

    [Fact]
    public async Task StartSession_UniqueActiveIndex_BlocksRawDuplicate()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session); // first active session for UserId

        // A second Activity shell + a raw active session row for the same user must violate the partial
        // unique index (proves it exists and enforces the invariant, error code 19 = SQLITE_CONSTRAINT).
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Activity (track_ID, start_datetime_utc, visibility) VALUES (99999, {0}, 'friends');",
            DateTime.UtcNow);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            _context.Database.ExecuteSqlRawAsync(
                "INSERT INTO RecordingSession (session_id, track_id, user_id, status, activity_type, started_at) " +
                "VALUES ('dup', 99999, {0}, 'recording', 'Run', {1});",
                UserId, DateTime.UtcNow));

        var sqlite = ex.GetBaseException() as Microsoft.Data.Sqlite.SqliteException;
        Assert.Equal(19, sqlite?.SqliteErrorCode);
    }

    // ── AppendPoints ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AppendPoints_InsertsGpsRows()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        var points = MakeGpsPoints(5);

        var inserted = await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(points), UserId);

        Assert.NotNull(inserted);
        Assert.Equal(5, inserted.Count);

        var dbCount = await StagingCountAsync(session.TrackId);
        Assert.Equal(5, dbCount);
    }

    [Fact]
    public async Task AppendPoints_UpdatesSessionMetadata()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        var points = MakeGpsPoints(3);

        await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(points, seq: 1), UserId);

        var persisted = await _context.RecordingSessions.FindAsync(session.SessionId);
        Assert.NotNull(persisted);
        Assert.Equal(3, persisted.PointCount);
        Assert.Equal(1, persisted.LastSequence);
        Assert.NotNull(persisted.LastPointAt);
    }

    [Fact]
    public async Task AppendPoints_DuplicateBatch_IsRejected()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        var points = MakeGpsPoints(3);
        var batch = MakeBatch(points, seq: 1);

        // First batch succeeds.
        var first = await _recordingService.AppendPointsAsync(
            session.SessionId, batch, UserId);
        Assert.NotNull(first);
        Assert.Equal(3, first.Count);

        // Same batch replayed — must be rejected (empty list, no new inserts).
        var dup = await _recordingService.AppendPointsAsync(
            session.SessionId, batch, UserId);
        Assert.NotNull(dup);

        Assert.Empty(dup);

        var dbCount = await StagingCountAsync(session.TrackId);
        Assert.Equal(3, dbCount); // still 3, not 6
    }

    [Fact]
    public async Task AppendPoints_WrongUser_ReturnsNull()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        var points = MakeGpsPoints(2);

        var result = await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(points), userId: "other-user");

        Assert.Null(result);
    }

    // ── RS-01: inter-batch cumulative-distance accumulation ─────────────────────

    [Fact]
    public async Task AppendPoints_FirstBatch_StartsAtZero()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(3)), UserId);

        var dists = await StagedDistancesAsync(session.TrackId);
        Assert.Equal(0.0, dists[0], 3);
    }

    [Fact]
    public async Task AppendPoints_SecondBatch_AccumulatesBoundaryLeg()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        // Batch 1: P0, P1 (uniform 4.5 m spacing). Batch 2 continues in space + time from P1.
        await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(MakeGpsPoints(2), seq: 1), UserId);
        await _recordingService.AppendPointsAsync(
            session.SessionId,
            MakeBatch(MakeGpsPoints(2, lat0: 47.0 + 2 * PointStepLat, startSecond: 2), seq: 2), UserId);

        var dists = await StagedDistancesAsync(session.TrackId);
        Assert.Equal(4, dists.Count);

        // One uniform leg length; the batch-boundary leg (P1→P2) must be counted (was dropped pre-fix).
        double leg = GeoUtils.HaversineDistance(47.0, 11.0, 47.0 + PointStepLat, 11.0);
        Assert.Equal(0.0, dists[0], 3);
        Assert.Equal(leg, dists[1], 1);
        Assert.Equal(2 * leg, dists[2], 1);   // boundary leg accumulated here
        Assert.Equal(3 * leg, dists[3], 1);
    }

    [Fact]
    public async Task FinalizeAsync_BlobDistanceChannel_MatchesComputedTotal()
    {
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);

        await _recordingService.AppendPointsAsync(
            session.SessionId, MakeBatch(MakeGpsPoints(5), seq: 1), UserId);
        await _recordingService.AppendPointsAsync(
            session.SessionId,
            MakeBatch(MakeGpsPoints(5, lat0: 47.0 + 5 * PointStepLat, startSecond: 5), seq: 2), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);
        await _finalizationService.FinalizeAsync(session.SessionId);

        var dists = await BlobDistancesAsync(session.TrackId);
        // Monotonically non-decreasing channel.
        for (int i = 1; i < dists.Count; i++)
            Assert.True(dists[i] >= dists[i - 1], $"distance channel decreased at {i}");

        var activity = await _context.Activities.FindAsync(session.TrackId);
        Assert.NotNull(activity?.TotalDistance);
        // Last per-point distance agrees with the aggregate total (both server-haversine over the same
        // cleaned points) within 1 m.
        Assert.True(Math.Abs(activity!.TotalDistance!.Value - dists[^1]) < 1.0,
            $"blob last distance {dists[^1]} vs total_distance {activity.TotalDistance}");
    }

    [Fact]
    public async Task RefreshElevation_PreservesImportedDistanceChannel()
    {
        // Import-style track carrying a device (non-haversine) Distance channel — RefreshElevationAsync
        // must NOT overwrite it with haversine (rebuildDistanceChannel defaults false for imports).
        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        var trackId = session.TrackId;

        var baseUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var points = Enumerable.Range(0, 3).Select(i => new ExternalGpsPoint
        {
            Lat = 47.0 + i * PointStepLat,
            Lng = 11.0,
            Altitude = 1000 + i * 10,
            Speed = 5,
            Distance = i switch { 0 => 0.0, 1 => 5000.0, _ => 12345.0 },  // artificial, ≫ haversine ~9 m
            TimestampUtc = baseUtc.AddSeconds(i),
        }).ToList();
        await _importService.WriteTrackBlobAsync(trackId, points);
        // EL-10 guard: refresh requires a terminal session — finalize the arrange-only session shell.
        session.Status = "finalized";
        await _context.SaveChangesAsync();

        await _finalizationService.RefreshElevationAsync(trackId);

        var dists = await BlobDistancesAsync(trackId);
        Assert.Equal(0.0, dists[0], 1);
        Assert.Equal(5000.0, dists[1], 1);     // preserved, not rebuilt to ~4.5 m
        Assert.Equal(12345.0, dists[2], 1);
    }

    [Fact]
    public async Task RefreshElevation_ReportsOutcome_NullOnEmptyTrack()
    {
        // EL-08: an empty/unknown track refreshes nothing → null; a real track reports counts
        // (the test elevation service is a no-op, so PointsReplaced is 0 — the "DEM yielded nothing" shape).
        Assert.Null(await _finalizationService.RefreshElevationAsync(new TrackId(999_999)));

        var session = await _recordingService.StartSessionAsync(MakeStartRequest(), UserId);
        Assert.NotNull(session);
        var trackId = session.TrackId;
        var baseUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var points = Enumerable.Range(0, 3).Select(i => new ExternalGpsPoint
        {
            Lat = 47.0 + i * PointStepLat,
            Lng = 11.0,
            Altitude = 1000,
            Speed = 5,
            Distance = i * 9.0,
            TimestampUtc = baseUtc.AddSeconds(i),
        }).ToList();
        await _importService.WriteTrackBlobAsync(trackId, points);
        // EL-10 guard: refresh requires a terminal session — finalize the arrange-only session shell.
        session.Status = "finalized";
        await _context.SaveChangesAsync();

        var result = await _finalizationService.RefreshElevationAsync(trackId);

        Assert.NotNull(result);
        Assert.Equal(3, result!.PointsTotal);
        Assert.Equal(0, result.PointsReplaced);
    }

    // ── RS-04: concurrent-append race ───────────────────────────────────────────

}
