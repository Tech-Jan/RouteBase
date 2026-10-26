using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins the background auto-purge (<see cref="RecordingSessionCleanupService.PurgeAbandonedOlderThanAsync"/>):
/// abandoned sessions older than the cutoff are hard-purged (session + Activity shell + blob), fresh ones
/// are left intact.
/// </summary>
public class RecordingSessionPurgeTests : IAsyncLifetime
{
    private const string Me = "me";
    private string _connStr = null!;
    private RouteBaseDbContext _context = null!;
    private ActivityCommandService _command = null!;

    public async Task InitializeAsync()
    {
        _connStr = $"Data Source=file:purge_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_connStr).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schema = new DbSchemaManager(_context);
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureRecordingStagingTableAsync();
        await schema.EnsureActivityBboxTableAsync();
        await schema.EnsureTrackEditTablesAsync(); // ActivityCommandService's child-table delete seam touches Track_Blob_History

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = Me, Email = "me@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        var vis   = new TestVisibilityService(Me);
        var inval = new UserCacheInvalidator();
        _command = new ActivityCommandService(
            _context, vis, inval, NullLogger<ActivityCommandService>.Instance,
            TestLifecycle.For(_context, vis, inval));
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    // Calls the purge with a real staging store over the test context. `finalization` defaults to a
    // coordinator whose finalize path throws — the auto-source / no-staging cases must never reach it.
    private Task<int> PurgeAsync(
        IActivityCommandService command, DateTime abandonedCutoff, DateTime trashedCutoff,
        RecordingFinalizationCoordinator? finalization = null)
        => RecordingSessionCleanupService.PurgeAbandonedOlderThanAsync(
            _context, command, new RecordingStagingStore(_context),
            finalization ?? CoordinatorOver(new NeverFinalize()), NullLogger.Instance,
            abandonedCutoff, trashedCutoff, default);

    // Coordinator whose scope resolves the given finalization stub (mirrors RecordingSessionCleanupRetryTests).
    private static RecordingFinalizationCoordinator CoordinatorOver(IRecordingFinalizationService svc)
    {
        var provider = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddScoped(_ => svc)
            .BuildServiceProvider();
        return new RecordingFinalizationCoordinator(
            new SingleFlight(), provider.GetRequiredService<IServiceScopeFactory>());
    }

    private async Task SeedAbandonedAsync(
        string sessionId, TrackId trackId, DateTime stoppedAt,
        string lifecycle = LifecycleStates.Released, string? reason = null)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = Me, ActivityType = "Ride",
            Source = "auto-recording", Lifecycle = lifecycle, LifecycleReason = reason,
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = sessionId, TrackId = trackId, UserId = Me,
            Status = "abandoned", StartedAt = stoppedAt.AddHours(-1), StoppedAt = stoppedAt,
        });
        await _context.SaveChangesAsync();
        await TrackSeed.SeedTrackAsync(_context, trackId, new[]
        {
            (47.0, 11.0, new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
        });
    }

    private async Task<int> BlobCountAsync(TrackId trackId) =>
        (await _context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM Activity_Version WHERE track_id = {0} AND is_head = 1", trackId.Value).ToListAsync()).First();

    [Fact]
    public async Task Purge_RemovesOldAbandoned_LeavesFresh()
    {
        var now = DateTime.UtcNow;
        await SeedAbandonedAsync("old", new TrackId(1), now.AddDays(-8));   // older than 7d cutoff
        await SeedAbandonedAsync("fresh", new TrackId(2), now.AddDays(-1)); // within window

        // Production runs the purge on a fresh scope/context that has tracked nothing; drop the seed's
        // tracked entities so the assert path mirrors that (a tracked session would EF-cascade double-delete).
        _context.ChangeTracker.Clear();

        var purged = await PurgeAsync(_command, now - TimeSpan.FromDays(7), now - TimeSpan.FromDays(2));

        Assert.Equal(1, purged);
        // Old fully purged.
        Assert.Equal(0, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(1)));
        Assert.Equal(0, await _context.RecordingSessions.AsNoTracking().CountAsync(s => s.SessionId == "old"));
        Assert.Equal(0, await BlobCountAsync(new TrackId(1)));
        // Fresh untouched.
        Assert.Equal(1, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(2)));
        Assert.Equal(1, await _context.RecordingSessions.AsNoTracking().CountAsync(s => s.SessionId == "fresh"));
        Assert.Equal(1, await BlobCountAsync(new TrackId(2)));
    }

    [Fact]
    public async Task Purge_IgnoresNonAbandoned()
    {
        _context.Activities.Add(new Activity { TrackId = new TrackId(3), UserId = Me, ActivityType = "Run" });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = "finalized", TrackId = new TrackId(3), UserId = Me,
            Status = "finalized", StartedAt = DateTime.UtcNow.AddDays(-30), StoppedAt = DateTime.UtcNow.AddDays(-30),
        });
        await _context.SaveChangesAsync();

        var purged = await PurgeAsync(_command, DateTime.UtcNow, DateTime.UtcNow);

        Assert.Equal(0, purged);
        Assert.Equal(1, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(3)));
    }

    [Fact]
    public async Task Purge_SkipsSessionRestoredAfterSnapshot()
    {
        var now = DateTime.UtcNow;
        // Two purge candidates past the 7-day cutoff. During the delete of the first, a Restore lands on
        // the second (flips its session to 'finalized') — the per-candidate recheck must skip it.
        await SeedAbandonedAsync("purge-a", new TrackId(20), now.AddDays(-8));
        await SeedAbandonedAsync("purge-b", new TrackId(21), now.AddDays(-8));
        _context.ChangeTracker.Clear();

        // Decorator: on the first core delete (track 20), flip session purge-b to 'finalized' (a restore
        // landing mid-cycle) before the loop reaches its recheck.
        var flipping = new FlipOnFirstDeleteCommand(_command, _context, "purge-b");

        var purged = await PurgeAsync(flipping, now - TimeSpan.FromDays(7), now - TimeSpan.FromDays(2));

        Assert.Equal(1, purged);   // only track 20; track 21 skipped by the recheck
        Assert.Equal(0, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(20)));
        Assert.Equal(1, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(21)));
        Assert.Equal(1, await _context.RecordingSessions.AsNoTracking().CountAsync(s => s.SessionId == "purge-b"));
    }

    [Fact]
    public async Task Purge_SessionRowGoneBeforeActivityCoreDelete()
    {
        // SVC-05: the old code re-checked "still abandoned?" then, several round-trips later, ran
        // DeleteActivityCoreAsync — a Restore landing in that gap still got hard-purged. The fix folds
        // the check into the delete itself (one atomic statement) so nothing can land between them; this
        // pins the ordering that makes that true — by the time the (multi-step) Activity-core delete
        // even starts, the session row this purge is entitled to must already be gone.
        var now = DateTime.UtcNow;
        await SeedAbandonedAsync("solo", new TrackId(30), now.AddDays(-8));
        _context.ChangeTracker.Clear();

        var probe = new RecordingSessionGoneProbeCommand(_command, _context, "solo");

        var purged = await PurgeAsync(probe, now - TimeSpan.FromDays(7), now - TimeSpan.FromDays(2));

        Assert.Equal(1, purged);
        Assert.True(probe.SessionRowWasGoneBeforeActivityDelete);
    }

    [Fact]
    public async Task Reconciler_PurgesStrandedGhostShell()
    {
        // A3: purge deleted the session row, then died before the Activity-core delete — a released
        // NULL-stats shell with no session row is stranded (the sweep iterates sessions, never sees it).
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(40), UserId = Me, ActivityType = "Ride",
            Source = "live-recording", Lifecycle = LifecycleStates.Released,
            LifecycleReason = LifecycleReasons.Recording,
        });
        await _context.SaveChangesAsync();
        await TrackSeed.SeedTrackAsync(_context, new TrackId(40), new[]
        {
            (47.0, 11.0, new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
        });
        _context.ChangeTracker.Clear();

        var purged = await RecordingSessionCleanupService.ReconcileGhostShellsAsync(
            _context, _command, NullLogger.Instance, default);

        Assert.Equal(1, purged);
        Assert.Equal(0, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(40)));
        Assert.Equal(0, await BlobCountAsync(new TrackId(40)));
    }

    [Fact]
    public async Task Reconciler_PurgesStrandedTrashedShell_FromAnInterruptedTrashPurge()
    {
        // SVC-15: the 2-day trash purge also deletes session-first; a crash between the deletes strands a
        // TRASHED review-rejected shell (finalized — stats set) with no session. Restore refuses a
        // session-less row, so completing the purge is the only path that ever reclaims it.
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(45), UserId = Me, ActivityType = "Ride",
            Source = "auto-recording", Lifecycle = LifecycleStates.Trashed,
            LifecycleReason = LifecycleReasons.ReviewRejected, TotalDistance = 8.4,
        });
        await _context.SaveChangesAsync();
        await TrackSeed.SeedTrackAsync(_context, new TrackId(45), new[]
        {
            (47.0, 11.0, new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
        });
        _context.ChangeTracker.Clear();

        var purged = await RecordingSessionCleanupService.ReconcileGhostShellsAsync(
            _context, _command, NullLogger.Instance, default);

        Assert.Equal(1, purged);
        Assert.Equal(0, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(45)));
    }

    [Fact]
    public async Task Reconciler_LeavesARestorableTrashedRide_WithItsSession()
    {
        // A trashed review-rejected ride whose session still exists is the normal, restorable state the
        // 2-day window serves — the reconciler must never touch it.
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(46), UserId = Me, ActivityType = "Ride",
            Source = "auto-recording", Lifecycle = LifecycleStates.Trashed,
            LifecycleReason = LifecycleReasons.ReviewRejected, TotalDistance = 8.4,
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = "trash-sess", TrackId = new TrackId(46), UserId = Me,
            Status = "abandoned", StartedAt = DateTime.UtcNow, PointCount = 5,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var purged = await RecordingSessionCleanupService.ReconcileGhostShellsAsync(
            _context, _command, NullLogger.Instance, default);

        Assert.Equal(0, purged);
        Assert.Equal(1, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(46)));
    }

    [Fact]
    public async Task Reconciler_NeverTouchesHealthyRows()
    {
        // In-flight live recording: released + NULL stats, but its session row exists — must survive.
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(41), UserId = Me, ActivityType = "Ride",
            Source = "live-recording", Lifecycle = LifecycleStates.Released,
            LifecycleReason = LifecycleReasons.Recording,
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = "live", TrackId = new TrackId(41), UserId = Me,
            Status = "recording", StartedAt = DateTime.UtcNow,
        });
        // Import with NULL stats and no session: not recording-sourced — must survive (source guard).
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(42), UserId = Me, ActivityType = "Ride",
            Source = "gpx", Lifecycle = LifecycleStates.Released,
        });
        // Finalized recording (stats set) whose session row is gone — must survive (NULL-stats guard).
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(43), UserId = Me, ActivityType = "Ride",
            Source = "live-recording", Lifecycle = LifecycleStates.Released,
            TotalDistance = 12.3,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var purged = await RecordingSessionCleanupService.ReconcileGhostShellsAsync(
            _context, _command, NullLogger.Instance, default);

        Assert.Equal(0, purged);
        Assert.Equal(3, await _context.Activities.AsNoTracking()
            .CountAsync(a => a.TrackId == new TrackId(41) || a.TrackId == new TrackId(42) || a.TrackId == new TrackId(43)));
    }

    [Fact]
    public async Task Purge_TrashedUsesShorterWindow()
    {
        var now = DateTime.UtcNow;
        // Review-rejected rides purge on the 2-day window, not the 7-day one.
        await SeedAbandonedAsync("trash-old", new TrackId(10), now.AddDays(-3), lifecycle: LifecycleStates.Trashed, reason: LifecycleReasons.ReviewRejected); // > 2d
        await SeedAbandonedAsync("trash-fresh", new TrackId(11), now.AddHours(-12), lifecycle: LifecycleStates.Trashed, reason: LifecycleReasons.ReviewRejected); // < 2d
        await SeedAbandonedAsync("abandoned-5d", new TrackId(12), now.AddDays(-5)); // plain abandoned, within 7d → kept

        _context.ChangeTracker.Clear();

        var purged = await PurgeAsync(_command, now - TimeSpan.FromDays(7), now - TimeSpan.FromDays(2));

        Assert.Equal(1, purged); // only trash-old
        Assert.Equal(0, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(10)));
        Assert.Equal(1, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(11)));
        Assert.Equal(1, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(12)));
    }

    // ── SVC-02: an abandoned MANUAL recording with staged GPS is finalized, not hard-purged ──

    private async Task SeedAbandonedManualAsync(string sessionId, TrackId trackId, DateTime stoppedAt, bool withStaging)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = Me, ActivityType = "Ride",
            Source = ActivitySources.LiveRecording, Lifecycle = LifecycleStates.Released,
            LifecycleReason = LifecycleReasons.Recording,
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = sessionId, TrackId = trackId, UserId = Me,
            Status = "abandoned", StartedAt = stoppedAt.AddHours(-1), StoppedAt = stoppedAt,
            PointCount = withStaging ? 2 : 0,
        });
        await _context.SaveChangesAsync();
        if (withStaging)
            await new RecordingStagingStore(_context).AppendAsync(trackId, 0, new[]
            {
                new ExternalGpsPoint { Lat = 47.0,   Lng = 11.0, Distance = 0,   TimestampUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc) },
                new ExternalGpsPoint { Lat = 47.001, Lng = 11.0, Distance = 111, TimestampUtc = new DateTime(2025, 1, 1, 8, 0, 1, DateTimeKind.Utc) },
            });
    }

    [Fact]
    public async Task Purge_AbandonedManualWithStagedGps_IsFinalizedNotPurged()
    {
        var now = DateTime.UtcNow;
        // A manual recording, abandoned past the 7-day cutoff, still holding real staged GPS → rescue it.
        await SeedAbandonedManualAsync("rescue", new TrackId(60), now.AddDays(-8), withStaging: true);
        // A manual session with no staged GPS past the cutoff → nothing to rescue → still hard-purged.
        await SeedAbandonedManualAsync("empty-manual", new TrackId(61), now.AddDays(-8), withStaging: false);
        _context.ChangeTracker.Clear();

        var finalizer = new RecordingFinalizeSpy(_connStr);
        var purged = await PurgeAsync(
            _command, now - TimeSpan.FromDays(7), now - TimeSpan.FromDays(2), CoordinatorOver(finalizer));

        // Only the empty manual session was hard-purged; the one with staged GPS took the finalize path.
        Assert.Equal(1, purged);
        Assert.Equal(new[] { "rescue" }, finalizer.Finalized, StringComparer.Ordinal);
        Assert.Equal(0, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(61))); // empty purged

        // Rescued: the activity shell + session survive (status finalized) and the staged GPS was NOT destroyed.
        Assert.Equal(1, await _context.Activities.AsNoTracking().CountAsync(a => a.TrackId == new TrackId(60)));
        var sess = await _context.RecordingSessions.AsNoTracking().FirstAsync(s => s.SessionId == "rescue");
        Assert.Equal(RecordingSessionStatus.Finalized, sess.Status);
        Assert.True(await new RecordingStagingStore(_context).ExistsAsync(new TrackId(60)));
    }
}

