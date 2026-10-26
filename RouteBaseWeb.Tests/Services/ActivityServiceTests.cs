using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Characterization tests for the activity service trio (<see cref="ActivityQueryService"/>,
/// <see cref="ActivityGpsService"/>, <see cref="ActivityCommandService"/>) — pin update/delete
/// ownership + visibility rules, transactional child-row deletion (AS-02), filter edge cases,
/// statistics aggregation, GPS ownership scoping, and owner-token cache eviction (AS-01).
/// Named in-memory SQLite (shared cache) so the raw-SQL delete and window functions run for real.
/// </summary>
public class ActivityServiceTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Other = "other";
    private string _conn = null!;
    private RouteBaseDbContext _context = null!;
    private IMemoryCache _cache = null!;
    private GpsCoordinateCache _gpsCache = null!;
    private UserCacheInvalidator _invalidator = null!;
    private SingleFlight _singleFlight = null!;
    private TrackReaderCache _readerCache = null!;
    private ContentTrackReader _trackReader = null!;
    private ServiceProvider _provider = null!;
    private ActivityQueryService _query = null!;
    private ActivityGpsService _gps = null!;
    private ActivityCommandService _command = null!;
    private int _nextTrackId = 1;
    // Accumulates each track's points so a per-track blob can be re-encoded on every append (the blob is the
    // whole track — INSERT OR REPLACE). Replaces the pre-flip GPS-row inserts.
    private readonly Dictionary<int, List<GpsData>> _points = new();

    public async Task InitializeAsync()
    {
        _conn = $"Data Source=file:activity_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        foreach (var id in new[] { Me, Other })
            _context.Users.Add(new AppUser { UserId = id, DisplayName = id, Email = $"{id}@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        // Non-EF point stores must exist on the shared-cache DB before the reader decodes.
        var schema = new DbSchemaManager(_context);
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureRecordingStagingTableAsync();
        await schema.EnsureActivityBboxTableAsync(); // DeleteActivityAsync now drops the orphan bbox row (inval-09)
        await schema.EnsureTrackEditTablesAsync(); // DeleteActivityCoreAsync's child-table loop touches Track_Blob_History

        _cache = new MemoryCache(new MemoryCacheOptions());
        _gpsCache = new GpsCoordinateCache(new MemoryBudget().CoordinateCachePoints);
        _invalidator = new UserCacheInvalidator();
        _singleFlight = new SingleFlight();
        // Scope factory yields fresh contexts on the same shared-cache DB (single-flight producers).
        _provider = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddDbContext<RouteBaseDbContext>(o => o.UseSqlite(_conn))
            .AddScoped<ITrackObjectStore, TrackObjectStore>()
            .AddScoped<TrackContentStore>()
            .AddScoped<RecordingStagingStore>()
            .BuildServiceProvider();

        // Blob-backed reader (v2) on the same shared-cache DB — the read seam ActivityGpsService decodes
        // through, so these tests still exercise real decode + stride end-to-end (now from Track_Blob).
        _readerCache = new TrackReaderCache(64L * 1024 * 1024);
        _trackReader = new ContentTrackReader(_readerCache, _singleFlight, _provider.GetRequiredService<IServiceScopeFactory>());

        var vis = new TestVisibilityService(Me);
        _query   = QueryFor(vis);
        _gps     = GpsFor(vis);
        _command = CommandFor(vis);
    }

    public Task DisposeAsync()
    {
        _cache.Dispose();
        _gpsCache.Dispose();
        _provider.Dispose();
        return _context.DisposeAsync().AsTask();
    }

    private ActivityQueryService QueryFor(IVisibilityService vis) =>
        new(_context, vis, _cache, _invalidator);

    private ActivityGpsService GpsFor(IVisibilityService vis) =>
        new(_context, _gpsCache, vis, _invalidator,
            new GpsCoordinateCoalescer(_singleFlight, _trackReader, _gpsCache), _trackReader);

    private ActivityCommandService CommandFor(IVisibilityService vis) =>
        new(_context, vis, _invalidator, NullLogger<ActivityCommandService>.Instance,
            TestLifecycle.For(_context, vis, _invalidator));

    private async Task<int> AddActivityAsync(
        string owner, string? type = null, DateTime? localStart = null,
        double distance = 0, double ascent = 0, double moving = 0, string visibility = "private")
    {
        int id = _nextTrackId++;
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(id), UserId = owner, ActivityType = type,
            StartDateTimeLocal = localStart, StartDateTimeUtc = localStart,
            TotalDistance = distance, TotalAscent = ascent, TimeMoving = moving,
            Visibility = visibility,
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private Task AddGpsAsync(TrackId trackId, double lat, double lng) => AddGpsRawAsync(trackId, lat, lng);

    // Append one point (optionally NULL coords) and re-encode the track's blob. Returns the point's ordinal
    // (0-based) — the post-flip address that GpsCoordinate.GpsId now carries (was the 1-based GPS_ID).
    private async Task<int> AddGpsRawAsync(TrackId trackId, double? lat, double? lng)
    {
        if (!_points.TryGetValue(trackId.Value, out var list)) list = _points[trackId.Value] = new();
        list.Add(new GpsData { GpsLat = lat, GpsLong = lng });
        await TrackSeed.SeedTrackAsync(_context, trackId, list);
        return list.Count - 1;
    }

    private async Task<bool> BlobExistsAsync(TrackId trackId)
    {
        var rows = await _context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Activity_Version WHERE track_id = {0} AND is_head = 1", trackId.Value)
            .ToListAsync();
        return rows.FirstOrDefault() > 0;
    }

    // ─── Update ownership / visibility ───

    [Fact]
    public async Task Update_NonOwner_ReturnsFalse()
    {
        var id = await AddActivityAsync(Other);
        Assert.False(await _command.UpdateActivityAsync(new TrackId(id), "new", null, null));
    }

    [Fact]
    public async Task Update_InvalidVisibility_ReturnsFalse()
    {
        var id = await AddActivityAsync(Me);
        Assert.False(await _command.UpdateActivityAsync(new TrackId(id), null, null, "bogus"));
    }

    [Fact]
    public async Task Update_LowercasesVisibility()
    {
        var id = await AddActivityAsync(Me);
        Assert.True(await _command.UpdateActivityAsync(new TrackId(id), null, null, "Public"));

        var stored = await _context.Activities.AsNoTracking().FirstAsync(a => a.TrackId == new TrackId(id));
        Assert.Equal("public", stored.Visibility);
    }

    // ─── IsActivityOwnedAsync (AA-01) ───

    [Fact]
    public async Task IsActivityOwnedAsync_True_ForOwnActivity()
    {
        var id = await AddActivityAsync(Me);
        Assert.True(await _query.IsActivityOwnedAsync(new TrackId(id)));
    }

    [Fact]
    public async Task IsActivityOwnedAsync_False_ForVisibleFriendActivity()
    {
        var id = await AddActivityAsync(Other, visibility: "friends");
        var query = QueryFor(new ViewerVisibility(Me, Other)); // visible but not owned
        Assert.False(await query.IsActivityOwnedAsync(new TrackId(id)));
    }

    [Fact]
    public async Task IsActivityOwnedAsync_False_ForUnknownId()
    {
        Assert.False(await _query.IsActivityOwnedAsync(new TrackId(999999)));
    }

    // ─── Delete ownership / cascade / transaction (AS-02) ───

    [Fact]
    public async Task Delete_NonOwner_ReturnsFalse()
    {
        var id = await AddActivityAsync(Other);
        Assert.False(await _command.DeleteActivityAsync(new TrackId(id)));
        Assert.True(await _context.Activities.AnyAsync(a => a.TrackId == new TrackId(id)));
    }

    // Decision 9: DeleteActivityCoreAsync used to trust every caller to pre-scope by owner (raw
    // _context.Activities, no filter) — a latent footgun for a future caller that forgets. It now asserts
    // the owner itself, so a mismatched id is a no-op no matter what the caller believes it owns.
    [Fact]
    public async Task DeleteActivityCoreAsync_WrongOwner_ReturnsFalse_AndLeavesRowIntact()
    {
        var id = await AddActivityAsync(Other);

        Assert.False(await _command.DeleteActivityCoreAsync(new TrackId(id), Me));

        Assert.True(await _context.Activities.AnyAsync(a => a.TrackId == new TrackId(id)));
    }

    // The trash purge's TOCTOU guard: the purge endpoint snapshots the trashed set, then deletes one id
    // at a time — a ride RESTORED after the snapshot must not be permanently deleted when the loop
    // reaches it. requireTrashed re-asserts the state inside the delete's own transaction.
    [Fact]
    public async Task DeleteActivityCoreAsync_RequireTrashed_RefusesARestoredRow()
    {
        var id = await AddActivityAsync(Me);   // Released — what a just-restored ride looks like

        Assert.False(await _command.DeleteActivityCoreAsync(new TrackId(id), Me, requireTrashed: true));
        Assert.True(await _context.Activities.AnyAsync(a => a.TrackId == new TrackId(id)));

        // Actually trashed → the same call purges.
        var row = await _context.Activities.FirstAsync(a => a.TrackId == new TrackId(id));
        row.Lifecycle = LifecycleStates.Trashed;
        row.LifecycleReason = LifecycleReasons.User;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        Assert.True(await _command.DeleteActivityCoreAsync(new TrackId(id), Me, requireTrashed: true));
        Assert.False(await _context.Activities.AnyAsync(a => a.TrackId == new TrackId(id)));
    }

    [Fact]
    public async Task Delete_RemovesChildRows()
    {
        var id = await AddActivityAsync(Me);
        await AddGpsAsync(new TrackId(id), 47.0, 11.0);
        _context.Photos.Add(new Photo { TrackId = new TrackId(id), UserId = Me, ImportedUtc = DateTime.UtcNow });
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(1), UserId = Me });
        await _context.SaveChangesAsync();
        _context.GpsInSegments.Add(new GpsInSegment { TrackId = new TrackId(id), SegmentId = new SegmentId(1) });
        await _context.SaveChangesAsync();
        // Mirror a fresh request context: the delete path re-queries; nothing pre-tracked,
        // so EF's cascade doesn't fight the raw-SQL child deletes.
        _context.ChangeTracker.Clear();

        Assert.True(await _command.DeleteActivityCoreAsync(new TrackId(id), Me));   // the PERMANENT purge (trash → delete forever)

        Assert.False(await _context.Activities.AnyAsync(a => a.TrackId == new TrackId(id)));
        Assert.False(await BlobExistsAsync(new TrackId(id)));   // track blob deleted with the activity
        Assert.False(await _context.Photos.AnyAsync(p => p.TrackId == new TrackId(id)));
        Assert.False(await _context.GpsInSegments.AnyAsync(s => s.TrackId == new TrackId(id)));
    }

    [Fact]
    public async Task DeleteActivity_DeletesRecordingSession()
    {
        // Fresh EnsureCreated schema had no RecordingSession→Activity FK before ME-01, so the
        // delete path must remove the session explicitly — otherwise a ghost live session lingers.
        var id = await AddActivityAsync(Me);
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = Guid.NewGuid().ToString(), TrackId = new TrackId(id), UserId = Me,
            Status = "recording", StartedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        Assert.True(await _command.DeleteActivityCoreAsync(new TrackId(id), Me));

        Assert.False(await _context.RecordingSessions.AnyAsync(s => s.TrackId == new TrackId(id)));
    }

    [Fact]
    public async Task DeleteActivity_DropsActivityBboxRow()
    {
        // inval-09: Activity_Bbox is not EF-modelled → not cascade-deleted; the delete path must drop it
        // explicitly, else an orphan row (no owning Activity) leaks.
        var id = await AddActivityAsync(Me);
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long) VALUES ({0}, 47.0, 47.1, 11.0, 11.1)", id);
        _context.ChangeTracker.Clear();

        Assert.True(await _command.DeleteActivityCoreAsync(new TrackId(id), Me));

        var remaining = await _context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Activity_Bbox WHERE track_ID = {0}", id)
            .ToListAsync();
        Assert.Equal(0, remaining.Single());
    }

    // ─── Filter edge cases ───

    [Fact]
    public async Task FilteredIds_EndDateIsInclusive()
    {
        var day = new DateTime(2025, 5, 10, 0, 0, 0, DateTimeKind.Unspecified);
        var id = await AddActivityAsync(Me, localStart: day.AddHours(23)); // late on the end date

        var ids = await _query.GetFilteredActivityIdsAsync(null, null, endDate: day);

        Assert.Contains(new TrackId(id), ids);
    }

    [Fact]
    public async Task FilteredIds_TypesMultiSelectWinsOverGroup()
    {
        var ride = await AddActivityAsync(Me, type: "Ride");
        await AddActivityAsync(Me, type: "Run");

        // Group filter says "run" but the exact-types multi-select ["Ride"] must win.
        var ids = await _query.GetFilteredActivityIdsAsync(
            "run", null, null, activityTypes: new[] { "Ride" });

        Assert.Equal(new[] { new TrackId(ride) }, ids);
    }

    [Fact]
    public async Task FilteredIds_OwnerMatchNone_ReturnsEmpty()
    {
        await AddActivityAsync(Me);
        var ids = await _query.GetFilteredActivityIdsAsync(null, null, null, ownerMatchNone: true);
        Assert.Empty(ids);
    }

    // ─── GPS ownership scoping ───

    [Fact]
    public async Task TrackCoordinates_OtherUsersTrack_ReturnsEmpty()
    {
        var id = await AddActivityAsync(Other);
        await AddGpsAsync(new TrackId(id), 47.0, 11.0);

        var coords = await _gps.GetTrackCoordinatesAsync(new TrackId(id));

        Assert.Empty(coords);
    }

    // ─── AS-06: parameterized downsampling SQL still returns points ───

    [Fact]
    public async Task DownsampledCoords_ParameterizedSql_ReturnsPoints()
    {
        var id = await AddActivityAsync(Me);
        for (int i = 0; i < 10; i++)
            await AddGpsAsync(new TrackId(id), 47.0 + i * 0.001, 11.0 + i * 0.001);

        var single = await _gps.GetTrackCoordinatesAsync(new TrackId(id), maxPoints: 3);
        Assert.NotEmpty(single);
        Assert.True(single.Count <= 10);

        var bulk = await _gps.GetBulkTrackCoordinatesAsync(new List<int> { id }, maxPointsPerTrack: 3);
        Assert.True(bulk.ContainsKey(id));
        Assert.NotEmpty(bulk[id]);
    }

    // ─── TE-35: decode + stride parity with the retired ROW_NUMBER SQL ───

    // 10 points, maxPoints 3 → stride = MAX(1, 10/3) = 3 → keep 0-based positions 0,3,6,9.
    // GpsId is the ordinal now (0-based), so the kept set is {0,3,6,9} (was {1,4,7,10} under GPS_IDs).
    [Fact]
    public async Task Downsample_MatchesHandComputedStride()
    {
        var id = await AddActivityAsync(Me);
        for (int i = 0; i < 10; i++)
            await AddGpsAsync(new TrackId(id), 47.0 + i * 0.001, 11.0 + i * 0.001); // ordinals 0..9

        var single = await _gps.GetTrackCoordinatesAsync(new TrackId(id), maxPoints: 3);
        Assert.Equal(new[] { 0, 3, 6, 9 }, single.Select(c => c.GpsId));

        var bulk = await _gps.GetBulkTrackCoordinatesAsync(new List<int> { id }, maxPointsPerTrack: 3);
        Assert.Equal(new[] { 0, 3, 6, 9 }, bulk[id].Select(c => c.GpsId));
    }

    // n < maxPoints → stride = MAX(1, 3/10) = 1 → every point, in ordinal order.
    [Fact]
    public async Task Downsample_FewerThanMax_ReturnsAllInOrder()
    {
        var id = await AddActivityAsync(Me);
        for (int i = 0; i < 3; i++)
            await AddGpsAsync(new TrackId(id), 47.0 + i * 0.001, 11.0 + i * 0.001); // ordinals 0..2

        var single = await _gps.GetTrackCoordinatesAsync(new TrackId(id), maxPoints: 10);
        Assert.Equal(new[] { 0, 1, 2 }, single.Select(c => c.GpsId));
    }

    [Fact]
    public async Task Downsample_SinglePoint_ReturnsThatPoint()
    {
        var id = await AddActivityAsync(Me);
        await AddGpsAsync(new TrackId(id), 47.0, 11.0); // ordinal 0

        var single = await _gps.GetTrackCoordinatesAsync(new TrackId(id), maxPoints: 5);
        Assert.Equal(new[] { 0 }, single.Select(c => c.GpsId));
    }

    // NULL-coord points are filtered BEFORE ranking (as the SQL WHERE did), then stride runs over the
    // survivors. Survivor ordinals [0,2,3,5,6,7], cnt 6, maxPoints 2 → stride 3 → positions 0,3 → ordinals 0,5.
    [Fact]
    public async Task Downsample_NullCoordHoles_FilteredBeforeStride()
    {
        var id = await AddActivityAsync(Me);
        await AddGpsRawAsync(new TrackId(id), 47.0, 11.0);   // 0 good
        await AddGpsRawAsync(new TrackId(id), null, 11.1);   // 1 null lat
        await AddGpsRawAsync(new TrackId(id), 47.2, 11.2);   // 2 good
        await AddGpsRawAsync(new TrackId(id), 47.3, 11.3);   // 3 good
        await AddGpsRawAsync(new TrackId(id), 47.4, null);   // 4 null lng
        await AddGpsRawAsync(new TrackId(id), 47.5, 11.5);   // 5 good
        await AddGpsRawAsync(new TrackId(id), 47.6, 11.6);   // 6 good
        await AddGpsRawAsync(new TrackId(id), 47.7, 11.7);   // 7 good

        var single = await _gps.GetTrackCoordinatesAsync(new TrackId(id), maxPoints: 2);
        Assert.Equal(new[] { 0, 5 }, single.Select(c => c.GpsId));
    }

    // Full-resolution single-track shape keeps every point and maps NULL coord → 0 (bug-for-bug).
    [Fact]
    public async Task FullCoordinates_NullCoords_EmittedAsZero()
    {
        var id = await AddActivityAsync(Me);
        await AddGpsRawAsync(new TrackId(id), 47.0, 11.0);   // 0
        await AddGpsRawAsync(new TrackId(id), null, null);   // 1
        await AddGpsRawAsync(new TrackId(id), 47.2, 11.2);   // 2

        var full = await _gps.GetTrackCoordinatesAsync(new TrackId(id));
        Assert.Equal(new[] { 0, 1, 2 }, full.Select(c => c.GpsId));
        var hole = full.Single(c => c.GpsId == 1);
        Assert.Equal(0, hole.Lat);
        Assert.Equal(0, hole.Lng);
    }

    // Bulk no-limit path filters NULL-coord points out entirely (the SQL WHERE lat/lng NOT NULL).
    [Fact]
    public async Task BulkNoLimit_NullCoords_Filtered()
    {
        var id = await AddActivityAsync(Me);
        await AddGpsRawAsync(new TrackId(id), 47.0, 11.0);   // 0
        await AddGpsRawAsync(new TrackId(id), null, null);   // 1
        await AddGpsRawAsync(new TrackId(id), 47.2, 11.2);   // 2

        var bulk = await _gps.GetBulkTrackCoordinatesAsync(new List<int> { id }, maxPointsPerTrack: 0);
        Assert.Equal(new[] { 0, 2 }, bulk[id].Select(c => c.GpsId));
    }

    // ─── AS-01: owner-token invalidation reaches other viewers ───

    [Fact]
    public async Task VisibilityChange_EvictsOtherViewersSliceCache()
    {
        var id = await AddActivityAsync(Other, visibility: "public");
        await _context.Activities.Where(a => a.TrackId == new TrackId(id))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.TrackName, "old"));
        _context.ChangeTracker.Clear();

        var viewer = QueryFor(new ViewerVisibility(Me, Other));
        var owner  = CommandFor(new ViewerVisibility(Other));

        var first = await viewer.GetActivitySlicesByIdsAsync(new List<int> { id });
        Assert.Equal("old", first[id].TrackName);

        // Owner renames — invalidates the owner token, which the viewer's entry now carries.
        Assert.True(await owner.UpdateActivityAsync(new TrackId(id), "new", null, null));

        var second = await viewer.GetActivitySlicesByIdsAsync(new List<int> { id });
        Assert.Equal("new", second[id].TrackName); // stale "old" would mean the viewer's entry survived
    }

    [Fact]
    public async Task Delete_EvictsOtherViewersCoordCache()
    {
        var id = await AddActivityAsync(Other, visibility: "public");
        await AddGpsAsync(new TrackId(id), 47.0, 11.0);
        _context.ChangeTracker.Clear();

        var viewer = GpsFor(new ViewerVisibility(Me, Other));
        var owner  = CommandFor(new ViewerVisibility(Other));

        var first = await viewer.GetTrackCoordinatesAsync(new TrackId(id));
        Assert.Single(first); // cached, tokened to the owner

        Assert.True(await owner.DeleteActivityAsync(new TrackId(id)));

        var second = await viewer.GetTrackCoordinatesAsync(new TrackId(id));
        Assert.Empty(second); // entry evicted → re-query finds the deleted activity gone
    }

    // ─── Bulk update / delete (Select-mode) ───

    [Fact]
    public async Task UpdateMany_OwnedOnly_SetsVisibility_AndReturnsCount()
    {
        var a = await AddActivityAsync(Me, visibility: "friends");
        var b = await AddActivityAsync(Me, visibility: "friends");
        var foreign = await AddActivityAsync(Other, visibility: "friends");

        // The foreign id is passed too — it must be silently skipped, not counted, not changed.
        var updated = await _command.UpdateManyAsync(new[] { new TrackId(a), new TrackId(b), new TrackId(foreign) }, null, "public");

        Assert.Equal(2, updated);
        var rows = await _context.Activities.AsNoTracking().ToListAsync();
        Assert.Equal("public", rows.Single(r => r.TrackId == new TrackId(a)).Visibility);
        Assert.Equal("public", rows.Single(r => r.TrackId == new TrackId(b)).Visibility);
        Assert.Equal("friends", rows.Single(r => r.TrackId == new TrackId(foreign)).Visibility); // untouched
    }

    [Fact]
    public async Task UpdateMany_LowercasesVisibility()
    {
        var id = await AddActivityAsync(Me);
        Assert.Equal(1, await _command.UpdateManyAsync(new[] { new TrackId(id) }, null, "Private"));
        var stored = await _context.Activities.AsNoTracking().FirstAsync(a => a.TrackId == new TrackId(id));
        Assert.Equal("private", stored.Visibility);
    }

    [Fact]
    public async Task UpdateMany_ActivityType_SetsType()
    {
        var a = await AddActivityAsync(Me, type: "Ride");
        var b = await AddActivityAsync(Me, type: "Ride");
        Assert.Equal(2, await _command.UpdateManyAsync(new[] { new TrackId(a), new TrackId(b) }, "Run", null));
        var rows = await _context.Activities.AsNoTracking().ToListAsync();
        Assert.All(rows, r => Assert.Equal("Run", r.ActivityType));
    }

    [Fact]
    public async Task UpdateMany_InvalidVisibility_ReturnsZero_NoChange()
    {
        var id = await AddActivityAsync(Me, visibility: "friends");
        Assert.Equal(0, await _command.UpdateManyAsync(new[] { new TrackId(id) }, null, "bogus"));
        var stored = await _context.Activities.AsNoTracking().FirstAsync(a => a.TrackId == new TrackId(id));
        Assert.Equal("friends", stored.Visibility);
    }

    [Fact]
    public async Task UpdateMany_EmptyIds_ReturnsZero()
    {
        Assert.Equal(0, await _command.UpdateManyAsync(Array.Empty<TrackId>(), null, "public"));
    }

    [Fact]
    public async Task DeleteMany_TrashesOwnedOnly_AndReturnsCount()
    {
        var a = await AddActivityAsync(Me);
        var b = await AddActivityAsync(Me);
        var foreign = await AddActivityAsync(Other);
        _context.ChangeTracker.Clear();

        var deleted = await _command.DeleteManyAsync(new[] { new TrackId(a), new TrackId(b), new TrackId(foreign) });

        Assert.Equal(2, deleted);
        var rows = await _context.Activities.AsNoTracking().ToListAsync();
        Assert.Equal(LifecycleStates.Trashed, rows.Single(r => r.TrackId == new TrackId(a)).Lifecycle);
        Assert.Equal(LifecycleStates.Trashed, rows.Single(r => r.TrackId == new TrackId(b)).Lifecycle);
        Assert.Equal(LifecycleStates.Released, rows.Single(r => r.TrackId == new TrackId(foreign)).Lifecycle); // untouched
    }

    /// <summary>Visibility double where the viewer additionally sees the listed owners' activities.</summary>
    private sealed class ViewerVisibility : IVisibilityService
    {
        private readonly string _me;
        private readonly HashSet<string> _visible;
        public ViewerVisibility(string me, params string[] alsoVisible)
        {
            _me = me;
            _visible = new HashSet<string>(alsoVisible, StringComparer.Ordinal) { me };
        }
        public string GetCurrentUserId() => _me;
        // Mirrors VisibilityService: the lifecycle gate FIRST, then owner scoping. Without the gate a
        // trashed ride would still be served here and the eviction assertion below would prove nothing.
        public IQueryable<Activity> VisibleActivities(IQueryable<Activity> q) =>
            q.Where(a => a.Lifecycle == LifecycleStates.Released && _visible.Contains(a.UserId!));
        public IQueryable<Activity> OwnedActivities(IQueryable<Activity> q) => q.Where(a => a.UserId == _me);
        public IQueryable<Segment> VisibleSegments(IQueryable<Segment> q) => q.Where(s => _visible.Contains(s.UserId!));
        public IQueryable<Segment> OwnedSegments(IQueryable<Segment> q) => q.Where(s => s.UserId == _me);
        public IQueryable<MultiDayTour> VisibleTours(IQueryable<MultiDayTour> q) => q.Where(t => _visible.Contains(t.UserId!));
        public IQueryable<MultiDayTour> OwnedTours(IQueryable<MultiDayTour> q) => q.Where(t => t.UserId == _me);
        public IQueryable<AppCredentials> OwnedCredentials(IQueryable<AppCredentials> q) => q.Where(c => c.UserId == _me);
        public Task<List<string>> GetVisibleUserIdsAsync() => Task.FromResult(_visible.ToList());
        public Task<bool> CanViewItemAsync(string ownerUserId, string? visibility)
            => Task.FromResult(ownerUserId == _me || visibility == "public"
                || (visibility == "friends" && _visible.Contains(ownerUserId)));
        // Permissive is deliberate and unreachable: the Query/Gps/Command services under test scope through
        // Visible/OwnedActivities and never call CanViewActivityAsync (a photo/race-only gate).
        public Task<bool> CanViewActivityAsync(TrackId trackId) => Task.FromResult(true);
    }
}
