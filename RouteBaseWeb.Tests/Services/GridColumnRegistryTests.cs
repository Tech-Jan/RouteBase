using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Tests for the generic grid column registry dispatch (<see cref="GridColumnRegistry{TEntity}"/>) via the
/// activities instance. Uses in-memory SQLite so the real filter/sort SQL (incl. unit scaling) is exercised.
/// </summary>
public class GridColumnRegistryTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private readonly ActivityGridColumns _reg = ActivityGridColumns.Instance;

    public async Task InitializeAsync()
    {
        var dbName = $"gridreg_test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;

        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser
        {
            UserId = "u1", DisplayName = "U1", Email = "u1@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });

        _context.Activities.AddRange(
            new Activity { TrackId = new TrackId(1), UserId = "u1", TrackName = "Morning Run",   ActivityType = "Run",  TotalDistance = 5_000,  TotalAscent = 50,   StartDateTimeLocal = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Unspecified),   Visibility = "private" },
            new Activity { TrackId = new TrackId(2), UserId = "u1", TrackName = "Evening Ride",   ActivityType = "Ride", TotalDistance = 40_000, TotalAscent = 600,  StartDateTimeLocal = new DateTime(2024, 2, 15, 18, 0, 0, DateTimeKind.Unspecified), Visibility = "public" },
            new Activity { TrackId = new TrackId(3), UserId = "u1", TrackName = "Trail run loop", ActivityType = "Run",  TotalDistance = 12_000, TotalAscent = 300,  StartDateTimeLocal = new DateTime(2024, 3, 20, 6, 0, 0, DateTimeKind.Unspecified),   Visibility = "friends" },
            new Activity { TrackId = new TrackId(4), UserId = "u1", TrackName = "50% effort day", ActivityType = "Walk", TotalDistance = 3_000,  TotalAscent = null, StartDateTimeLocal = new DateTime(2024, 3, 20, 23, 30, 0, DateTimeKind.Unspecified), Visibility = "friends" }
        );
        await _context.SaveChangesAsync();

        // Photos for the "photos" CountColumn: track 1 → 3, track 2 → 1, tracks 3 & 4 → none.
        // Saved after the activities so the track_id FK resolves.
        _context.Photos.AddRange(
            new Photo { PhotoId = new PhotoId(1), TrackId = new TrackId(1), ImportedUtc = DateTime.UtcNow },
            new Photo { PhotoId = new PhotoId(2), TrackId = new TrackId(1), ImportedUtc = DateTime.UtcNow },
            new Photo { PhotoId = new PhotoId(3), TrackId = new TrackId(1), ImportedUtc = DateTime.UtcNow },
            new Photo { PhotoId = new PhotoId(4), TrackId = new TrackId(2), ImportedUtc = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    private IQueryable<Activity> Q => _context.Activities.AsQueryable();

    private static Dictionary<string, ColumnFilter> One(string key, ColumnFilter value) => new(StringComparer.Ordinal) { [key] = value };

    private async Task<List<int>> FilteredIds(Dictionary<string, ColumnFilter> filters) =>
        (await _reg.ApplyFilters(Q, filters).Select(a => a.TrackId).ToListAsync()).Select(t => t.Value).OrderBy(x => x).ToList();

    // ── ApplyFilters dispatch by data type ───────────────────────────────────────

    [Fact]
    public async Task ApplyFilters_Text_WildcardContains()
    {
        var ids = await FilteredIds(One("name", new ColumnFilter { Text = "run" }));
        Assert.Equal(new[] { 1, 3 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_NumberRange_ScalesKmToMetres()
    {
        // distance column has a km→m scale, so "5".."12" km matches the 5 000 m and 12 000 m rows.
        var ids = await FilteredIds(One("distance", new ColumnFilter { Min = "5", Max = "12" }));
        Assert.Equal(new[] { 1, 3 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_NumberRange_NullColumnNeverMatches()
    {
        // ascent has no scale; TrackId 4 has null ascent and must be excluded.
        var ids = await FilteredIds(One("ascent", new ColumnFilter { Min = "0", Max = "10000" }));
        Assert.Equal(new[] { 1, 2, 3 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_NumberRangeExpr_MultiRange_ScalesKmToMetres()
    {
        // distance "3-6,8-12" km → [3000,6000] (rows 1=5000, 4=3000) OR [8000,12000] (row 3=12000).
        var ids = await FilteredIds(One("distance", new ColumnFilter { Range = "3-6,8-12" }));
        Assert.Equal(new[] { 1, 3, 4 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_NumberRangeExpr_OpenEnded()
    {
        var ge = await FilteredIds(One("distance", new ColumnFilter { Range = ">5" }));   // ≥5 km
        Assert.Equal(new[] { 1, 2, 3 }, ge);

        var le = await FilteredIds(One("distance", new ColumnFilter { Range = "-5" }));   // ≤5 km
        Assert.Equal(new[] { 1, 4 }, le);
    }

    [Fact]
    public async Task ApplyFilters_NumberRangeExpr_NullColumnNeverMatches()
    {
        var ids = await FilteredIds(One("ascent", new ColumnFilter { Range = "0-100000" }));
        Assert.Equal(new[] { 1, 2, 3 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_NumberRangeExpr_BadExpression_IsNoOp()
    {
        var ids = await FilteredIds(One("distance", new ColumnFilter { Range = "abc" }));
        Assert.Equal(new[] { 1, 2, 3, 4 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_PhotoCount_AtLeastOne_MatchesActivitiesWithPhotos()
    {
        // ">1" is ≥1 in this grammar (the >/>= prefix is inclusive) → "has photos": tracks 1 (3) and 2 (1),
        // via the COUNT subquery over Activity.Photos. Exercises the multi-range expression path.
        var ids = await FilteredIds(One("photos", new ColumnFilter { Range = ">1" }));
        Assert.Equal(new[] { 1, 2 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_PhotoCount_MinTwo_MatchesOnlyWellPhotographed()
    {
        // Legacy two-box min (≥2), no unit scale on a raw count → only track 1 (3 photos).
        var ids = await FilteredIds(One("photos", new ColumnFilter { Min = "2" }));
        Assert.Equal(new[] { 1 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_MultiSelect_MatchesMembers()
    {
        var ids = await FilteredIds(One("type", new ColumnFilter { In = { "Run", "Walk" } }));
        Assert.Equal(new[] { 1, 3, 4 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_Visibility_MultiSelect_MatchesMembers()
    {
        // visibility is a multi-select IN column: private (t1) OR public (t2), excluding the two friends rows.
        var ids = await FilteredIds(One("visibility", new ColumnFilter { In = { "private", "public" } }));
        Assert.Equal(new[] { 1, 2 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_DateRange_IsInclusiveOfWholeEndDay()
    {
        // To = the same day; the 23:30 activity must still match (upper bound advanced one day).
        var ids = await FilteredIds(One("date", new ColumnFilter { From = "2024-03-20", To = "2024-03-20" }));
        Assert.Equal(new[] { 3, 4 }, ids);
    }

    // ── Security / robustness ────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyFilters_UnknownKey_IsIgnored()
    {
        var ids = await FilteredIds(One("bogus", new ColumnFilter { Text = "x" }));
        Assert.Equal(new[] { 1, 2, 3, 4 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_NonFilterableColumn_IsIgnored()
    {
        // owner is sort-only (Kind None) → a value on it must not filter.
        var ids = await FilteredIds(One("owner", new ColumnFilter { Text = "u1" }));
        Assert.Equal(new[] { 1, 2, 3, 4 }, ids);
    }

    [Fact]
    public async Task ApplyFilters_EmptyValue_IsNoOp()
    {
        var ids = await FilteredIds(One("name", new ColumnFilter()));
        Assert.Equal(new[] { 1, 2, 3, 4 }, ids);
    }

    // ── ApplySort ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplySort_Distance_Ascending()
    {
        var ids = (await _reg.ApplySort(Q, "distance", asc: true).Select(a => a.TrackId).ToListAsync()).Select(t => t.Value).ToList();
        Assert.Equal(new[] { 4, 1, 3, 2 }, ids);
    }

    [Fact]
    public async Task ApplySort_PhotoCount_Descending_OrdersByCount()
    {
        // Counts: t1=3, t2=1, t3=0, t4=0. Descending puts the photographed tracks first; the two
        // zero-count tracks tie (SQL order among ties is unspecified), so assert their set only.
        var ids = (await _reg.ApplySort(Q, "photos", asc: false).Select(a => a.TrackId).ToListAsync()).Select(t => t.Value).ToList();
        Assert.Equal(1, ids[0]);
        Assert.Equal(2, ids[1]);
        Assert.Equal(new[] { 3, 4 }, ids.Skip(2).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task ApplySort_UnknownKey_FallsBackToDateDescending()
    {
        var ids = (await _reg.ApplySort(Q, "not-a-column", asc: false).Select(a => a.TrackId).ToListAsync()).Select(t => t.Value).ToList();
        Assert.Equal(new[] { 4, 3, 2, 1 }, ids);
    }

    [Fact]
    public async Task ApplySort_NonSortableFallback_UsesDefault()
    {
        // Even a registered-but-unknown direction resolves; here a real sortable column sorts as asked.
        var ids = (await _reg.ApplySort(Q, "date", asc: true).Select(a => a.TrackId).ToListAsync()).Select(t => t.Value).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4 }, ids);
    }

    // ── Frontend ⇄ backend key contract ──────────────────────────────────────────

    [Theory]
    // Mirrors the filter/sort column keys declared in src/js/overview/activities-grid.ts buildColumns()
    // (excluding the presentation-only "actions" column). If the frontend adds a filterable/sortable column,
    // its key must be registered here or its filter/sort would be silently ignored by the allowlist.
    [InlineData("date")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("visibility")]
    [InlineData("owner")]
    [InlineData("distance")]
    [InlineData("ascent")]
    [InlineData("time")]
    [InlineData("avgspeed")]
    [InlineData("maxspeed")]
    [InlineData("descent")]
    [InlineData("avghr")]
    [InlineData("maxhr")]
    [InlineData("avgcadence")]
    [InlineData("avgpower")]
    [InlineData("movingtime")]
    [InlineData("photos")]
    public void Registry_Contains_EveryFrontendColumnKey(string key)
    {
        Assert.True(_reg.Contains(key), $"Activity grid column '{key}' is used by the frontend but not registered.");
    }
}
