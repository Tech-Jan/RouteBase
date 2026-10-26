using System.Globalization;
using RouteBaseWeb.Api;
using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the <see cref="ActivitiesApiController"/> contract: grid paging + the three ID/count sets
/// (filteredIds / ownerIds / total / unfilteredTotal), owner filter + label resolution ("Me" / display
/// name / enriched non-friend), visibility scoping on every GPS read endpoint, PATCH validation +
/// normalization, DELETE as soft-trash, and refresh-elevation's ownership gate + result branches.
/// Real query/gps/command services over named in-memory SQLite (like ActivityServiceTests); only
/// IElevationRefreshService is faked — the real one talks to a DEM source.
/// </summary>
public class ActivitiesApiControllerTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Friend = "friend";
    private const string Stranger = "stranger";

    private string _conn = null!;
    private RouteBaseDbContext _context = null!;
    private IMemoryCache _cache = null!;
    private GpsCoordinateCache _gpsCache = null!;
    private UserCacheInvalidator _invalidator = null!;
    private SingleFlight _singleFlight = null!;
    private ContentTrackReader _trackReader = null!;
    private ServiceProvider _provider = null!;
    private int _nextTrackId = 1;

    public async Task InitializeAsync()
    {
        _conn = $"Data Source=file:activities_api_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schema = new DbSchemaManager(_context);
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureRecordingStagingTableAsync();
        await schema.EnsureActivityBboxTableAsync();

        foreach (var id in new[] { Me, Friend, Stranger })
            _context.Users.Add(new AppUser { UserId = id, DisplayName = id, Email = $"{id}@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        _cache = new MemoryCache(new MemoryCacheOptions());
        _gpsCache = new GpsCoordinateCache(new MemoryBudget().CoordinateCachePoints);
        _invalidator = new UserCacheInvalidator();
        _singleFlight = new SingleFlight();
        _provider = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddDbContext<RouteBaseDbContext>(o => o.UseSqlite(_conn))
            .AddScoped<ITrackObjectStore, TrackObjectStore>()
            .AddScoped<TrackContentStore>()
            .AddScoped<RecordingStagingStore>()
            .BuildServiceProvider();
        _trackReader = new ContentTrackReader(
            new TrackReaderCache(64L * 1024 * 1024), _singleFlight,
            _provider.GetRequiredService<IServiceScopeFactory>());
    }

    public Task DisposeAsync()
    {
        _cache.Dispose();
        _gpsCache.Dispose();
        _provider.Dispose();
        return _context.DisposeAsync().AsTask();
    }

    // ─── Construction ───

    private ActivitiesApiController ControllerFor(
        IVisibilityService vis, string userId, FakeElevationRefresh? elevation = null)
    {
        var currentUser = new TestCurrentUserService(userId);
        var query   = new ActivityQueryService(_context, vis, _cache, _invalidator);
        var coalescer = new GpsCoordinateCoalescer(_singleFlight, _trackReader, _gpsCache);
        var gps     = new ActivityGpsService(_context, _gpsCache, vis, _invalidator, coalescer, _trackReader);
        var command = new ActivityCommandService(_context, vis, _invalidator,
            NullLogger<ActivityCommandService>.Instance, TestLifecycle.For(_context, vis, _invalidator));
        var users   = new UserService(_context, NullLogger<UserService>.Instance, TimeProvider.System);
        var matcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, new DirectTrackReader(_context));
        var ownerScope = new OwnerScopeService(currentUser, vis, users);
        return new ActivitiesApiController(query, gps, command, elevation ?? new FakeElevationRefresh(),
            vis, users, currentUser, _invalidator, matcher, ownerScope,
            NullLogger<ActivitiesApiController>.Instance, Options.Create(new JsonOptions()))
        {
            // The GPS GET endpoints now read If-None-Match / write ETag + Cache-Control, so they need a
            // real HttpContext (the conditional-GET path NPEs on ControllerBase.Response otherwise).
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    /// <summary>Single-user view: only the user's own released activities.</summary>
    private ActivitiesApiController SoloController(FakeElevationRefresh? elevation = null) =>
        ControllerFor(new TestVisibilityService(Me), Me, elevation);

    /// <summary>Multi-user view for Me with the given friends (public activities of anyone also visible).</summary>
    private ActivitiesApiController SocialController(params string[] friends) =>
        ControllerFor(new SocialVisibility(Me, friends), Me);

    private async Task<int> AddActivityAsync(
        string owner, string? type = null, DateTime? localStart = null, string visibility = "private",
        string? name = null, double distance = 0, double ascent = 0,
        (double sLat, double sLng, double eLat, double eLng)? endpoints = null)
    {
        int id = _nextTrackId++;
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(id), UserId = owner, ActivityType = type, TrackName = name,
            StartDateTimeLocal = localStart, StartDateTimeUtc = localStart,
            TotalDistance = distance, TotalAscent = ascent, Visibility = visibility,
            StartGpsLat = endpoints?.sLat, StartGpsLong = endpoints?.sLng,
            EndGpsLat = endpoints?.eLat, EndGpsLong = endpoints?.eLng,
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private Task SeedGpsAsync(TrackId trackId) => TrackSeed.SeedTrackAsync(_context, trackId,
        new[]
        {
            (47.0, 11.0, new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            (47.001, 11.0, new DateTime(2025, 1, 1, 8, 0, 1, DateTimeKind.Utc)),
            (47.002, 11.0, new DateTime(2025, 1, 1, 8, 0, 2, DateTimeKind.Utc)),
        });

    // A released Me-track with GPS content (read by the shape matcher's DirectTrackReader) at (lat,lng) and
    // its Activity_Bbox row (the matcher's Stage-1 candidate gate) — for the "Merge table ↔ shapes" grid tests.
    private async Task<int> AddGeoTrackAsync(double lat, double lng, double distanceMetres = 0)
    {
        int id = _nextTrackId++;
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(id), UserId = Me, Visibility = "private", Lifecycle = LifecycleStates.Released,
            StartDateTimeLocal = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Unspecified), TotalDistance = distanceMetres,
        });
        await _context.SaveChangesAsync();
        await TrackSeed.SeedTrackAsync(_context, new TrackId(id), new[]
        {
            (lat,         lng, new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            (lat + 0.001, lng, new DateTime(2025, 1, 1, 8, 0, 1, DateTimeKind.Utc)),
            (lat + 0.002, lng, new DateTime(2025, 1, 1, 8, 0, 2, DateTimeKind.Utc)),
        });
        await _context.Database.ExecuteSqlRawAsync(
            @"INSERT OR IGNORE INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long)
              VALUES ({0}, {1}, {2}, {3}, {3})", id, lat, lat + 0.002, lng);
        return id;
    }

    // A zone rectangle around (47.00, 11.00); tracks whose GPS falls inside pass its passes-all membership.
    private static string ZoneNear47() => new RuleDocument
    {
        Shapes = { new ShapeDef { No = 1, Geom = new RectGeom { South = 46.990, West = 10.990, North = 47.010, East = 11.010 } } },
        Membership = { new PassesAllRule { Zones = { 1 } } },
    }.Serialize();

    // Case-INSENSITIVE on purpose: reads today's anonymous bodies (camelCase) and the named DTOs of the
    // parallel dto-controller branch (PascalCase records, e.g. ActivityGridResponse in 51db587) alike, so
    // these tests survive whichever branch merges first. Wire casing is pinned there, not here.
    private static object? Prop(object o, string name) => o.GetType().GetProperty(name,
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.IgnoreCase)!.GetValue(o);

    // The grid payload, unpacked once so every test reads typed fields.
    private sealed record GridResponse(
        IReadOnlyList<ActivityRowDto> Rows, IReadOnlyList<int> FilteredIds, IReadOnlyList<int> OwnerIds,
        int Total, int UnfilteredTotal);

    private static GridResponse ReadGrid(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var v = ok.Value!;
        return new GridResponse(
            (IReadOnlyList<ActivityRowDto>)Prop(v, "rows")!,
            (IReadOnlyList<int>)Prop(v, "filteredIds")!,
            (IReadOnlyList<int>)Prop(v, "ownerIds")!,
            (int)Prop(v, "total")!,
            (int)Prop(v, "unfilteredTotal")!);
    }

    // The GPS GET endpoints now serve a cacheable JSON body via File() (ETag + max-age), not Ok(dto).
    // Decode the raw bytes back to a JSON array so the point/coordinate assertions read the wire form.
    private static List<JsonElement> ReadJsonArray(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", file.ContentType);
        return JsonSerializer.Deserialize<List<JsonElement>>(file.FileContents)!;
    }

    // ─── POST grid: shape filter ("Merge table ↔ shapes") ───

    [Fact]
    public async Task Grid_ShapeFilter_RestrictsRowsAndFilteredIds_ButNotOwnerIds()
    {
        int in1 = await AddGeoTrackAsync(47.000, 11.000);
        int in2 = await AddGeoTrackAsync(47.001, 11.000);
        int outside = await AddGeoTrackAsync(48.500, 12.500); // far from the zone

        var grid = ReadGrid(await SoloController().QueryGrid(new ActivityGridRequest { Shape = ZoneNear47() }));

        // Rows + filteredIds narrow to the tracks the shape actually matches (GPS point-inside).
        Assert.Equal(new[] { in1, in2 }, grid.FilteredIds.OrderBy(i => i));
        Assert.Equal(new[] { in1, in2 }, grid.Rows.Select(r => r.TrackId).OrderBy(i => i));
        Assert.DoesNotContain(outside, grid.FilteredIds);
        // ownerIds stays the full display universe — the map keeps every track, only dims to filteredIds.
        Assert.Equal(new[] { in1, in2, outside }, grid.OwnerIds.OrderBy(i => i));
    }

    [Fact]
    public async Task Grid_ShapeFilter_IntersectsWithColumnFilters()
    {
        int near = await AddGeoTrackAsync(47.000, 11.000, distanceMetres: 3000);
        int far  = await AddGeoTrackAsync(47.001, 11.000, distanceMetres: 20000); // in the zone, but too long

        var req = new ActivityGridRequest { Shape = ZoneNear47() };
        req.Filters.Items["distance"] = new ColumnFilter { Range = "0-5" }; // km → ≤5000 m

        var grid = ReadGrid(await SoloController().QueryGrid(req));

        Assert.Equal(new[] { near }, grid.FilteredIds); // shape ∩ distance
        Assert.DoesNotContain(far, grid.FilteredIds);
    }

    [Fact]
    public async Task Grid_ShapeFilter_MatchingNothing_EmptiesTheTable()
    {
        await AddGeoTrackAsync(48.500, 12.500); // exists, but outside the zone

        var grid = ReadGrid(await SoloController().QueryGrid(new ActivityGridRequest { Shape = ZoneNear47() }));

        Assert.Empty(grid.FilteredIds);
        Assert.Empty(grid.Rows);
        Assert.Single(grid.OwnerIds); // still in the universe
    }

    [Fact]
    public async Task Grid_ShapeFilter_HonoursCancellation()
    {
        // An abandoned grid request must not run the exact matcher's per-track decode loop to
        // completion — the token has to reach PreviewExactTrackIdsAsync (same as preview-exact, SB-03).
        await AddGeoTrackAsync(47.000, 11.000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SoloController().QueryGrid(new ActivityGridRequest { Shape = ZoneNear47() }, cts.Token));
    }

    // ─── POST grid ───

    [Fact]
    public async Task Grid_ReturnsRowsAndAllFourCountSets()
    {
        var a = await AddActivityAsync(Me, type: "Ride", localStart: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), name: "one");
        var b = await AddActivityAsync(Me, type: "Run",  localStart: new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Unspecified), name: "two");

        var grid = ReadGrid(await SoloController().QueryGrid(new ActivityGridRequest()));

        Assert.Equal(2, grid.Rows.Count);
        Assert.Equal(2, grid.Total);
        Assert.Equal(2, grid.UnfilteredTotal);
        Assert.Equal(new[] { a, b }, grid.FilteredIds.OrderBy(i => i));
        // No column filter active → the map universe IS the filtered set.
        Assert.Equal(grid.FilteredIds, grid.OwnerIds);
        var row = grid.Rows.Single(r => r.TrackId == a);
        Assert.Equal("one", row.Name);
        Assert.Equal("Ride", row.ActivityType);
        Assert.Equal("Me", row.Owner);
        Assert.True(row.CanDelete);
    }

    [Fact]
    public async Task Grid_SecondPage_ReturnsOldestByDateDesc()
    {
        var ids = new List<int>();
        for (int month = 1; month <= 12; month++)
            ids.Add(await AddActivityAsync(Me, localStart: new DateTime(2025, month, 1, 0, 0, 0, DateTimeKind.Unspecified)));

        var grid = ReadGrid(await SoloController().QueryGrid(
            new ActivityGridRequest { Page = 2, PageSize = 10 }));

        Assert.Equal(12, grid.Total);
        Assert.Equal(12, grid.FilteredIds.Count); // ID set is the FULL filtered set, not the page
        // Default sort is date desc → page 2 of 10 holds the two oldest, newest first.
        Assert.Equal(new[] { ids[1], ids[0] }, grid.Rows.Select(r => r.TrackId));
    }

    [Fact]
    public async Task Grid_Total_SharesTheFilteredIdCount_UnderFilterAndPaging()
    {
        // perf-5: the paged query no longer runs its own COUNT — it reuses filteredIds.Count. Total must
        // still be the FULL filtered match count (across all pages), never the page size, under an active
        // filter, so this pins the shared count exactly where the two used to be computed independently.
        // 12 matching rows + a page size clamped to 10 → the page is a strict subset of the filtered set.
        for (int month = 1; month <= 12; month++)
            await AddActivityAsync(Me, localStart: new DateTime(2025, month, 1, 0, 0, 0, DateTimeKind.Unspecified));
        await AddActivityAsync(Me, localStart: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)); // excluded by the date filter

        var grid = ReadGrid(await SoloController().QueryGrid(
            new ActivityGridRequest { StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), Page = 1, PageSize = 10 }));

        Assert.Equal(10, grid.Rows.Count);                // page is capped at PageSize
        Assert.Equal(12, grid.FilteredIds.Count);         // full filtered set (2024 row excluded)
        Assert.Equal(grid.FilteredIds.Count, grid.Total); // total is the shared count, not the page size
    }

    [Fact]
    public async Task Grid_NonPositivePageSize_ReturnsEverything()
    {
        await AddActivityAsync(Me);
        await AddActivityAsync(Me);

        var grid = ReadGrid(await SoloController().QueryGrid(new ActivityGridRequest { PageSize = 0 }));

        Assert.Equal(2, grid.Rows.Count);
    }

    [Fact]
    public async Task Grid_FriendRow_GetsDisplayNameAndNoDelete()
    {
        await AddActivityAsync(Me);
        var theirs = await AddActivityAsync(Friend, visibility: "friends");

        var grid = ReadGrid(await SocialController(Friend).QueryGrid(new ActivityGridRequest()));

        var friendRow = grid.Rows.Single(r => r.TrackId == theirs);
        Assert.Equal(Friend, friendRow.Owner);   // seeded DisplayName == user id
        Assert.False(friendRow.CanDelete);
        Assert.True(grid.Rows.Single(r => r.TrackId != theirs).CanDelete);
    }

    [Fact]
    public async Task Grid_OwnerFilterOthers_ExcludesMyRows()
    {
        await AddActivityAsync(Me);
        var theirs = await AddActivityAsync(Friend, visibility: "friends");

        var request = new ActivityGridRequest();
        request.Filters.GetOrAdd("owner").In.Add(OwnerFilterResolver.Others);
        var grid = ReadGrid(await SocialController(Friend).QueryGrid(request));

        Assert.Equal(new[] { theirs }, grid.Rows.Select(r => r.TrackId));
        Assert.Equal(1, grid.Total);
        Assert.Equal(2, grid.UnfilteredTotal); // grand total stays owner-unfiltered
    }

    [Fact]
    public async Task Grid_OwnerFilterMatchingNobody_ReturnsNoRowsNotAll()
    {
        await AddActivityAsync(Me);
        await AddActivityAsync(Friend, visibility: "friends");

        var request = new ActivityGridRequest();
        request.Filters.GetOrAdd("owner").Text = "no-such-person";
        var grid = ReadGrid(await SocialController(Friend).QueryGrid(request));

        Assert.Empty(grid.Rows);
        Assert.Equal(0, grid.Total);
        Assert.Empty(grid.FilteredIds);
        Assert.Empty(grid.OwnerIds);
        Assert.Equal(2, grid.UnfilteredTotal);
    }

    [Fact]
    public async Task Grid_ColumnFilter_OwnerIdsKeepTheUnfilteredMapUniverse()
    {
        var january = await AddActivityAsync(Me, localStart: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
        var june    = await AddActivityAsync(Me, localStart: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Unspecified));

        var grid = ReadGrid(await SoloController().QueryGrid(
            new ActivityGridRequest { StartDate = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Unspecified) }));

        Assert.Equal(new[] { june }, grid.FilteredIds);
        // ownerIds ignores the date filter: it is the owner-scoped display universe the map dims from.
        Assert.Equal(new[] { january, june }, grid.OwnerIds.OrderBy(i => i));
    }

    [Fact]
    public async Task Grid_NonFriendPublicOwner_NameEnrichedOntoRow()
    {
        // Stranger is not in the visible-user set (not a friend), but their public ride shows in the
        // grid — EnrichOwnerNamesAsync must backfill the display name instead of leaving a blank label.
        var theirs = await AddActivityAsync(Stranger, visibility: "public");

        var grid = ReadGrid(await SocialController(/* no friends */).QueryGrid(new ActivityGridRequest()));

        var row = grid.Rows.Single(r => r.TrackId == theirs);
        Assert.Equal(Stranger, row.Owner);
        Assert.False(row.CanDelete);
    }

    // ─── POST gps-data ───

    [Fact]
    public async Task GpsData_EmptyIds_Returns400()
    {
        var result = await SoloController().GetGpsData(new GpsDataRequest(), default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GpsData_ReturnsTrackMetadataAndLatLngPairs()
    {
        var start = new DateTime(2025, 5, 10, 8, 30, 0, DateTimeKind.Unspecified);
        var id = await AddActivityAsync(Me, type: "Ride", localStart: start,
            name: "Morning Ride", distance: 1234.5, ascent: 42);
        await SeedGpsAsync(new TrackId(id));

        var result = await SoloController().GetGpsData(
            new GpsDataRequest { ActivityIds = new List<int> { id }, MaxPointsPerTrack = 0 }, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var track = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().Single();
        Assert.Equal(id, Prop(track, "id"));
        Assert.Equal("Morning Ride", Prop(track, "name"));
        // Invariant: the wire contract is Gregorian dd.MM.yyyy regardless of server culture
        // (a provider-less ToString here rebuilt the expectation in Buddhist years under th-TH).
        Assert.Equal(start.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), Prop(track, "date"));
        Assert.Equal(1234.5, Prop(track, "distance"));
        var points = (IReadOnlyList<double[]>)Prop(track, "points")!;
        Assert.Equal(3, points.Count);
        Assert.Equal(new[] { 47.0, 11.0 }, points[0]); // [lat, lng] pairs, not {lat,lng} objects
    }

    [Fact]
    public async Task GpsData_InvisibleTrack_SilentlyExcluded()
    {
        var mine = await AddActivityAsync(Me);
        await SeedGpsAsync(new TrackId(mine));
        var theirs = await AddActivityAsync(Friend); // private → invisible to the solo view
        await SeedGpsAsync(new TrackId(theirs));

        var result = await SoloController().GetGpsData(
            new GpsDataRequest { ActivityIds = new List<int> { mine, theirs } }, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var tracks = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Equal(new object?[] { mine }, tracks.Select(t => Prop(t, "id")));
    }

    [Fact]
    public async Task GpsData_TrackWithoutPoints_Skipped()
    {
        var id = await AddActivityAsync(Me); // no GPS seeded

        var result = await SoloController().GetGpsData(
            new GpsDataRequest { ActivityIds = new List<int> { id } }, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(((System.Collections.IEnumerable)ok.Value!).Cast<object>());
    }

    // ─── POST bounds ───

    [Fact]
    public async Task Bounds_EmptyIds_ReturnsEmptyList()
    {
        var result = await SoloController().GetActivityBounds(new ActivityEndpointsRequest(), default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(((System.Collections.IEnumerable)ok.Value!).Cast<object>());
    }

    [Fact]
    public async Task Bounds_ReturnsEndpointsForVisibleOnly()
    {
        var mine = await AddActivityAsync(Me, endpoints: (47.1, 11.2, 47.3, 11.4));
        var theirs = await AddActivityAsync(Friend, endpoints: (48.0, 12.0, 48.1, 12.1)); // invisible

        var result = await SoloController().GetActivityBounds(
            new ActivityEndpointsRequest { ActivityIds = new List<int> { mine, theirs } }, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var endpoint = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().Single();
        Assert.Equal(47.1, Prop(endpoint, "startLat"));
        Assert.Equal(11.2, Prop(endpoint, "startLng"));
        Assert.Equal(47.3, Prop(endpoint, "endLat"));
        Assert.Equal(11.4, Prop(endpoint, "endLng"));
    }

    // ─── GET {id}/gps + {id}/gps-summary ───

    [Fact]
    public async Task Gps_InvisibleActivity_Returns404()
    {
        var theirs = await AddActivityAsync(Friend);
        await SeedGpsAsync(new TrackId(theirs));

        var result = await SoloController().GetActivityGps(theirs, default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Gps_ReturnsAllPoints()
    {
        var id = await AddActivityAsync(Me);
        await SeedGpsAsync(new TrackId(id));

        var result = await SoloController().GetActivityGps(id, default);

        var points = ReadJsonArray(result);
        Assert.Equal(3, points.Count);
        Assert.Equal(47.0, points[0].GetProperty("gpsLat").GetDouble());
        Assert.Equal(11.0, points[0].GetProperty("gpsLong").GetDouble());
    }

    [Fact]
    public async Task Gps_VisibleButNoPoints_ReturnsEmptyList()
    {
        var id = await AddActivityAsync(Me);

        var result = await SoloController().GetActivityGps(id, default);

        Assert.Empty(ReadJsonArray(result));
    }

    // ─── GPS caching (ETag + short max-age) ───

    [Fact]
    public async Task Gps_FirstRequest_SetsEtagAndPrivateMaxAge()
    {
        var id = await AddActivityAsync(Me);
        await SeedGpsAsync(new TrackId(id));

        var controller = SoloController();
        var result = await controller.GetActivityGps(id, default);

        Assert.IsType<FileContentResult>(result);
        Assert.True(controller.Response.Headers.ContainsKey("ETag"));
        Assert.Contains("max-age=", controller.Response.Headers.CacheControl.ToString(), StringComparison.Ordinal);
        Assert.Contains("private", controller.Response.Headers.CacheControl.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gps_MatchingIfNoneMatch_Returns304()
    {
        var id = await AddActivityAsync(Me);
        await SeedGpsAsync(new TrackId(id));

        var first = SoloController();
        await first.GetActivityGps(id, default);
        var etag = first.Response.Headers.ETag.ToString();

        var second = SoloController();
        second.Request.Headers.IfNoneMatch = etag;
        var result = await second.GetActivityGps(id, default);

        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public async Task Gps_DifferentTrackContent_GetsDifferentEtag()
    {
        // Two tracks with different GPS → different bodies → different ETags. This is the same guarantee a
        // re-analysis relies on: change the content (a new head object), get a new ETag (never a stale 304).
        var a = await AddActivityAsync(Me);
        await SeedGpsAsync(new TrackId(a));
        var b = await AddGeoTrackAsync(48.5, 12.5); // seeds a different geometry

        var ca = SoloController();
        await ca.GetActivityGps(a, default);
        var cb = SoloController();
        await cb.GetActivityGps(b, default);

        Assert.NotEqual(ca.Response.Headers.ETag.ToString(), cb.Response.Headers.ETag.ToString(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task GpsSummary_InvisibleActivity_Returns404()
    {
        var theirs = await AddActivityAsync(Friend);
        await SeedGpsAsync(new TrackId(theirs));

        var result = await SoloController().GetActivityGpsSummary(theirs, default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GpsSummary_ReturnsCoordinatePairs()
    {
        var id = await AddActivityAsync(Me);
        await SeedGpsAsync(new TrackId(id));

        var result = await SoloController().GetActivityGpsSummary(id, default);

        var coords = ReadJsonArray(result);
        Assert.Equal(3, coords.Count);
        Assert.Equal(47.0, coords[0].GetProperty("lat").GetDouble());
        Assert.Equal(11.0, coords[0].GetProperty("lng").GetDouble());
    }

    [Fact]
    public async Task GpsSummary_MatchingIfNoneMatch_Returns304()
    {
        var id = await AddActivityAsync(Me);
        await SeedGpsAsync(new TrackId(id));

        var first = SoloController();
        await first.GetActivityGpsSummary(id, default);
        var etag = first.Response.Headers.ETag.ToString();

        var second = SoloController();
        second.Request.Headers.IfNoneMatch = etag;
        var result = await second.GetActivityGpsSummary(id, default);

        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    // ─── PATCH {id} ───

    [Fact]
    public async Task Update_NoFields_Returns400()
    {
        var result = await SoloController().UpdateActivity(1, new UpdateActivityRequest());
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_WhitespaceName_Returns400()
    {
        var result = await SoloController().UpdateActivity(1, new UpdateActivityRequest { Name = "   " });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_WhitespaceActivityType_Returns400()
    {
        var result = await SoloController().UpdateActivity(1, new UpdateActivityRequest { ActivityType = " " });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_UnknownVisibility_Returns400()
    {
        var result = await SoloController().UpdateActivity(1, new UpdateActivityRequest { Visibility = "bogus" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_NonOwner_Returns404()
    {
        var theirs = await AddActivityAsync(Friend);

        var result = await SoloController().UpdateActivity(theirs, new UpdateActivityRequest { Name = "hijack" });

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Null(await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == new TrackId(theirs)).Select(a => a.TrackName).FirstAsync());
    }

    [Fact]
    public async Task Update_Owner_TrimsAndLowercasesThenPersists()
    {
        var id = await AddActivityAsync(Me, name: "old");

        var result = await SoloController().UpdateActivity(id,
            new UpdateActivityRequest { Name = "  New Name  ", Visibility = "Public" });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("New Name", Prop(ok.Value!, "name"));
        Assert.Equal("public", Prop(ok.Value!, "visibility")); // echoes the NORMALIZED values
        var stored = await _context.Activities.AsNoTracking().FirstAsync(a => a.TrackId == new TrackId(id));
        Assert.Equal("New Name", stored.TrackName);
        Assert.Equal("public", stored.Visibility);
    }

    // ─── DELETE {id} ───

    [Fact]
    public async Task Delete_NonOwner_Returns404()
    {
        var theirs = await AddActivityAsync(Friend);

        var result = await SoloController().DeleteActivity(theirs);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(LifecycleStates.Released, await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == new TrackId(theirs)).Select(a => a.Lifecycle).FirstAsync());
    }

    [Fact]
    public async Task Delete_Owner_Returns204AndSoftTrashes()
    {
        var id = await AddActivityAsync(Me);
        _context.ChangeTracker.Clear();

        var result = await SoloController().DeleteActivity(id);

        Assert.IsType<NoContentResult>(result);
        // Soft trash, not a hard delete: the row survives with Lifecycle=trashed (purge lives in /api/trash).
        Assert.Equal(LifecycleStates.Trashed, await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == new TrackId(id)).Select(a => a.Lifecycle).FirstAsync());
    }

    // ─── POST {id}/refresh-elevation ───

    [Fact]
    public async Task RefreshElevation_VisibleButNotOwned_Returns404WithoutRefreshing()
    {
        // Friend's ride is VISIBLE to me, but refresh mutates the owner's data → ownership gate, not visibility.
        var theirs = await AddActivityAsync(Friend, visibility: "friends");
        var fake = new FakeElevationRefresh { Result = new ElevationRefreshResult(1, 1) };

        var result = await ControllerFor(new SocialVisibility(Me, Friend), Me, fake).RefreshElevation(theirs);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task RefreshElevation_NoGpsPoints_Returns400()
    {
        var id = await AddActivityAsync(Me);
        var fake = new FakeElevationRefresh { Result = null }; // service contract: null = nothing refreshed

        var result = await SoloController(fake).RefreshElevation(id);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RefreshElevation_Success_ReportsCountsAndEvictsOwnerCache()
    {
        var id = await AddActivityAsync(Me);
        var fake = new FakeElevationRefresh { Result = new ElevationRefreshResult(10, 8) };
        var token = _invalidator.GetChangeToken(Me);

        var result = await SoloController(fake).RefreshElevation(id);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(10, Prop(ok.Value!, "pointsTotal"));
        Assert.Equal(8, Prop(ok.Value!, "pointsReplaced")); // honest count — 0 would mean DEM yielded nothing
        Assert.True(token.HasChanged); // cached slices/coords evicted so the refresh is visible immediately
    }

    [Fact]
    public async Task RefreshElevation_ActiveRecordingSession_Returns409WithReason()
    {
        var id = await AddActivityAsync(Me);
        var fake = new FakeElevationRefresh { Throws = new InvalidOperationException("session still open") };

        var result = await SoloController(fake).RefreshElevation(id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("session still open", Prop(conflict.Value!, "message")); // D32: reason now in `message`
    }

    // ─── Doubles ───

    private sealed class FakeElevationRefresh : IElevationRefreshService
    {
        public ElevationRefreshResult? Result { get; set; }
        public Exception? Throws { get; set; }
        public int Calls { get; private set; }

        public Task<ElevationRefreshResult?> RefreshElevationAsync(TrackId trackId)
        {
            Calls++;
            if (Throws != null) throw Throws;
            return Task.FromResult(Result);
        }
    }

    /// <summary>
    /// Multi-user visibility double mirroring the production rules the controller depends on: the
    /// lifecycle gate first, then own rows + friends' friends/public rows + ANYONE's public rows.
    /// GetVisibleUserIdsAsync deliberately returns only me + friends (not public strangers) — that gap
    /// is exactly what the controller's EnrichOwnerNamesAsync exists to fill.
    /// </summary>
    private sealed class SocialVisibility : IVisibilityService
    {
        private readonly string _me;
        private readonly HashSet<string> _friends;

        public SocialVisibility(string me, params string[] friends)
        {
            _me = me;
            _friends = new HashSet<string>(friends, StringComparer.Ordinal);
        }

        public string GetCurrentUserId() => _me;

        public IQueryable<Activity> VisibleActivities(IQueryable<Activity> q) =>
            q.Where(a => a.Lifecycle == LifecycleStates.Released &&
                (a.UserId == _me
                 || a.Visibility == "public"
                 || (_friends.Contains(a.UserId!) && a.Visibility == "friends")));

        public IQueryable<Activity> OwnedActivities(IQueryable<Activity> q) => q.Where(a => a.UserId == _me);
        public IQueryable<Segment> VisibleSegments(IQueryable<Segment> q) => q.Where(s => s.UserId == _me);
        public IQueryable<Segment> OwnedSegments(IQueryable<Segment> q) => q.Where(s => s.UserId == _me);
        public IQueryable<MultiDayTour> VisibleTours(IQueryable<MultiDayTour> q) => q.Where(t => t.UserId == _me);
        public IQueryable<MultiDayTour> OwnedTours(IQueryable<MultiDayTour> q) => q.Where(t => t.UserId == _me);
        public IQueryable<AppCredentials> OwnedCredentials(IQueryable<AppCredentials> q) => q.Where(c => c.UserId == _me);

        public Task<List<string>> GetVisibleUserIdsAsync()
        {
            var ids = new List<string> { _me };
            ids.AddRange(_friends);
            return Task.FromResult(ids);
        }

        public Task<bool> CanViewItemAsync(string ownerUserId, string? visibility)
            => Task.FromResult(ownerUserId == _me || visibility == "public"
                || (visibility == "friends" && _friends.Contains(ownerUserId)));

        // Permissive is deliberate and unreachable: ActivitiesApiController's endpoints scope every read
        // through Visible/OwnedActivities and never call CanViewActivityAsync — that gate lives on the
        // photo and race surfaces. Wire the real VisibilityService if an endpoint here ever needs it.
        public Task<bool> CanViewActivityAsync(TrackId trackId) => Task.FromResult(true);
    }
}