/// <summary>Delegates to the real command but, on the first core delete, flips a target session to
/// 'finalized' first — simulating a Restore that lands between the purge snapshot and a later candidate's
/// delete (RF-08 recheck test).</summary>
file sealed class FlipOnFirstDeleteCommand(
    IActivityCommandService inner, RouteBaseDbContext context, string flipSessionId) : IActivityCommandService
{
    private bool _flipped;

    public async Task<bool> DeleteActivityCoreAsync(TrackId trackId, string ownerId, bool requireTrashed = false)
    {
        if (!_flipped)
        {
            _flipped = true;
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE RecordingSession SET status = 'finalized' WHERE session_id = {0}", flipSessionId);
        }
        return await inner.DeleteActivityCoreAsync(trackId, ownerId, requireTrashed);
    }

    public Task<bool> UpdateActivityAsync(TrackId trackId, string? name, string? activityType, string? visibility) =>
        throw new NotSupportedException();
    public Task<int> UpdateManyAsync(IReadOnlyList<TrackId> trackIds, string? activityType, string? visibility) =>
        throw new NotSupportedException();
    public Task<bool> DeleteActivityAsync(TrackId trackId) => throw new NotSupportedException();
    public Task<int> DeleteManyAsync(IReadOnlyList<TrackId> trackIds) => throw new NotSupportedException();
}

