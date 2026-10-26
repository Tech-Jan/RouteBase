using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests;

/// <summary>
/// Builds the three split segment services (<see cref="SegmentCrud"/> / <see cref="SegmentAnalysis"/> /
/// <see cref="SegmentEfforts"/>) over a SHARED <see cref="SegmentCache"/>, mirroring how DI wires them so
/// a Crud delete still evicts the Efforts read-model's cache keys. Replaces the old single-object
/// <c>new SegmentService(...)</c> construction in tests.
/// </summary>
public sealed record SegmentServices(SegmentCrud Crud, SegmentAnalysis Analysis, SegmentEfforts Efforts, SegmentCache Cache, HeatmapTilePackStore TileStore)
{
    public static SegmentServices Create(
        RouteBaseDbContext context,
        IMemoryCache cache,
        IVisibilityService visibility,
        UserCacheInvalidator invalidator,
        IProfileSettingsService settings,
        ISegmentMatchStrategy shapeMatcher,
        IMatchEngine matchEngine,
        IEffortMath effortMath,
        ITrackReader trackReader)
    {
        var segCache = new SegmentCache(cache, visibility, invalidator);
        // Heatmap tile store + lifecycle owner (B2) shared by all three services, mirroring DI. Rooted at
        // a throwaway temp path; most tests don't generate heatmaps, so Delete on a missing file is a no-op.
        var tileStore = new HeatmapTilePackStore(
            new HeatmapStorageOptions { Root = Path.Combine(Path.GetTempPath(), $"hmtest_{Guid.NewGuid():N}") },
            new HeatmapTileIndexCache(cache), TimeProvider.System);
        var heatmaps = new HeatmapLifecycle(context, visibility, tileStore, NullLogger<HeatmapLifecycle>.Instance);
        var crud     = new SegmentCrud(context, cache, NullLogger<SegmentCrud>.Instance, visibility, segCache, heatmaps, settings, TimeProvider.System);
        var analysis = new SegmentAnalysis(context, NullLogger<SegmentAnalysis>.Instance, visibility, segCache,
            shapeMatcher, matchEngine, effortMath, trackReader, heatmaps, settings);
        // The REAL builder, not a fake — it carries the GPS→wire math the effort tests assert on, and
        // mirroring DI here is what keeps those assertions meaningful.
        var perfBuilder = new SegmentEffortPerformanceBuilder(context, cache, segCache, settings, trackReader);
        var efforts  = new SegmentEfforts(context, cache, NullLogger<SegmentEfforts>.Instance, visibility, segCache,
            trackReader, perfBuilder, heatmaps, TimeProvider.System);
        return new SegmentServices(crud, analysis, efforts, segCache, tileStore);
    }
}

/// <summary>
/// Test double for IProfileSettingsService: every read returns the caller's own default, which is the
/// real service's behaviour for a user with no saved settings. Use this rather than passing null! —
/// SegmentCrud reads DefaultVisibility on create, so a null settings service NREs.
///
/// <see cref="Override"/> models the other half — a user who HAS saved a value — for the tests that need
/// to exercise both sides of a setting.
/// </summary>
public sealed class TestProfileSettingsService : IProfileSettingsService
{
    private readonly Dictionary<(string, string), object> _overrides = new();

    /// <summary>Pin a stored value for one setting; later reads return it instead of the caller's default.</summary>
    public TestProfileSettingsService Override(string category, string key, object value)
    {
        _overrides[(category, key)] = value;
        return this;
    }

    public Task<T?> GetSettingAsync<T>(string category, string key, string? userId = null, T? defaultValue = default)
        => Task.FromResult(_overrides.TryGetValue((category, key), out var stored) ? (T?)stored : defaultValue);
    public Task<string?> GetSettingAsync(string category, string key, string? userId = null)
        => Task.FromResult<string?>(null);
    public Task<bool> SetSettingAsync(string category, string key, object value, string userId)
        => throw new NotSupportedException();
    public Task SetSettingsAsync(IReadOnlyList<(string Category, string Key, object Value)> settings, string userId)
        => throw new NotSupportedException();
    // Reads only — no fake here stores anything a delete could remove.
    public Task<bool> DeleteSettingAsync(string category, string key, string userId) =>
        Task.FromResult(false);

