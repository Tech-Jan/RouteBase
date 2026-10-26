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
using RouteBaseWeb.Tests.Helpers;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// Integration tests for the live-recording pipeline:
/// RecordingService → RecordingFinalizationService → Activity stats.
///
/// Uses a named in-memory SQLite database so raw SQL, last_insert_rowid(),
/// and FK constraints behave identically to production.
/// </summary>
public partial class RecordingPipelineTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private ServiceProvider _provider = null!;
    private ActivityImportService _importService = null!;
    private RecordingService _recordingService = null!;
    private RecordingFinalizationService _finalizationService = null!;
    private UserCacheInvalidator _finalizeInvalidator = null!;
    private ContentTrackReader _reader = null!;
    private string _connStr = null!;
    /// <summary>Finalize and approve both enqueue the incremental segment match — captured, not queued, so a
    /// test can assert WHICH track ids were queued and why (OperationQueue exposes no payload).</summary>
    private CapturingDeriveQueue Derives { get; } = new();

    /// <summary>Settings the finalize path reads. Defaults to production defaults (auto-approve ON); a test
    /// that needs the review queue overrides it before finalizing.</summary>
    private TestProfileSettingsService Settings { get; } = new();

    private const string UserId = "test-user";

    public async Task InitializeAsync()
    {
        var dbName = $"recording_test_{Guid.NewGuid():N}";
        var connStr = $"Data Source=file:{dbName}?mode=memory&cache=shared";
        _connStr = connStr;
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connStr).Options;

        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schemaManager = new DbSchemaManager(_context);
        await schemaManager.EnsureActivityBboxTableAsync();
        await schemaManager.EnsureTrackContentTablesAsync();
        await schemaManager.EnsureRecordingStagingTableAsync();
        await schemaManager.EnsureTrackEditTablesAsync(); // ActivityCommandService's child-table delete seam touches Track_Blob_History

        // Seed a user row to satisfy the Activity.user_id FK constraint.
        _context.Users.Add(new AppUser
        {
            UserId = UserId,
            DisplayName = "Test User",
            Email = "test@test.com",
            IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        // Blob-backed reader (staging-aware) over the same shared-cache DB, matching production wiring.
        _provider = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddDbContext<RouteBaseDbContext>(o => o.UseSqlite(connStr))
            .AddScoped<ITrackObjectStore, TrackObjectStore>()
            .AddScoped<TrackContentStore>()
            .AddScoped<RecordingStagingStore>()
            .BuildServiceProvider();
        var reader = new ContentTrackReader(
            new TrackReaderCache(64L * 1024 * 1024), new SingleFlight(),
            _provider.GetRequiredService<IServiceScopeFactory>());
        _reader = reader;
        var content = new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System);
        var stagingStore = new RecordingStagingStore(_context);

        _importService = new ActivityImportService(
            _context, content, NullLogger<ActivityImportService>.Instance);

        var commandVis   = new TestVisibilityService(UserId);
        var commandInval = new UserCacheInvalidator();
        var commandService = new ActivityCommandService(
            _context, commandVis, commandInval,
            NullLogger<ActivityCommandService>.Instance,
            TestLifecycle.For(_context, commandVis, commandInval));

        _recordingService = new RecordingService(
            _context, _importService, stagingStore, reader, commandService,
            new RecordingFinalizationCoordinator(new SingleFlight(), _provider.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<RecordingService>.Instance, TimeProvider.System);

        // Same _context as the finalizer so the adjudicator shares its ambient finalize transaction
        // (mirrors DI, where both scoped services resolve one DbContext).
        var reviewVis   = new TestVisibilityService(UserId);
        var reviewInval = new UserCacheInvalidator();
        var reviewLifecycle = TestLifecycle.For(_context, reviewVis, reviewInval);
        var reviewCommand = new ActivityCommandService(
            _context, reviewVis, reviewInval, NullLogger<ActivityCommandService>.Instance, reviewLifecycle);
        var autoReview = new AutoReviewService(
            _context, reviewVis, reviewLifecycle, reviewCommand, Derives, Settings,
            NullLogger<AutoReviewService>.Instance, TimeProvider.System);

        _finalizeInvalidator = reviewInval;
        _finalizationService = new RecordingFinalizationService(
            _context,
            _importService,
            commandService,
            new NoOpElevationService(),
            new GpsNormalizationService(),
            new GeoTimeZoneActivityResolver(NullLogger<GeoTimeZoneActivityResolver>.Instance),
            reader,
            content,
            stagingStore,
            autoReview,
            reviewInval,
            Derives,
            NullLogger<RecordingFinalizationService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // During recording, points sit in Recording_Staging; after finalize they are compacted into Track_Blob.
    private async Task<int> StagingCountAsync(TrackId trackId) =>
        (await _context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Recording_Staging WHERE track_ID = {0}", trackId.Value)
            .ToListAsync()).First();

    private async Task<int> BlobPointCountAsync(TrackId trackId) =>
        (await _context.Database
            .SqlQueryRaw<int>("SELECT COALESCE((SELECT point_count FROM Activity_Version WHERE track_id = {0} AND is_head = 1), 0) AS Value", trackId.Value)
            .ToListAsync()).First();

    private async Task<int> BlobRowCountAsync(TrackId trackId) =>
        (await _context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Activity_Version WHERE track_id = {0} AND is_head = 1", trackId.Value)
            .ToListAsync()).First();

    // A finalization service over the same _context but with a custom (fault-injecting) import service —
    // exercises RF-01's write-phase transaction rollback/retry.
    private RecordingFinalizationService NewFinalizationServiceWith(IActivityImportService import)
    {
        var vis   = new TestVisibilityService(UserId);
        var inval = new UserCacheInvalidator();
        var lifecycle = TestLifecycle.For(_context, vis, inval);
        var command = new ActivityCommandService(_context, vis, inval,
            NullLogger<ActivityCommandService>.Instance, lifecycle);
        return new(_context, import,
            command,
            new NoOpElevationService(), new GpsNormalizationService(),
            new GeoTimeZoneActivityResolver(NullLogger<GeoTimeZoneActivityResolver>.Instance),
            _reader, new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System), new RecordingStagingStore(_context),
            new AutoReviewService(_context, vis, lifecycle, command, Derives, Settings,
                NullLogger<AutoReviewService>.Instance, TimeProvider.System),
            inval,
            Derives,
            NullLogger<RecordingFinalizationService>.Instance);
    }

    // A finalization service over the same _context but with a custom elevation service — lets a test
    // inject a side effect (e.g. a late staging append) at the DEM-fetch point in FinalizeAsync (SVC-02).
    private RecordingFinalizationService NewFinalizationServiceWithElevation(
        IElevationService elevation, ITrackReader? reader = null)
    {
        var vis   = new TestVisibilityService(UserId);
        var inval = new UserCacheInvalidator();
        var lifecycle = TestLifecycle.For(_context, vis, inval);
        var command = new ActivityCommandService(_context, vis, inval,
            NullLogger<ActivityCommandService>.Instance, lifecycle);
        return new(_context, _importService,
            command,
            elevation, new GpsNormalizationService(),
            new GeoTimeZoneActivityResolver(NullLogger<GeoTimeZoneActivityResolver>.Instance),
            reader ?? _reader, new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System), new RecordingStagingStore(_context),
            new AutoReviewService(_context, vis, lifecycle, command, Derives, Settings,
                NullLogger<AutoReviewService>.Instance, TimeProvider.System),
            inval,
            Derives,
            NullLogger<RecordingFinalizationService>.Instance);
    }

    // Per-point staged cumulative distance, ordered by seq (RS-01 boundary-leg assertions).
    private async Task<List<double>> StagedDistancesAsync(TrackId trackId) =>
        (await _context.Database
            .SqlQueryRaw<double>("SELECT distance AS Value FROM Recording_Staging WHERE track_ID = {0} ORDER BY seq", trackId.Value)
            .ToListAsync());

    // Decoded blob Distance channel (RS-01 finalize-rebuild assertions).
    private async Task<List<double>> BlobDistancesAsync(TrackId trackId)
    {
        var track = await _reader.GetTrackAsync(trackId);
        return Enumerable.Range(0, track!.Count).Select(i => track.Distance[i]).ToList();
    }

    // Staged seq ordinals ordered ascending (RS-04 dense-seq / no-collision assertions).
    private async Task<List<long>> StagedSeqsAsync(TrackId trackId) =>
        (await _context.Database
            .SqlQueryRaw<long>("SELECT seq AS Value FROM Recording_Staging WHERE track_ID = {0} ORDER BY seq", trackId.Value)
            .ToListAsync());

    // A RecordingService over its OWN DbContext on the same shared-cache DB — mirrors production request
    // scoping so RS-04's process-wide striped lock is what actually serializes two racing appends.
    private RecordingService NewRecordingServiceOnFreshContext(IActivityImportService? importOverride = null)
    {
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_connStr).Options;
        var ctx = new RouteBaseDbContext(options);
        var content = new TrackContentStore(ctx, new TrackObjectStore(ctx, TimeProvider.System), TimeProvider.System);
        var stagingStore = new RecordingStagingStore(ctx);
        var importService = importOverride
            ?? new ActivityImportService(ctx, content, NullLogger<ActivityImportService>.Instance);
        var ctxVis   = new TestVisibilityService(UserId);
        var ctxInval = new UserCacheInvalidator();
        var commandService = new ActivityCommandService(
            ctx, ctxVis, ctxInval,
            NullLogger<ActivityCommandService>.Instance,
            TestLifecycle.For(ctx, ctxVis, ctxInval));
        return new RecordingService(
            ctx, importService, stagingStore, _reader, commandService,
            new RecordingFinalizationCoordinator(new SingleFlight(), _provider.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<RecordingService>.Instance, TimeProvider.System);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static StartRecordingRequest MakeStartRequest(
        string activityType = "Run",
        string trackName = "Test Run",
        string visibility = "friends",
        bool autoDetected = false) =>
        new()
        {
            ActivityType = activityType,
            TrackName = trackName,
            Visibility = visibility,
            ClientVersion = "test-1.0",
            AutoDetected = autoDetected,
        };

    // ~0.00004° lat ≈ 4.5 m per point at 1 s spacing ≈ 4.5 m/s — a realistic on-foot pace that
    // matches the declared Speed and stays under the 40 m/s teleport-rejection gate in
    // GpsNormalizationService (0.001°/s ≈ 111 m/s would be stripped as a GPS teleport).
    private const double PointStepLat = 0.00004;

    /// <summary>
    /// Build a straight-line track of <paramref name="count"/> GPS points starting at (lat0, lng0),
    /// spaced ~4.5 m apart each second (see <see cref="PointStepLat"/>). Altitude rises 10 m per point;
    /// speed is 5 m/s throughout. <paramref name="startSecond"/> offsets the timestamps so a second
    /// batch continues the first in time instead of repeating it (keeps the concatenated track monotonic).
    /// </summary>
    private static List<RecordingGpsPoint> MakeGpsPoints(
        int count,
        double lat0 = 47.0,
        double lng0 = 11.0,
        double alt0 = 1000.0,
        double speed = 5.0,
        int startSecond = 0)
    {
        var baseUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count)
            .Select(i => new RecordingGpsPoint
            {
                Lat          = lat0 + i * PointStepLat,
                Lng          = lng0,
                Altitude     = alt0 + i * 10.0,
                Speed        = speed,
                TimestampUtc = baseUtc.AddSeconds(startSecond + i),
            })
            .ToList();
    }

    private static RecordingPointsBatch MakeBatch(List<RecordingGpsPoint> points, int seq = 1) =>
        new() { Points = points, ClientSequence = seq };

}