/// <summary>Delegates to the real command but records, on its first call, whether the named session's
/// row was already gone — proving the purge's guarded delete resolved the race before the (multi-step,
/// non-atomic) Activity-core delete ever started (SVC-05 ordering pin).</summary>
file sealed class RecordingSessionGoneProbeCommand(
    IActivityCommandService inner, RouteBaseDbContext context, string watchedSessionId) : IActivityCommandService
{
    public bool SessionRowWasGoneBeforeActivityDelete { get; private set; }

    public async Task<bool> DeleteActivityCoreAsync(TrackId trackId, string ownerId, bool requireTrashed = false)
    {
        SessionRowWasGoneBeforeActivityDelete = !await context.RecordingSessions
            .AsNoTracking().AnyAsync(s => s.SessionId == watchedSessionId);
        return await inner.DeleteActivityCoreAsync(trackId, ownerId, requireTrashed);
    }

    public Task<bool> UpdateActivityAsync(TrackId trackId, string? name, string? activityType, string? visibility) =>
        throw new NotSupportedException();
    public Task<int> UpdateManyAsync(IReadOnlyList<TrackId> trackIds, string? activityType, string? visibility) =>
        throw new NotSupportedException();
    public Task<bool> DeleteActivityAsync(TrackId trackId) => throw new NotSupportedException();
    public Task<int> DeleteManyAsync(IReadOnlyList<TrackId> trackIds) => throw new NotSupportedException();
}