    public Task<Dictionary<string, string>> GetCategorySettingsAsync(string category, string? userId = null)
        => Task.FromResult(new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>
/// Test double for ICurrentUserService that returns a fixed user ID.
/// Used in service tests where no HttpContext is available.
/// </summary>
public class TestCurrentUserService : ICurrentUserService
{
    public string UserId { get; }

    public TestCurrentUserService(string userId = "test-user")
    {
        UserId = userId;
    }

    public string GetUserId() => UserId;
    public string? TryGetUserId() => UserId;
    public string? GetDisplayName() => UserId;
    public bool IsAdmin() => false;
    public void SetUserIdOverride(string userId) { /* no-op in tests */ }
    public IDisposable BeginUserScope(string userId) => NoopUserScope.Instance;
}

/// <summary>
/// Scope handle for <see cref="ICurrentUserService"/> fakes that return a FIXED id and so cannot model
/// impersonation. Disposing is a no-op because there is nothing to restore — fakes that do track an id
/// (the queue-worker stubs) implement a real restoring scope instead, matching production semantics.
/// </summary>
public sealed class NoopUserScope : IDisposable
{
    public static readonly NoopUserScope Instance = new();
    public void Dispose() { }
}

/// <summary>
/// Test double for ILiveSessionDirectory: nobody is live. Satisfies the RecordingApiController
/// constructor for the tests that exercise other endpoints; the directory has its own tests.
/// </summary>
public class TestLiveSessionDirectory : ILiveSessionDirectory
{
    public Task<List<LiveSessionInfo>> GetVisibleAsync() => Task.FromResult(new List<LiveSessionInfo>());

    public Task<List<LiveSessionInfo>> ResolveAsync(IEnumerable<string> sessionIds)
        => Task.FromResult(new List<LiveSessionInfo>());
}

/// <summary>
/// Test double for IVisibilityService that scopes queries to a fixed user ID.
/// Wraps TestCurrentUserService so behaviour mirrors the real VisibilityService.
/// </summary>
public class TestVisibilityService : IVisibilityService
{
    private readonly string _userId;

    public TestVisibilityService(string userId = "test-user")
    {
        _userId = userId;
    }

    public string GetCurrentUserId() => _userId;

    // Mirrors VisibilityService: the lifecycle gate first, then owner scoping. Only a RELEASED activity is
    // a real one — a trashed or parked ride must be invisible here exactly as it is in production.
    public IQueryable<Activity> VisibleActivities(IQueryable<Activity> query)
        => query.Where(a => a.Lifecycle == LifecycleStates.Released && a.UserId == _userId);

    public IQueryable<Segment> VisibleSegments(IQueryable<Segment> query)
        => query.Where(s => s.UserId == _userId);

    public IQueryable<MultiDayTour> VisibleTours(IQueryable<MultiDayTour> query)
        => query.Where(t => t.UserId == _userId);

    public IQueryable<Activity> OwnedActivities(IQueryable<Activity> query)
        => query.Where(a => a.UserId == _userId);

    public IQueryable<Segment> OwnedSegments(IQueryable<Segment> query)
        => query.Where(s => s.UserId == _userId);

    public IQueryable<MultiDayTour> OwnedTours(IQueryable<MultiDayTour> query)
        => query.Where(t => t.UserId == _userId);

    public IQueryable<AppCredentials> OwnedCredentials(IQueryable<AppCredentials> query)
        => query.Where(c => c.UserId == _userId);

    public Task<List<string>> GetVisibleUserIdsAsync()
        => Task.FromResult(new List<string> { _userId });

    public Task<bool> CanViewItemAsync(string ownerUserId, string? visibility)
        => Task.FromResult(ownerUserId == _userId || visibility == "public");

    /// <summary>
    /// The activity view gate (the photo and race-participant surfaces consult it). Permissive by DEFAULT
    /// to match this double's own-only scoping, but now SEEDABLE: unlike the old unconditional <c>true</c>
    /// that no test could make refuse, a suite that actually reaches the gate can point this at a predicate
    /// to drive the deny branch. The race slice IS such a reach — RaceParticipantCrud.AddRealParticipantAsync
    /// and RaceAccess.GetRaceAsync gate real riders on it — and RaceCrudTests/RaceImportServiceTests wire
    /// this double into it. Default keeps every existing consumer green; suites with a DbContext still
    /// prefer the real VisibilityService, which models the gate against seeded rows.
    /// </summary>
    public Func<int, bool> CanViewActivity { get; set; } = _ => true;
    public Task<bool> CanViewActivityAsync(TrackId trackId) => Task.FromResult(CanViewActivity(trackId.Value));
}
