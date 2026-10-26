using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Integration tests for <see cref="ActivityImportService"/>.
/// Uses a named in-memory SQLite database (not the EF in-memory provider)
/// so raw SQL, transactions, and last_insert_rowid() behave identically to production.
///
///</summary>
public class ActivityImportServiceTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private ActivityImportService _sut = null!;

    public async Task InitializeAsync()
    {
        var dbName = $"import_test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;

        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        // Activity_Bbox + Track_Blob are not in the EF model — create them via the production schema helper.
        var schemaManager = new DbSchemaManager(_context);
        await schemaManager.EnsureActivityBboxTableAsync();
        await schemaManager.EnsureTrackContentTablesAsync();

        // Seed a user row to satisfy the Activity.user_id FK constraint.
        _context.Users.Add(new AppUser
        {
            UserId      = "test-user",
            DisplayName = "Test User",
            Email       = "test@test.com",
            IsAdmin     = false,
            CreatedAt   = DateTime.UtcNow.ToString("O"),
            LastLogin   = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        _sut = new ActivityImportService(
            _context, new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System), NullLogger<ActivityImportService>.Instance);
    }

    // Points now live packed in Track_Blob; the point_count column mirrors the encoded count (0 if no blob).
    private async Task<int> BlobPointCountAsync(TrackId trackId)
    {
        var rows = await _context.Database
            .SqlQueryRaw<int>("SELECT COALESCE((SELECT point_count FROM Activity_Version WHERE track_id = {0} AND is_head = 1), 0) AS Value", trackId.Value)
            .ToListAsync();
        return rows.FirstOrDefault();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ExternalActivitySummary MakeSummary(long externalId = 1001, string source = "strava") =>
        new()
        {
            ExternalId    = externalId,
            ProviderId    = source,
            Source        = source,
            Name          = "Morning Ride",
            ActivityType  = "Ride",
            StartDateLocal = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Unspecified),
            StartDateUtc  = new DateTime(2024, 6, 1, 6, 0, 0, DateTimeKind.Utc),
            Distance      = 50_000,
            ElapsedTime   = 7200,
            MovingTime    = 7000,
            ElevationGain = 500,
            AverageSpeed  = 7.0,
            MaxSpeed      = 15.0,
            Calories      = 1200,
        };

    private static readonly DateTime BaseUtc = new(2024, 6, 1, 6, 0, 0, DateTimeKind.Utc);

    private static List<ExternalGpsPoint> MakeGpsPoints(int count = 5) =>
        Enumerable.Range(0, count)
            .Select(i => new ExternalGpsPoint
            {
                Lat      = 47.0 + (i % 1000) * 0.001,
                Lng      = 11.0 + (i % 1000) * 0.001,
                Altitude = 1000.0 + i * 10,
                Distance = i * 100.0,
                Speed    = 5.0,
                TimestampUtc   = BaseUtc.AddSeconds(i),
            })
            .ToList();

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportFromSummaryAsync_ReturnsActivity_WithNonZeroTrackId()
    {
        var result = await _sut.ImportFromSummaryAsync(MakeSummary(), [], "test-user");

        Assert.NotNull(result);
        Assert.True(result.TrackId.Value > 0);
    }

    [Fact]
    public async Task ImportFromSummaryAsync_ReturnsActivity_WithCorrectApiId()
    {
        var result = await _sut.ImportFromSummaryAsync(MakeSummary(externalId: 9999), [], "test-user");

        Assert.NotNull(result);
        Assert.Equal(9999L, result.ApiId);
    }

    [Fact]
    public async Task ImportFromSummaryAsync_PersistsScalarFields()
    {
        var summary = MakeSummary(externalId: 42);
        var result  = await _sut.ImportFromSummaryAsync(summary, [], "test-user");

        var persisted = await _context.Activities.SingleAsync(a => a.TrackId == result!.TrackId);

        Assert.Equal(42L,            persisted.ApiId);
        Assert.Equal("Morning Ride", persisted.TrackName);
        Assert.Equal("Ride",         persisted.ActivityType);
        Assert.Equal(50_000,         persisted.TotalDistance);
        Assert.Equal(500,            persisted.TotalAscent);
        Assert.Equal(1200,           persisted.Calories);
        Assert.Equal("strava",       persisted.Source);
    }

    [Fact]
    public async Task ImportFromSummaryAsync_WithNoGpsPoints_ActivityIsCreated()
    {
        var result = await _sut.ImportFromSummaryAsync(MakeSummary(), [], "test-user");

        Assert.NotNull(result);
        var gpsCount = await BlobPointCountAsync(result.TrackId);
        Assert.Equal(0, gpsCount);
    }

    [Fact]
    public async Task ImportFromSummaryAsync_WithGpsPoints_PersistsCorrectGpsCount()
    {
        var gpsPoints = MakeGpsPoints(count: 7);
        var result    = await _sut.ImportFromSummaryAsync(MakeSummary(), gpsPoints, "test-user");

        var gpsCount = await BlobPointCountAsync(result!.TrackId);
        Assert.Equal(7, gpsCount);
    }

    [Fact]
    public async Task ImportFromSummaryAsync_ComputesTotalDescent_FromAltitudeStream()
    {
        // Descending altitude: 1000 → 900 → 800 → descent = 200
        var gpsPoints = new List<ExternalGpsPoint>
        {
            new() { Lat = 47.0, Lng = 11.0, Altitude = 1000 },
            new() { Lat = 47.001, Lng = 11.0, Altitude = 900 },
            new() { Lat = 47.002, Lng = 11.0, Altitude = 800 },
        };
        var summary = MakeSummary();
        summary.TotalDescent = null; // force computation from GPS

        var result    = await _sut.ImportFromSummaryAsync(summary, gpsPoints, "test-user");
        var persisted = await _context.Activities.SingleAsync(a => a.TrackId == result!.TrackId);

        Assert.Equal(200.0, persisted.TotalDescent);
    }

    [Fact]
    public async Task ImportFromSummaryAsync_UsesSummaryTotalDescent_WhenPrecomputed()
    {
        var gpsPoints = new List<ExternalGpsPoint>
        {
            new() { Lat = 47.0, Lng = 11.0, Altitude = 1000 },
            new() { Lat = 47.001, Lng = 11.0, Altitude = 900 },
        };
        var summary = MakeSummary();
        summary.TotalDescent = 999.0; // pre-computed value should win

        var result    = await _sut.ImportFromSummaryAsync(summary, gpsPoints, "test-user");
        var persisted = await _context.Activities.SingleAsync(a => a.TrackId == result!.TrackId);

        Assert.Equal(999.0, persisted.TotalDescent);
    }

    [Fact]
    public async Task ImportFromSummaryAsync_WithGpsPoints_UpsertsBboxRow()
    {
        var gpsPoints = new List<ExternalGpsPoint>
        {
            new() { Lat = 47.0,   Lng = 11.0 },
            new() { Lat = 47.005, Lng = 11.01 },
            new() { Lat = 47.010, Lng = 11.005 },
        };
        var result = await _sut.ImportFromSummaryAsync(MakeSummary(), gpsPoints, "test-user");

        var bboxRows = await _context.Database
            .SqlQueryRaw<BboxRow>(
                "SELECT min_lat, max_lat, min_long, max_long FROM Activity_Bbox WHERE track_ID = {0}",
                result!.TrackId.Value)
            .ToListAsync();

        Assert.Single(bboxRows);
        Assert.Equal(47.0,   bboxRows[0].min_lat,  precision: 6);
        Assert.Equal(47.010, bboxRows[0].max_lat,  precision: 6);
        Assert.Equal(11.0,   bboxRows[0].min_long, precision: 6);
        Assert.Equal(11.01,  bboxRows[0].max_long, precision: 6);
    }

    /// <summary>
    /// The content-based overload runs when content IS the truth (finalize after outlier rejection,
    /// post-import guard) — it must REWRITE the bbox exactly, not expand-only merge. Expand-only left a
    /// live-append GPS teleport's inflated box in place forever (permanent false candidate for segment
    /// matching / shape stage-1 / photo bbox candidates) after finalize dropped the outlier point.
    /// </summary>
    [Fact]
    public async Task UpsertActivityBbox_FromContent_ShrinksAnInflatedRow()
    {
        var gpsPoints = new List<ExternalGpsPoint>
        {
            new() { Lat = 47.0,   Lng = 11.0 },
            new() { Lat = 47.010, Lng = 11.01 },
        };
        var result = await _sut.ImportFromSummaryAsync(MakeSummary(), gpsPoints, "test-user");

        // Simulate the live-append teleport: the streaming path expanded the box far beyond the content.
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity_Bbox SET min_lat = 40.0, max_lat = 55.0, min_long = 2.0, max_long = 20.0 " +
            "WHERE track_ID = {0}", result!.TrackId.Value);

        await _sut.UpsertActivityBboxAsync(result.TrackId);

        var bbox = (await _context.Database
            .SqlQueryRaw<BboxRow>(
                "SELECT min_lat, max_lat, min_long, max_long FROM Activity_Bbox WHERE track_ID = {0}",
                result.TrackId.Value)
            .ToListAsync()).Single();
        Assert.Equal(47.0,   bbox.min_lat,  precision: 6);
        Assert.Equal(47.010, bbox.max_lat,  precision: 6);
        Assert.Equal(11.0,   bbox.min_long, precision: 6);
        Assert.Equal(11.01,  bbox.max_long, precision: 6);
    }

    [Fact]
    public async Task FindExistingAsync_ReturnsNull_BeforeImport()
    {
        Assert.Null(await _sut.FindExistingAsync(externalId: 12345, source: "strava", userId: "test-user"));
    }

    [Fact]
    public async Task FindExistingAsync_ReturnsTheActivity_AfterImport()
    {
        await _sut.ImportFromSummaryAsync(MakeSummary(externalId: 777), [], "test-user");

        var existing = await _sut.FindExistingAsync(externalId: 777, source: "strava", userId: "test-user");

        Assert.NotNull(existing);
        Assert.Equal(LifecycleStates.Released, existing!.Lifecycle);
    }

    [Fact]
    public async Task FindExistingAsync_ReturnsNull_ForDifferentSource()
    {
        await _sut.ImportFromSummaryAsync(MakeSummary(externalId: 888, source: "strava"), [], "test-user");

        // Same ID, different source — should not match
        Assert.Null(await _sut.FindExistingAsync(externalId: 888, source: "garmin", userId: "test-user"));
    }

    /// <summary>
    /// The bug this replaced: re-importing a ride that a merge swallowed said "duplicate", which reads as
    /// "you already have this" — when in fact the ride was in the trash and one click from coming back. The
    /// import is still BLOCKED (re-importing it would double-count its distance); only the message changes,
    /// and it now names the ride so it can be found again.
    /// </summary>
    [Fact]
    public async Task FindExistingAsync_TrashedRide_ReportsInTrash_NotDuplicate()
    {
        var imported = await _sut.ImportFromSummaryAsync(MakeSummary(externalId: 999), [], "test-user");
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity SET lifecycle = 'trashed', lifecycle_reason = 'edit-merge' WHERE track_ID = {0}",
            imported!.TrackId.Value);

        var existing = await _sut.FindExistingAsync(externalId: 999, source: "strava", userId: "test-user");

        Assert.NotNull(existing);
        Assert.Equal(ImportSkipReason.InTrash, ImportSkipReason.For(existing!));
        Assert.Contains("in the trash", ImportSkipReason.Message(existing!), StringComparison.Ordinal);
        Assert.Contains("merged into another ride", ImportSkipReason.Message(existing!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFromSummaryAsync_PersistsTimeTotal_FromTotalTime()
    {
        // GI-02: time_total now prefers device timer time (TotalTime) over ElapsedTime.
        var summary = MakeSummary(externalId: 555);
        summary.TotalTime   = 7100;
        summary.ElapsedTime = 7200;
        summary.MovingTime  = 7000;

        var result    = await _sut.ImportFromSummaryAsync(summary, [], "test-user");
        var persisted = await _context.Activities.SingleAsync(a => a.TrackId == result!.TrackId);

        Assert.Equal(7100, persisted.TimeTotal);
        Assert.Equal(7200, persisted.TimeElapsed);
        Assert.Equal(7000, persisted.TimeMoving);
    }

    [Fact]
    public async Task ImportFromSummaryAsync_TimeTotal_FallsBackToElapsed_WhenTotalTimeNull()
    {
        // GI-02: TotalTime ?? ElapsedTime — Strava path (TotalTime null) unchanged.
        var summary = MakeSummary(externalId: 556);
        summary.TotalTime   = null;
        summary.ElapsedTime = 3600;

        var result    = await _sut.ImportFromSummaryAsync(summary, [], "test-user");
        var persisted = await _context.Activities.SingleAsync(a => a.TrackId == result!.TrackId);

        Assert.Equal(3600, persisted.TimeTotal);
    }

    [Fact]
    public async Task WriteTrackBlob_RoundTripsCoordinates()
    {
        // The former geo_cell-inline pin is obsolete (geo_cell is gone — the engine hashes cells in C#).
        // Replacement blob-decode assert: the funnel packs points into Track_Blob and they decode back to
        // their (quantization-exact) coordinates, including negative ones. Ordinals are dense 0..n-1.
        var gpsPoints = new List<ExternalGpsPoint>
        {
            new() { Lat = 47.2692,  Lng = 11.4041 },
            new() { Lat = -33.8688, Lng = 151.2093 },
            new() { Lat = 40.7128,  Lng = -74.0060 },
        };
        var result = await _sut.ImportFromSummaryAsync(MakeSummary(externalId: 557), gpsPoints, "test-user");

        var blob = await new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System).GetHeadDataAsync(result!.TrackId);
        Assert.NotNull(blob);
        var decoded = TrackBlobCodec.Decode(blob!);

        Assert.Equal(3, decoded.Count);
        for (int i = 0; i < gpsPoints.Count; i++)
        {
            Assert.Equal(TrackBlobCodec.RoundTripValue(gpsPoints[i].Lat, TrackBlobCodec.LatScale), decoded.Lat[i]);
            Assert.Equal(TrackBlobCodec.RoundTripValue(gpsPoints[i].Lng, TrackBlobCodec.LngScale), decoded.Lng[i]);
        }
    }

    [Fact]
    public async Task ImportFromSummaryAsync_LargeGpsList_AllPointsInserted()
    {
        // 1 500 points: a large track packed into one Track_Blob (was two GPS insert batches)
        var gpsPoints = MakeGpsPoints(count: 1_500);
        var result    = await _sut.ImportFromSummaryAsync(MakeSummary(), gpsPoints, "test-user");

        var count = await BlobPointCountAsync(result!.TrackId);
        Assert.Equal(1_500, count);
    }

    // Projection type for the raw Activity_Bbox query
    private record BboxRow(double min_lat, double max_lat, double min_long, double max_long);
}
