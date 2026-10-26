using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
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
/// Item 25 (effort-exclusion list): RemoveEffortAsync no longer hard-deletes — it inserts a durable
/// SegmentEffortExclusion row keyed (segment_id, track_id, entry_index) that every read consumer
/// (totals, counts, detailed list flag, effort-cost chart data, heatmap paint) anti-joins out, so the
/// removal survives re-analysis. Restore deletes the row. Exclusion/restore are owner-only; exclusion
/// rows have no FK cascade and are purged explicitly at the segment-delete / track-delete / user-reset
/// seams. Runs the REAL match engine so the survives-re-analysis pin exercises the actual wipe+rebuild.
/// </summary>
public class SegmentEffortExclusionTests : IAsyncLifetime
{
    private const string Me    = "test-user"; // TestVisibilityService default — segment + track owner
    private const string Other = "other-user";

    private RouteBaseDbContext _context = null!;
    private SegmentServices _svc = null!;       // visibility = Me
    private SegmentEfforts _effortsAsOther = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:efexcl_{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync(); // EF entity creates SegmentEffortExclusion

        var schema = new DbSchemaManager(_context);
        await schema.EnsureActivityBboxTableAsync();
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureRecordingStagingTableAsync(); // track-delete seam touches it
        await schema.EnsureTrackEditTablesAsync(); // track-delete seam also touches Track_Blob_History
        await schema.EnsureErrorLogTableAsync(); // B8: every reset also wipes the caller's Error_Log rows
        await schema.EnsureIndexesAsync();

        foreach (var u in new[] { Me, Other })
            _context.Users.Add(new AppUser
            {
                UserId = u, DisplayName = u, Email = $"{u}@t.com", IsAdmin = false,
                CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
            });
        await _context.SaveChangesAsync();

        var cache        = new MemoryCache(new MemoryCacheOptions());
        var settings     = new ProfileSettingsService(_context, NullLogger<ProfileSettingsService>.Instance, cache, TimeProvider.System);
        var reader       = new DirectTrackReader(_context);
        var shapeMatcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, reader);
        var engine       = new MatchEngineService(_context, reader, NullLogger<MatchEngineService>.Instance);
        var effortMath   = new EffortMathService(_context, reader, NullLogger<EffortMathService>.Instance);

        _svc = SegmentServices.Create(
            _context, cache, new TestVisibilityService(Me), new UserCacheInvalidator(), settings,
            shapeMatcher, engine, effortMath, reader);
        // Same context, non-owner viewpoint — the I-8a owner gate must reject exclude/restore.
        _effortsAsOther = SegmentServices.Create(
            _context, cache, new TestVisibilityService(Other), new UserCacheInvalidator(), settings,
            shapeMatcher, engine, effortMath, reader).Efforts;
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    // ── seeding (mirrors the MatchEngineServiceTests harness: 20-pt track crossing an 8-pt segment) ──