/// <summary>No-op elevation service for tests — leaves GPS altitude untouched.</summary>
internal sealed class NoOpElevationService : IElevationService
{
    public Task<int> ApplyDemElevationAsync(List<GpsData> points) => Task.FromResult(0);
}

/// <summary>Elevation service that appends a fresh staging batch as a side effect of the DEM fetch —
/// simulates the CONC-P2 race: a live GPS batch that passed the 'recording' gate before stop committed lands
/// in the un-transacted DEM window, ABOVE the watermark FinalizeAsync snapshotted. Fires ONCE (like a real
/// bounded in-flight batch: the session has left 'recording', so no further append can start), so the
/// finalize retry converges after re-reading — exactly as a real recording settles after stop.</summary>
internal sealed class LateAppendingElevationService : IElevationService
{
    private readonly RecordingStagingStore _staging;
    private readonly int _trackId;
    private readonly List<ExternalGpsPoint> _latePoints;
    private readonly bool _fireOnce;
    private bool _fired;

    public LateAppendingElevationService(
        RecordingStagingStore staging, TrackId trackId, List<ExternalGpsPoint> latePoints, bool fireOnce = true)
    {
        _staging = staging;
        _trackId = trackId.Value;
        _latePoints = latePoints;
        _fireOnce = fireOnce;
    }

