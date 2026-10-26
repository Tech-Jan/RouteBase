using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Queue;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// The last uncovered span of the approve → segments chain: <see cref="OperationQueueWorker"/> dequeue →
/// <see cref="TrackEditDeriveHandler"/> → <see cref="OperationScope"/> impersonation → the derive.
///
/// <c>ApproveSegmentMatchTests</c> drains the queue inline and hands the payload to a hand-built
/// TrackEditDeriveService, so it proves the matcher but not the dispatch. Everything a real deployment does
/// between "the work item exists" and "the matcher runs" lived here untested — and it is the only place a
/// production-only failure could still hide, because every other step either throws (and the Error_Log
/// mirror would have it) or is covered elsewhere.
///
/// Two things are deliberately REAL here rather than faked:
///   • the container, so <c>GetRequiredService&lt;TrackEditDeriveService&gt;()</c> can actually fail to resolve;
///   • <see cref="CurrentUserService"/>, so <c>SetUserIdOverride</c> is genuinely exercised. A fixed-id fake
///     would make the impersonation half vacuous — and impersonation is load-bearing for SHAPE segments:
///     ShapeSegmentMatcher's candidate query admits a private, still-owned ride only through its
///     `a.user_id = {curUser}` branch, where curUser is GetCurrentUserId() inside the worker scope. Lose the
///     override and a private ride silently matches nothing.
/// </summary>
public class ApproveWorkerSegmentMatchTests : IAsyncLifetime
{
    private const string User = "test-user";

    private RouteBaseDbContext _context = null!;   // held open so the shared-cache in-memory DB survives
    private ServiceProvider _provider = null!;
    private AutoReviewService _review = null!;
    private OperationQueue _queue = null!;
    private OperationQueueWorker _worker = null!;
    /// <summary>Every log this graph emits. The whole reason this path was undiagnosable in production is
    /// that its failures are logged and then swallowed, so the tests read them back.</summary>
    private readonly CapturingLoggerProvider _logs = new();
    /// <summary>Which segments the derive flipped stale — the observable side of SD-01.</summary>
    private readonly RecordingHeatmapLifecycle _heatmaps = new();

