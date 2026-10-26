using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// GodFileSplit characterization pin: the cross-responsibility cache-invalidation contract that the
/// Crud → Efforts split must preserve. Efforts caches its detailed-effort list under
/// <see cref="CacheKeys.SegmentEfforts"/>; a Crud delete and an effort removal MUST evict that key
/// (Crud's Delete must still reach Efforts' cache keys). Observes a real <see cref="MemoryCache"/>.
/// </summary>
public class SegmentCacheInvalidationPinTests : IAsyncLifetime
{
    private const string User = "test-user";
    private static readonly DateTime T0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string ViewerB = "viewer-b";

    private RouteBaseDbContext _context = null!;
    private MemoryCache _cache = null!;
    private UserCacheInvalidator _invalidator = null!;
    private IProfileSettingsService _settings = null!;
    private ITrackReader _reader = null!;
    private ISegmentMatchStrategy _shapeMatcher = null!;
    private IMatchEngine _engine = null!;
    private IEffortMath _effortMath = null!;
    private SegmentServices _svc = null!;

    // Builds a services instance over the SHARED cache + invalidator for a given viewer, so an
    // owner-side mutation (via _svc) and another viewer's warmed entry share the token machinery.
    private SegmentServices BuildServices(IVisibilityService visibility) =>
        SegmentServices.Create(
            _context, _cache, visibility, _invalidator, _settings,
            _shapeMatcher, _engine, _effortMath, _reader);

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:cacheinv_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options);
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureTrackContentTablesAsync();

        _context.Users.Add(new AppUser
        {
            UserId = User, DisplayName = User, Email = "t@t.com", IsAdmin = false,
            CreatedAt = T0.ToString("O"), LastLogin = T0.ToString("O"),
        });
        await _context.SaveChangesAsync();

        _cache = new MemoryCache(new MemoryCacheOptions());
        _invalidator = new UserCacheInvalidator();
        _settings = new ProfileSettingsService(_context, NullLogger<ProfileSettingsService>.Instance, _cache, TimeProvider.System);
        _reader = new DirectTrackReader(_context);
        _shapeMatcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, _reader);
        _engine = new MatchEngineService(_context, _reader, NullLogger<MatchEngineService>.Instance);
        _effortMath = new EffortMathService(_context, _reader, NullLogger<EffortMathService>.Instance);
        _svc = BuildServices(new TestVisibilityService(User));
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // Segment + one fully-aggregated effort → GetSegmentEffortsDetailedAsync returns it and caches
    // the list under SegmentEfforts(user, seg).
    private async Task<(SegmentId segId, GpsInSegmentId effortId)> SeedAnalyzedEffortAsync()
    {
        TrackId trackId = new TrackId(1);
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = User, TrackName = "T",
            ActivityType = "cycling", StartDateTimeLocal = T0,
        });
        await _context.SaveChangesAsync();

        var pts = new List<GpsData>();
        for (int i = 0; i < 5; i++)
            pts.Add(new GpsData
            {
                DateTimeUtc = T0.AddSeconds(i * 10),
                GpsLat = 47.000 + i * 0.001, GpsLong = 11.000,
                Distance = i * 100, Speed = 1.0 + i, Altitude = 100 + i * 10,
            });
        await TrackSeed.SeedTrackAsync(_context, trackId, pts);

        var seg = new Segment { UserId = User, SegmentName = "S", SegmentDistance = 400 };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();

        var effort = new GpsInSegment
        {
            SegmentId = seg.SegmentId, TrackId = trackId,
            GpsIdEntryCalculated = 0, GpsIdExitCalculated = 4,
            SegPerc = 100,
            EffortDistance = 400, EffortElevation = 40, EffortTime = 40, EffortSpeedAvg = 10,
        };
        _context.GpsInSegments.Add(effort);
        await _context.SaveChangesAsync();
        return (seg.SegmentId, effort.GpsInSegmentId);
    }

    [Fact]
    public async Task DeleteSegment_EvictsEffortsCache()
    {
        var (segId, _) = await SeedAnalyzedEffortAsync();

        // Warm the Efforts read-model cache.
        var warmed = await _svc.Efforts.GetSegmentEffortsDetailedAsync(segId);
        Assert.Single(warmed);
        Assert.True(_cache.TryGetValue(CacheKeys.SegmentEfforts(User, segId), out _));

        // Production warms & deletes in separate request scopes (fresh context each). Clear the tracker
        // so the raw-SQL effort delete inside DeleteSegmentAsync doesn't collide with the effort entity
        // this test's shared context tracked while warming.
        _context.ChangeTracker.Clear();

        await _svc.Crud.DeleteSegmentAsync(segId);

        // Crud's delete must reach the Efforts cache key.
        Assert.False(_cache.TryGetValue(CacheKeys.SegmentEfforts(User, segId), out _));
    }

    // SA-06: the three deletes (GPSinSegment, Segment_GPS, Segment) commit atomically and all clear.
    [Fact]
    public async Task DeleteSegment_RemovesAllThreeTables()
    {
        var (segId, _) = await SeedAnalyzedEffortAsync();
        _context.SegmentGps.Add(new SegmentGps { SegmentId = segId, GpsLat = 47.0, GpsLong = 11.0 });
        _context.SegmentGps.Add(new SegmentGps { SegmentId = segId, GpsLat = 47.001, GpsLong = 11.0 });
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var deleted = await _svc.Crud.DeleteSegmentAsync(segId);

        Assert.True(deleted);
        Assert.False(await _context.Segments.AnyAsync(s => s.SegmentId == segId));
        Assert.False(await _context.GpsInSegments.AnyAsync(g => g.SegmentId == segId));
        Assert.False(await _context.SegmentGps.AnyAsync(g => g.SegmentId == segId));
    }

    [Fact]
    public async Task RemoveEffort_EvictsEffortsCache()
    {
        var (segId, effortId) = await SeedAnalyzedEffortAsync();

        var warmed = await _svc.Efforts.GetSegmentEffortsDetailedAsync(segId);
        Assert.Single(warmed);
        Assert.True(_cache.TryGetValue(CacheKeys.SegmentEfforts(User, segId), out _));

        await _svc.Efforts.RemoveEffortAsync(effortId);

        Assert.False(_cache.TryGetValue(CacheKeys.SegmentEfforts(User, segId), out _));
    }

    // SA-07: another viewer's warmed entry is tagged with the segment OWNER's change token, so an
    // owner effort-removal (which trips that token via InvalidateUser) evicts it — not just the owner's
    // own keys. Without owner-tagging the friend keeps serving the removed effort until the TTL.
    [Fact]
    public async Task RemoveEffort_EvictsOtherViewersCache()
    {
        var (segId, effortId) = await SeedAnalyzedEffortAsync();
        var viewer = BuildServices(new MultiVisibilityService(ViewerB, User));

        // Friend viewer B warms their own per-viewer cache entry over owner A's segment.
        var warmed = await viewer.Efforts.GetSegmentEffortsDetailedAsync(segId);
        Assert.Single(warmed);
        Assert.True(_cache.TryGetValue(CacheKeys.SegmentEfforts(ViewerB, segId), out _));

        // Owner A removes the effort → B's entry must be gone on the next read.
        await _svc.Efforts.RemoveEffortAsync(effortId);

        Assert.False(_cache.TryGetValue(CacheKeys.SegmentEfforts(ViewerB, segId), out _));
    }

    [Fact]
    public async Task DeleteSegment_EvictsOtherViewersCache()
    {
        var (segId, _) = await SeedAnalyzedEffortAsync();
        var viewer = BuildServices(new MultiVisibilityService(ViewerB, User));

        var warmed = await viewer.Efforts.GetSegmentEffortsDetailedAsync(segId);
        Assert.Single(warmed);
        Assert.True(_cache.TryGetValue(CacheKeys.SegmentEfforts(ViewerB, segId), out _));

        // Warm & delete run in separate request scopes in production (fresh context each). Clear the
        // tracker so the raw-SQL effort delete doesn't collide with the tracked warm-time entity.
        _context.ChangeTracker.Clear();

        await _svc.Crud.DeleteSegmentAsync(segId);

        Assert.False(_cache.TryGetValue(CacheKeys.SegmentEfforts(ViewerB, segId), out _));
    }

    // SE-02: a re-analysis rebuilds the segment's efforts (new rowids under SQLite reuse), so per-effort
    // perf-cache entries keyed by the old ids are stale. AnalyzeSegmentMatchesAsync must evict them —
    // InvalidateSegment can't target that key family, so the owner-token trip (InvalidateUser) does it.
    [Fact]
    public async Task Reanalyze_EvictsPerEffortPerformanceEntries()
    {
        var (segId, effortId) = await SeedAnalyzedEffortAsync();

        // Warm the per-effort performance cache entry (full-res budget 0).
        var warmed = await _svc.Efforts.GetPerformanceDataForEffortsAsync(segId, new HashSet<GpsInSegmentId> { effortId }, 0);
        Assert.Single(warmed);
        Assert.True(_cache.TryGetValue(CacheKeys.SegmentPerformanceEffort(User, segId, effortId, 0), out _));

        _context.ChangeTracker.Clear();
        await _svc.Analysis.AnalyzeSegmentMatchesAsync(segId);

        Assert.False(_cache.TryGetValue(CacheKeys.SegmentPerformanceEffort(User, segId, effortId, 0), out _));
    }

    // SE-03: the shared reference-polyline entry (keyed by segmentId only) must be evicted by
    // InvalidateSegment, else a delete + SQLite id-reuse serves the old segment's geometry.
    [Fact]
    public async Task InvalidateSegment_EvictsSegmentReference()
    {
        var (segId, effortId) = await SeedAnalyzedEffortAsync();
        // Reference geometry: ≥2 distinct points so BuildSegmentReference produces (and caches) a reference.
        for (int i = 0; i < 3; i++)
            _context.SegmentGps.Add(new SegmentGps { SegmentId = segId, GpsLat = 47.000 + i * 0.001, GpsLong = 11.0 });
        await _context.SaveChangesAsync();

        // Warming the perf path populates the shared SegmentReference entry.
        await _svc.Efforts.GetPerformanceDataForEffortsAsync(segId, new HashSet<GpsInSegmentId> { effortId }, 0);
        Assert.True(_cache.TryGetValue(CacheKeys.SegmentReference(segId), out _));

        _svc.Cache.InvalidateSegment(User, segId);

        Assert.False(_cache.TryGetValue(CacheKeys.SegmentReference(segId), out _));
    }

    // Viewer that can SEE (but not OWN) a set of other users' segments/activities — models a friend.
    private sealed class MultiVisibilityService : IVisibilityService
    {
        private readonly string _userId;
        private readonly string[] _visible;

        public MultiVisibilityService(string userId, params string[] alsoVisible)
        {
            _userId  = userId;
            _visible = alsoVisible.Append(userId).Distinct(StringComparer.Ordinal).ToArray();
        }

        public string GetCurrentUserId() => _userId;

        public IQueryable<Activity> VisibleActivities(IQueryable<Activity> q) =>
            q.Where(a => a.UserId != null && _visible.Contains(a.UserId));
        public IQueryable<Segment> VisibleSegments(IQueryable<Segment> q) =>
            q.Where(s => s.UserId != null && _visible.Contains(s.UserId));
        public IQueryable<MultiDayTour> VisibleTours(IQueryable<MultiDayTour> q) =>
            q.Where(t => t.UserId != null && _visible.Contains(t.UserId));

        public IQueryable<Activity> OwnedActivities(IQueryable<Activity> q) => q.Where(a => a.UserId == _userId);
        public IQueryable<Segment> OwnedSegments(IQueryable<Segment> q) => q.Where(s => s.UserId == _userId);
        public IQueryable<MultiDayTour> OwnedTours(IQueryable<MultiDayTour> q) => q.Where(t => t.UserId == _userId);
        public IQueryable<AppCredentials> OwnedCredentials(IQueryable<AppCredentials> q) => q.Where(c => c.UserId == _userId);

        public Task<List<string>> GetVisibleUserIdsAsync() => Task.FromResult(_visible.ToList());
        public Task<bool> CanViewItemAsync(string ownerUserId, string? visibility) =>
            Task.FromResult(_visible.Contains(ownerUserId) || visibility == "public");
        // Permissive is deliberate and unreachable: the segment services under test never call
        // CanViewActivityAsync (a photo/race-only gate); this double models segment/activity scoping only.
        public Task<bool> CanViewActivityAsync(TrackId trackId) => Task.FromResult(true);
    }
}