    public async Task<int> ApplyDemElevationAsync(List<GpsData> points)
    {
        if (_fireOnce && _fired) return 0;
        _fired = true;
        var startSeq = await _staging.CountAsync(new TrackId(_trackId));
        await _staging.AppendAsync(new TrackId(_trackId), startSeq, _latePoints);
        return 0;
    }
}

/// <summary>Track reader that lands a staging batch DURING its first read — the CONC-P1 window, which sits
/// earlier than <see cref="LateAppendingElevationService"/>'s: these points are read into the compacted set
/// but were invisible to a watermark snapshotted before the read, so the old code wrote them to the blob and
/// left them in staging, and every later read re-appended them.</summary>
internal sealed class MidReadAppendingTrackReader : ITrackReader
{
    private readonly ITrackReader _inner;
    private readonly RecordingStagingStore _staging;
    private readonly int _trackId;
    private readonly List<ExternalGpsPoint> _latePoints;
    private bool _fired;

    public MidReadAppendingTrackReader(
        ITrackReader inner, RecordingStagingStore staging, TrackId trackId, List<ExternalGpsPoint> latePoints)
    {
        _inner = inner;
        _staging = staging;
        _trackId = trackId.Value;
        _latePoints = latePoints;
    }

    public async Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default)
    {
        // Once only: the retry must be able to converge, exactly as a real recording settles after stop.
        if (!_fired)
        {
            _fired = true;
            var startSeq = await _staging.CountAsync(new TrackId(_trackId));
            await _staging.AppendAsync(new TrackId(_trackId), startSeq, _latePoints);
        }
        return await _inner.GetTrackAsync(trackId, ct);
    }
}

/// <summary>Import service whose bbox upsert throws — fault injection for RS-05's atomic-append test.
/// The point-batch overload is called mid-append (after the staging INSERT); everything else is unused.</summary>
internal sealed class ThrowingBboxImportService : IActivityImportService
{
    public Task UpsertActivityBboxAsync(TrackId trackId, IReadOnlyList<ExternalGpsPoint> points) =>
        throw new InvalidOperationException("bbox write failed (injected)");
    public Task UpsertActivityBboxAsync(TrackId trackId) => throw new NotSupportedException();
    public Task<ExistingActivity?> FindExistingAsync(long externalId, string source, string userId) => throw new NotSupportedException();
    public Task<Activity?> ImportFromSummaryAsync(
        ExternalActivitySummary summary, List<ExternalGpsPoint> gpsPoints, string userId) => throw new NotSupportedException();
    public Task WriteTrackBlobAsync(TrackId trackId, List<ExternalGpsPoint> points) => throw new NotSupportedException();
    public double ComputeTotalDescent(List<ExternalGpsPoint> points) => throw new NotSupportedException();
}