/// <summary>Finalization stub that must never be reached — the auto-source and no-staging purge paths do
/// not finalize, so any call is a routing bug.</summary>
file sealed class NeverFinalize : IRecordingFinalizationService
{
    public Task FinalizeAsync(string sessionId) =>
        throw new Xunit.Sdk.XunitException($"finalize should not have run for session {sessionId}");
    public Task<ElevationRefreshResult?> RefreshElevationAsync(TrackId trackId) => throw new NotSupportedException();
}

/// <summary>Records which sessions were finalized and flips each to 'finalized' over its own context on the
/// shared-cache DB — stands in for the real finalize on the SVC-02 rescue path (a manual abandoned session
/// with staged GPS). The rescued track's real stats/compaction are the finalization service's own tested
/// job; this pins only the cleanup service's routing to it.</summary>
file sealed class RecordingFinalizeSpy(string connStr) : IRecordingFinalizationService
{
    public List<string> Finalized { get; } = new();

    public async Task FinalizeAsync(string sessionId)
    {
        Finalized.Add(sessionId);
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connStr).Options;
        await using var ctx = new RouteBaseDbContext(options);
        await ctx.Database.ExecuteSqlRawAsync(
            "UPDATE RecordingSession SET status = 'finalized' WHERE session_id = {0}", sessionId);
    }

    public Task<ElevationRefreshResult?> RefreshElevationAsync(TrackId trackId) => throw new NotSupportedException();
}