    public async Task InitializeAsync()
    {
        var connStr = $"Data Source=file:approveworker_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connStr).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schema = new DbSchemaManager(_context);
        await schema.EnsureActivityBboxTableAsync();
        await schema.EnsureTrackContentTablesAsync();
        // ContentTrackReader probes Recording_Staging on EVERY read (the live-session tail merge), so the
        // table has to exist even though nothing here records — without it the reader throws and the derive
        // swallows it. Not needed by tests that use DirectTrackReader, which is why it is easy to miss.
        await schema.EnsureRecordingStagingTableAsync();
        await schema.EnsureIndexesAsync();

        _context.Users.Add(new AppUser
        {
            UserId = User, DisplayName = "Test User", Email = "test@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        _queue = new OperationQueue(TimeProvider.System);
        _provider = BuildProvider(connStr);

        _worker = new OperationQueueWorker(
            _queue,
            _provider.GetServices<IOperationHandler>(),
            _provider.GetRequiredService<ILogger<OperationQueueWorker>>());

        // Approve side runs on its own context, as the HTTP request would.
        var visibility  = new TestVisibilityService(User);
        var invalidator = _provider.GetRequiredService<UserCacheInvalidator>();
        var lifecycle   = TestLifecycle.For(_context, visibility, invalidator);
        var command     = new ActivityCommandService(
            _context, visibility, invalidator,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityCommandService>.Instance, lifecycle);

        _review = new AutoReviewService(
            _context, visibility, lifecycle, command,
            new TrackDeriveQueue(_queue, Microsoft.Extensions.Logging.Abstractions.NullLogger<TrackDeriveQueue>.Instance),
            new TestProfileSettingsService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AutoReviewService>.Instance, TimeProvider.System);
    }

    /// <summary>The production graph for everything the handler resolves — mirrors Program.cs / the
    /// per-slice Add*Services() registrations, including their LIFETIMES (CurrentUserService must be Scoped
    /// or the override lands on a different instance than VisibilityService reads).</summary>
    private ServiceProvider BuildProvider(string connStr) =>
        new ServiceCollection()
            .AddLogging(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Debug))
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddHttpContextAccessor()
            .AddDbContext<RouteBaseDbContext>(o => o.UseSqlite(connStr))
            .AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()))
            .AddSingleton<UserCacheInvalidator>()
            .AddSingleton<SegmentAnalysisProgress>()
            // Identity: the REAL one, so SetUserIdOverride is what makes the owner branch apply.
            .AddScoped<ICurrentUserService, CurrentUserService>()
            .AddScoped<IFriendshipService, NoFriendsService>()
            .AddScoped<IVisibilityService, VisibilityService>()
            .AddScoped<IProfileSettingsService, ProfileSettingsService>()
            .AddScoped<SegmentCache>()
            .AddScoped<ITrackObjectStore, TrackObjectStore>()
            .AddScoped<TrackContentStore>()
            .AddScoped<RecordingStagingStore>()
            .AddSingleton<ITrackReader>(sp => new ContentTrackReader(
                new TrackReaderCache(64L * 1024 * 1024), new SingleFlight(),
                sp.GetRequiredService<IServiceScopeFactory>()))
            // Singleton instance, not the no-op: the stale-marks are an assertion target, so they have to
            // survive the worker's scope.
            .AddSingleton<IHeatmapLifecycle>(_heatmaps)
            .AddScoped<ISegmentMatchStrategy, ShapeSegmentMatcher>()
            .AddScoped<IMatchEngine, MatchEngineService>()
            .AddScoped<IEffortMath, EffortMathService>()
            .AddScoped<ISegmentAnalysis, SegmentAnalysis>()
            .AddScoped<IPhotoCommandService, NoOpPhotoCommandService>()
            .AddScoped<TrackEditDeriveService>()
            .AddSingleton<IOperationHandler, TrackEditDeriveHandler>()
            .BuildServiceProvider();

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    /// <summary>
    /// Approve → the worker dispatches → both segment kinds get the ride. The shape half is what proves
    /// impersonation survived into the matcher's SQL.
    /// </summary>
    [Fact]
    public async Task Approve_ThroughTheRealWorker_PutsTheRideOnBothSegmentKinds()
    {
        var polyId  = await SeedPolylineSegmentAsync();
        var shapeId = await SeedShapeSegmentAsync();
        var trackId = await SeedPendingAutoRecordingAsync();

        await ApproveAndDrainAsync(trackId);

        Assert.True((await EffortsAsync(polyId, trackId)).Count > 0, $"polyline segment got no efforts.{_logs}");
        Assert.True((await EffortsAsync(shapeId, trackId)).Count > 0, $"shape segment got no efforts.{_logs}");

        // The failure this whole path is prone to is "logged, then swallowed" — so assert it stayed silent.
        Assert.False(_logs.HasErrors, $"the derive logged an error.{_logs}");
    }

    /// <summary>
    /// SD-01, per-segment half: a ride that joins ONE of two shape segments leaves the other's heatmaps
    /// alone. The shape matcher reports WHICH segments changed rather than whether any did — gating on
    /// "any" marks every shape segment the user owns stale on every single import, which is N badges and N
    /// aggregate passes for one match.
    /// </summary>
    [Fact]
    public async Task Approve_MarksOnlyTheShapeSegmentsTheRideActuallyJoined()
    {
        var joined    = await SeedShapeSegmentAsync();
        var elsewhere = await SeedShapeSegmentAsync(FarFromTheCorridor);
        var trackId   = await SeedPendingAutoRecordingAsync();

        await ApproveAndDrainAsync(trackId);

        Assert.True((await EffortsAsync(joined, trackId)).Count > 0, $"shape segment got no efforts.{_logs}");
        Assert.Empty(await EffortsAsync(elsewhere, trackId));

        Assert.Contains(joined, _heatmaps.Stale);
        Assert.DoesNotContain(elsewhere, _heatmaps.Stale);
        Assert.False(_logs.HasErrors, $"the derive logged an error.{_logs}");
    }

    /// <summary>
    /// The other end of the same contract: a ride that joins NO shape segment does no follow-up work at
    /// all — no stale mark, so no badge thrash on an import that simply went somewhere else.
    /// </summary>
    [Fact]
    public async Task Approve_WhenNoShapeSegmentMatches_MarksNothingStale()
    {
        await SeedShapeSegmentAsync(FarFromTheCorridor);
        var trackId = await SeedPendingAutoRecordingAsync();

        await ApproveAndDrainAsync(trackId);

        Assert.Empty(_heatmaps.Stale);
        Assert.False(_logs.HasErrors, $"the derive logged an error.{_logs}");
    }

    /// <summary>
    /// A cancelled work item is skipped at dequeue and leaves the segments untouched. Pins the silent-drop
    /// branch that made this failure mode unobservable — the worker now says so in the log, and the ride
    /// stays unmatched until something re-derives it.
    /// </summary>
    [Fact]
    public async Task CancelledBeforeDispatch_LeavesTheRideUnmatched()
    {
        var polyId  = await SeedPolylineSegmentAsync();
        var trackId = await SeedPendingAutoRecordingAsync();

        Assert.True(await _review.ConfirmAsync(trackId, "Ride"));
        _queue.CancelForUser(User);   // as the op-toast's Cancel button does

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _worker.StartAsync(cts.Token);
        try
        {
            await WaitForProcessedAsync(1, cts.Token);   // the worker still "completes" it — by skipping
        }
        finally
        {
            await _worker.StopAsync(CancellationToken.None);
        }

        Assert.Empty(await EffortsAsync(polyId, trackId));
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Approve the ride and run the real worker until it has drained the enqueued derive.</summary>
    private async Task ApproveAndDrainAsync(TrackId trackId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _worker.StartAsync(cts.Token);
        try
        {
            Assert.True(await _review.ConfirmAsync(trackId, "Ride"));
            await WaitForProcessedAsync(1, cts.Token);
        }
        finally
        {
            await _worker.StopAsync(CancellationToken.None);
        }
    }

    // The worker is a BackgroundService with no completion signal; ProcessedCount is the queue's own
    // monotonic counter, incremented once per drained item (skipped ones included).
    private async Task WaitForProcessedAsync(int expected, CancellationToken ct)
    {
        while (_queue.ProcessedCount < expected)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(25, ct);
        }
    }

    private Task<List<GpsInSegment>> EffortsAsync(SegmentId segId, TrackId trackId) =>
        _context.GpsInSegments.AsNoTracking()
            .Where(e => e.SegmentId == segId && e.TrackId == trackId).ToListAsync();

    // ── Seeds ────────────────────────────────────────────────────────────────────────────────────────
    // One corridor, lat 47.000..47.019 @ lng 11.000, shared by the ride and both segments.

    private static (double lat, double lng)[] Corridor()
    {
        var pts = new (double lat, double lng)[20];
        for (int i = 0; i < 20; i++) pts[i] = (47.000 + i * 0.001, 11.000);
        return pts;
    }

    /// <summary>Private visibility on purpose: it is what makes the shape matcher's owner branch the ONLY
    /// way this ride can be a candidate, so the assertion actually depends on impersonation.</summary>
    private async Task<TrackId> SeedPendingAutoRecordingAsync()
    {
        var points = Corridor();
        var activity = new Activity
        {
            UserId = User, ActivityType = "Ride",
            Source = ActivitySources.AutoRecording,
            Lifecycle = LifecycleStates.InWork,
            LifecycleReason = LifecycleReasons.AwaitingReview,
            Visibility = Visibilities.Private,
            StartGpsLat = points[0].lat, StartGpsLong = points[0].lng,
            EndGpsLat = points[^1].lat, EndGpsLong = points[^1].lng,
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        await TrackSeed.SeedTrackAsync(
            _context, trackId,
            points.Select(p => new GpsData { GpsLat = p.lat, GpsLong = p.lng }).ToList());

        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})",
            trackId.Value,
            points.Min(p => p.lat), points.Max(p => p.lat),
            points.Min(p => p.lng), points.Max(p => p.lng));

        return trackId;
    }

    private async Task<SegmentId> SeedPolylineSegmentAsync()
    {
        var pts = new (double lat, double lng)[8];
        for (int i = 0; i < 8; i++) pts[i] = (47.000 + (6 + i) * 0.001, 11.000);

        double dist = 0;
        for (int i = 1; i < pts.Length; i++) dist += Haversine(pts[i - 1], pts[i]);

        var segment = new Segment { UserId = User, SegmentDistance = dist };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();

        foreach (var (lat, lng) in pts)
            _context.SegmentGps.Add(new SegmentGps { SegmentId = segment.SegmentId, GpsLat = lat, GpsLong = lng });
        await _context.SaveChangesAsync();
        return segment.SegmentId;
    }

    /// <summary>A zone the corridor never enters — the ride is a legitimate non-match for it, which is
    /// what makes "did this segment change" observable separately from "did any segment change".</summary>
    private static RectGeom FarFromTheCorridor =>
        new() { South = 40.000, West = 5.000, North = 40.100, East = 5.100 };

    private static RectGeom OverTheCorridor =>
        new() { South = 46.999, West = 10.999, North = 47.020, East = 11.001 };

    private async Task<SegmentId> SeedShapeSegmentAsync(RectGeom? zone = null)
    {
        var rules = new RuleDocument
        {
            Shapes = new()
            {
                new ShapeDef { No = 1, Kind = "zone", Geom = zone ?? OverTheCorridor },
            },
            Membership = { new PassesAllRule { Zones = { 1 } } },
        };

        var segment = new Segment
        {
            UserId = User, SegmentKind = SegmentKinds.Shapes, MatchRulesJson = rules.Serialize(),
        };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();
        return segment.SegmentId;
    }

    private static double Haversine((double lat, double lng) a, (double lat, double lng) b)
    {
        const double R = 6371000;
        double dLat = (b.lat - a.lat) * Math.PI / 180;
        double dLng = (b.lng - a.lng) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(a.lat * Math.PI / 180) * Math.Cos(b.lat * Math.PI / 180)
                 * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * R * Math.Asin(Math.Sqrt(h));
    }

    /// <summary>
    /// Records which segments were flipped stale. Everything else is the no-op behaviour — this graph
    /// generates no heatmaps, it only has to observe the staleness decision.
    /// </summary>
    private sealed class RecordingHeatmapLifecycle : IHeatmapLifecycle
    {
        private readonly List<SegmentId> _stale = new();
        private readonly object _gate = new();

        /// <summary>Segments marked stale, in call order. Read after the worker has stopped.</summary>
        public IReadOnlyList<SegmentId> Stale { get { lock (_gate) return _stale.ToList(); } }

        public Task WithdrawAsync(SegmentId segmentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkStaleAsync(SegmentId segmentId, CancellationToken ct = default)
        {
            lock (_gate) _stale.Add(segmentId);
            return Task.CompletedTask;
        }

        public Task<HeatmapAttachResult> AttachAsync(SegmentId segmentId, IEnumerable<string> activityTypes) =>
            Task.FromResult(HeatmapAttachResult.NotFound);
        public Task FailPendingAsync(SegmentId segmentId, string error) => Task.CompletedTask;
        public Task<Heatmap?> GetVisibleAsync(SegmentId segmentId, CancellationToken ct = default) =>
            Task.FromResult<Heatmap?>(null);
        public Task MarkStaleForTrackAsync(TrackId trackId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int?> CaptureIdForSegmentAsync(SegmentId segmentId, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);
        public void PurgeTiles(int heatmapId) { }
    }

    /// <summary>Collects every log record so a failing assertion can show what the background work said.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = new();
        private readonly object _lock = new();

        public bool HasErrors { get { lock (_lock) return _lines.Any(l => l.StartsWith("Error", StringComparison.Ordinal)); } }

        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, _lines, _lock);
        public void Dispose() { }

        public override string ToString()
        {
            lock (_lock) return _lines.Count == 0 ? " (no logs)" : "\n  " + string.Join("\n  ", _lines);
        }

        private sealed class ListLogger(string category, List<string> lines, object gate) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopUserScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var text = $"{logLevel} [{category}] {formatter(state, exception)}";
                if (exception is not null) text += $" :: {exception.GetType().Name}: {exception.Message}";
                lock (gate) lines.Add(text);
            }
        }
    }

    /// <summary>Friendship is irrelevant here (the polyline path ignores friendIds; the shape path's friend
    /// clause only widens the candidate set) — but VisibilityService requires the dependency.</summary>
    private sealed class NoFriendsService : IFriendshipService
    {
        public Task<List<string>> GetAcceptedFriendIdsAsync(string userId) => Task.FromResult(new List<string>());
        public Task<List<FriendListItem>> GetFriendsAsync(string userId) => Task.FromResult(new List<FriendListItem>());
        public Task<List<FriendRequestItem>> GetPendingRequestsAsync(string userId) => Task.FromResult(new List<FriendRequestItem>());
        public Task<int> GetPendingRequestCountAsync(string userId) => Task.FromResult(0);
        public Task SendRequestAsync(string requesterId, string addresseeId) => Task.CompletedTask;
        public Task AcceptRequestAsync(string currentUserId, int friendshipId) => Task.CompletedTask;
        public Task DeclineRequestAsync(string currentUserId, int friendshipId) => Task.CompletedTask;
        public Task RemoveFriendAsync(string currentUserId, int friendshipId) => Task.CompletedTask;
        public Task<List<UserSearchResult>> SearchUsersAsync(string query, string excludeUserId)
            => Task.FromResult(new List<UserSearchResult>());
    }

    private sealed class NoOpPhotoCommandService : IPhotoCommandService
    {
        public Task<PhotoCommandOutcome> PlaceAsync(PhotoId photoId, double lat, double lng, CancellationToken ct = default)
            => Task.FromResult(PhotoCommandOutcome.Ok);
        public Task<PhotoCommandOutcome> UnplaceAsync(PhotoId photoId, CancellationToken ct = default)
            => Task.FromResult(PhotoCommandOutcome.Ok);
        public Task<RestoreResult> RestoreAsync(PhotoId photoId, CancellationToken ct = default)
            => Task.FromResult(new RestoreResult(PhotoCommandOutcome.Ok, null, null));
        public Task<PhotoCommandOutcome> DeleteAsync(PhotoId photoId, CancellationToken ct = default)
            => Task.FromResult(PhotoCommandOutcome.Ok);
        public Task<int> DeleteManyAsync(IReadOnlyCollection<int> photoIds, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<int> RematchTrackPhotosAsync(TrackId trackId, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