    private async Task<int> SeedTrackAsync()
    {
        var activity = new Activity
        {
            UserId = Me, TrackName = "T",
            StartGpsLat = 47.000, StartGpsLong = 11.000, EndGpsLat = 47.019, EndGpsLong = 11.000,
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        TrackId trackId = activity.TrackId;

        var pts = new List<GpsData>();
        for (int i = 0; i < 20; i++)
            pts.Add(new GpsData
            {
                GpsLat = 47.000 + i * 0.001, GpsLong = 11.000,
                DateTimeUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i * 10),
                Distance = i * 111.0, Speed = 5.0, Altitude = 100 + i,
            });
        await TrackSeed.SeedTrackAsync(_context, trackId, pts);

        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {4})", trackId.Value, 47.000, 47.019, 11.000, 11.000);
        return trackId.Value;
    }

    private async Task<SegmentId> SeedSegmentAsync()
    {
        var segment = new Segment { UserId = Me, SegmentName = "S", SegmentDistance = 778.0 };
        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();
        for (int i = 0; i < 8; i++)
            _context.SegmentGps.Add(new SegmentGps
            {
                SegmentId = segment.SegmentId, GpsLat = 47.000 + (6 + i) * 0.001, GpsLong = 11.000,
            });
        await _context.SaveChangesAsync();
        return segment.SegmentId;
    }

    /// <summary>Seeds track+segment, runs the real matcher, returns (segId, trackId, effortId).</summary>
    private async Task<(SegmentId segId, TrackId trackId, GpsInSegmentId effortId)> SeedAndAnalyzeAsync()
    {
        TrackId trackId = new TrackId(await SeedTrackAsync());
        var segId   = await SeedSegmentAsync();
        await _svc.Analysis.AnalyzeSegmentMatchesAsync(segId);
        var effort = Assert.Single(await _context.GpsInSegments.AsNoTracking()
            .Where(g => g.SegmentId == segId).ToListAsync());
        return (segId, trackId, effort.GpsInSegmentId);
    }

    private Task<int> ExclusionRowCountAsync() =>
        _context.SegmentEffortExclusions.AsNoTracking().CountAsync();

    // ── the core pin: exclusion survives re-analysis ─────────────────────────

    [Fact]
    public async Task Exclude_SurvivesReanalysis_AbsentFromTotalsCountsListAndCharts()
    {
        var (segId, trackId, effortId) = await SeedAndAnalyzeAsync();

        // Pre-exclusion baseline: 1 counted effort with totals.
        var totalsBefore = await _svc.Efforts.GetEffortTotalsForSegmentsAsync([segId]);
        Assert.Equal(1, totalsBefore[segId].EffortCount);
        Assert.Equal(1, (await _svc.Efforts.GetEffortCountsForSegmentsAsync([segId])).GetValueOrDefault(segId));

        Assert.True(await _svc.Efforts.RemoveEffortAsync(effortId));

        // GPSinSegment row still there (no hard delete) + one exclusion row.
        Assert.Equal(1, await _context.GpsInSegments.AsNoTracking().CountAsync(g => g.SegmentId == segId));
        Assert.Equal(1, await ExclusionRowCountAsync());

        // Re-run the REAL matcher — it wipes + re-inserts the crossing (analysis untouched by design).
        await _svc.Analysis.AnalyzeSegmentMatchesAsync(segId);
        var rebuilt = Assert.Single(await _context.GpsInSegments.AsNoTracking()
            .Where(g => g.SegmentId == segId).ToListAsync());
        Assert.Equal(trackId, rebuilt.TrackId);

        // The exclusion still bites on every read consumer:
        Assert.Equal(0, (await _svc.Efforts.GetEffortCountsForSegmentsAsync([segId])).GetValueOrDefault(segId));
        Assert.False((await _svc.Efforts.GetEffortTotalsForSegmentsAsync([segId])).ContainsKey(segId));
        Assert.Null(await _svc.Efforts.GetPrecomputedEffortTotalsAsync(segId));
        // Efforts list is a LEFT JOIN: the row stays, flagged excluded (drives the grid's Restore UI).
        var listed = Assert.Single(await _svc.Efforts.GetSegmentEffortsDetailedAsync(segId));
        Assert.True(listed.Excluded);
        // Effort-cost chart data anti-joins it out even when requested by id.
        Assert.Empty(await _svc.Efforts.GetPerformanceDataForEffortsAsync(
            segId, new HashSet<GpsInSegmentId> { rebuilt.GpsInSegmentId }));
    }

    [Fact]
    public async Task Restore_BringsEffortBack()
    {
        var (segId, _, effortId) = await SeedAndAnalyzeAsync();
        Assert.True(await _svc.Efforts.RemoveEffortAsync(effortId));

        Assert.True(await _svc.Efforts.RestoreEffortAsync(effortId));
        Assert.Equal(0, await ExclusionRowCountAsync());

        Assert.Equal(1, (await _svc.Efforts.GetEffortCountsForSegmentsAsync([segId])).GetValueOrDefault(segId));
        Assert.Equal(1, (await _svc.Efforts.GetEffortTotalsForSegmentsAsync([segId]))[segId].EffortCount);
        var listed = Assert.Single(await _svc.Efforts.GetSegmentEffortsDetailedAsync(segId));
        Assert.False(listed.Excluded);
        Assert.Single(await _svc.Efforts.GetPerformanceDataForEffortsAsync(segId, new HashSet<GpsInSegmentId> { effortId }));
    }

    [Fact]
    public async Task RemoveAndRestore_AreIdempotent()
    {
        var (_, _, effortId) = await SeedAndAnalyzeAsync();
        Assert.True(await _svc.Efforts.RemoveEffortAsync(effortId));
        Assert.True(await _svc.Efforts.RemoveEffortAsync(effortId)); // second exclude → still one row
        Assert.Equal(1, await ExclusionRowCountAsync());
        Assert.True(await _svc.Efforts.RestoreEffortAsync(effortId));
        Assert.True(await _svc.Efforts.RestoreEffortAsync(effortId)); // second restore → no-op
        Assert.Equal(0, await ExclusionRowCountAsync());
    }

    // ── I-8a owner gate: non-owner gets false (controller maps false → 404) ──

    [Fact]
    public async Task NonOwner_CannotExcludeOrRestore()
    {
        var (_, _, effortId) = await SeedAndAnalyzeAsync();

        Assert.False(await _effortsAsOther.RemoveEffortAsync(effortId));
        Assert.Equal(0, await ExclusionRowCountAsync());

        Assert.True(await _svc.Efforts.RemoveEffortAsync(effortId)); // owner excludes for real
        Assert.False(await _effortsAsOther.RestoreEffortAsync(effortId));
        Assert.Equal(1, await ExclusionRowCountAsync()); // still excluded
    }

    // ── SD-01: excluding/restoring re-shapes painted spans → attached done heatmap goes stale ──

    [Fact]
    public async Task ExcludeAndRestore_MarkAttachedDoneHeatmapStale()
    {
        var (segId, _, effortId) = await SeedAndAnalyzeAsync();
        _context.Heatmaps.Add(new Heatmap { SegmentId = segId, Status = "done" });
        await _context.SaveChangesAsync();

        Assert.True(await _svc.Efforts.RemoveEffortAsync(effortId));
        Assert.Equal("stale", (await _context.Heatmaps.AsNoTracking().SingleAsync(h => h.SegmentId == segId)).Status);

        await _context.Heatmaps.Where(h => h.SegmentId == segId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, "done"));
        Assert.True(await _svc.Efforts.RestoreEffortAsync(effortId));
        Assert.Equal("stale", (await _context.Heatmaps.AsNoTracking().SingleAsync(h => h.SegmentId == segId)).Status);
    }

    // ── stale-only-from-done guard: a still-rendering (non-done) heatmap is never flipped stale ──

    [Fact]
    public async Task Exclude_NonDoneHeatmap_NotStaled()
    {
        var (segId, _, effortId) = await SeedAndAnalyzeAsync();
        _context.Heatmaps.Add(new Heatmap { SegmentId = segId, Status = "running" });
        await _context.SaveChangesAsync();

        Assert.True(await _svc.Efforts.RemoveEffortAsync(effortId));

        // Only `done` heatmaps demote to `stale`; a running render must survive to completion.
        Assert.Equal("running", (await _context.Heatmaps.AsNoTracking().SingleAsync(h => h.SegmentId == segId)).Status);
    }

    // ── delete-purges-tiles: segment delete removes the heatmap's packed .htiles file ──

    [Fact]
    public async Task SegmentDelete_PurgesHeatmapTileFile()
    {
        var segId = await SeedSegmentAsync();
        var heatmap = new Heatmap { SegmentId = segId, Status = "done" };
        _context.Heatmaps.Add(heatmap);
        await _context.SaveChangesAsync();

        await using (var writer = _svc.TileStore.BeginWrite(heatmap.HeatmapId))
        {
            await writer.AppendAsync(HeatmapGenerationService.CountLogLayer,
                new[] { new HeatmapTileData(0, 0, 0, new byte[] { 1, 2, 3 }) });
            await writer.CommitAsync();
        }
        Assert.True(File.Exists(_svc.TileStore.GetPhysicalPath(heatmap.HeatmapId)));

        _context.ChangeTracker.Clear();
        Assert.True(await _svc.Crud.DeleteSegmentAsync(segId));

        Assert.False(File.Exists(_svc.TileStore.GetPhysicalPath(heatmap.HeatmapId)));

        // The row itself must go too (FK ON DELETE CASCADE under FK-on) — file purge alone
        // would leave an orphaned Heatmap row if the cascade were ever dropped.
        _context.ChangeTracker.Clear();
        Assert.False(await _context.Heatmaps.AnyAsync(h => h.SegmentId == segId));
    }

    // ── delete seams: exclusion rows have no FK cascade and must be purged explicitly ──

    [Fact]
    public async Task SegmentDelete_PurgesExclusionRows()
    {
        var (segId, _, effortId) = await SeedAndAnalyzeAsync();
        Assert.True(await _svc.Efforts.RemoveEffortAsync(effortId));

        // Fresh-scope semantics: production delete seams run in their own DI scope; the shared test
        // context still tracks the effort from RemoveEffortAsync's FindAsync, and EF's client cascade
        // would double-delete the row the seam's raw SQL already removed.
        _context.ChangeTracker.Clear();
        Assert.True(await _svc.Crud.DeleteSegmentAsync(segId));
        Assert.Equal(0, await ExclusionRowCountAsync());
    }

    [Fact]
    public async Task TrackDelete_PurgesExclusionRows()
    {
        var (_, trackId, effortId) = await SeedAndAnalyzeAsync();
        Assert.True(await _svc.Efforts.RemoveEffortAsync(effortId));

        // Fresh-scope semantics — see SegmentDelete_PurgesExclusionRows.
        _context.ChangeTracker.Clear();
        var vis   = new TestVisibilityService(Me);
        var inval = new UserCacheInvalidator();
        var command = new ActivityCommandService(
            _context, vis, inval, NullLogger<ActivityCommandService>.Instance,
            TestLifecycle.For(_context, vis, inval));
        Assert.True(await command.DeleteActivityCoreAsync(trackId, Me));
        Assert.Equal(0, await ExclusionRowCountAsync());
    }

    [Fact]
    public async Task UserReset_PurgesExclusionRows()
    {
        var (_, _, effortId) = await SeedAndAnalyzeAsync();
        Assert.True(await _svc.Efforts.RemoveEffortAsync(effortId));

        var reset = new DatabaseResetService(
            _context, NullLogger<DatabaseResetService>.Instance, new TestCurrentUserService(Me));
        await reset.ResetDatabaseAsync(new DeleteOptions { Segments = true });
        Assert.Equal(0, await ExclusionRowCountAsync());
    }
}
