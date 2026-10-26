using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// first direct tests for <see cref="SegmentEffortPerformanceBuilder"/>. It is a pure GPS→wire
/// projection over efforts the caller has ALREADY authorized, so it is fed hand-built (row, activity) pairs
/// and its exact output fields are asserted. The mutation baseline had 24 survivors here and no dedicated
/// test file; the builder's field projection, its drop guards (null decode / empty slice), the estimated-vs-
/// measured cost flag, the rider-mass floor, and the distance/elapsed accumulation were all unasserted.
/// </summary>
public class SegmentEffortPerformanceBuilderTests : IAsyncLifetime
{
    private const string User = "u1";
    private static readonly DateTime T0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private RouteBaseDbContext _context = null!;
    private DirectTrackReader _reader = null!;
    private MemoryCache _cache = null!;
    private SegmentCache _segCache = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:effbuilder_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options);
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureTrackContentTablesAsync();

        _reader   = new DirectTrackReader(_context);
        _cache    = new MemoryCache(new MemoryCacheOptions());
        _segCache = new SegmentCache(_cache, new TestVisibilityService(User), new UserCacheInvalidator());
    }

    public async Task DisposeAsync()
    {
        _cache.Dispose();
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    private SegmentEffortPerformanceBuilder Builder(IProfileSettingsService? settings = null) =>
        new(_context, _cache, _segCache, settings ?? new FixedMassSettings(75.0), _reader);

    // Five quantization-exact points 10 s apart, 100 m / 1 m·s⁻¹ apart, valid coords.
    private static List<GpsData> StandardPoints() =>
        Enumerable.Range(0, 5).Select(i => new GpsData
        {
            DateTimeUtc = T0.AddSeconds(i * 10),
            GpsLat = 47.000 + i * 0.001, GpsLong = 11.000,
            Distance = i * 100, Speed = 1.0 + i, Altitude = 100 + i * 10,
        }).ToList();

    private async Task SeedTrackAsync(int trackId, IReadOnlyList<GpsData> pts)
    {
        // A bare Activity row satisfies the track-version FK; the (row, activity) pair passed to BuildAsync
        // carries the projection fields and is independent of this DB row (UserId left null → no user FK).
        _context.Activities.Add(new Activity { TrackId = new TrackId(trackId), UserId = null });
        await _context.SaveChangesAsync();
        await TrackSeed.SeedTrackAsync(_context, new TrackId(trackId), pts);
    }

    private static Activity Act(
        int trackId, string? name = "Morning", string? userId = User,
        AppUser? user = null, string? type = null, DateTime? start = null) => new()
    {
        TrackId = new TrackId(trackId), TrackName = name, UserId = userId,
        User = user, ActivityType = type, StartDateTimeLocal = start,
    };

    private static GpsInSegment Effort(int trackId, int entry, int exit, int effortId = 1) => new()
    {
        GpsInSegmentId = new GpsInSegmentId(effortId), TrackId = new TrackId(trackId),
        GpsIdEntryCalculated = entry, GpsIdExitCalculated = exit,
    };

    private Task<List<EffortPerformanceData>> BuildAsync(
        IReadOnlyList<(GpsInSegment, Activity)> authorized, SegmentId? segId = null,
        IProfileSettingsService? settings = null, int maxPoints = 0) =>
        Builder(settings).BuildAsync(segId ?? new SegmentId(1), authorized, maxPoints);

    // ---------------------------------------------------------------- field projection

    [Fact]
    public async Task Build_ProjectsCoreIdentityFields()
    {
        await SeedTrackAsync(1, StandardPoints());
        var data = await BuildAsync(new[] { (Effort(1, 0, 4, effortId: 42), Act(1, name: "Morning", userId: "u1")) });

        var epd = Assert.Single(data);
        Assert.Equal(42, epd.EffortId);
        Assert.Equal(1, epd.ActivityId);
        Assert.Equal("Morning", epd.ActivityName);   // mut 97: not the "Unknown" fallback
        Assert.Equal("u1", epd.UserId);
    }

    // Mut 97 (`?? "Unknown"` string → ""): a nameless activity falls back to the literal "Unknown".
    [Fact]
    public async Task Build_NullActivityName_FallsBackToUnknown()
    {
        await SeedTrackAsync(1, StandardPoints());
        var data = await BuildAsync(new[] { (Effort(1, 0, 4), Act(1, name: null)) });

        Assert.Equal("Unknown", Assert.Single(data).ActivityName);
    }

    // Mut 103 (`User?.DisplayName ?? UserId` remove-right / swap): the display name PREFERS the nav-loaded
    // user's DisplayName over the raw id.
    [Fact]
    public async Task Build_UserDisplayName_PrefersNavDisplayName()
    {
        await SeedTrackAsync(1, StandardPoints());
        var act = Act(1, userId: "u1", user: new AppUser { UserId = "u1", DisplayName = "Alice" });
        var data = await BuildAsync(new[] { (Effort(1, 0, 4), act) });

        Assert.Equal("Alice", Assert.Single(data).UserDisplayName);
    }

    // Mut 103 (remove-left): with no nav user, the display name falls back to the user id (not null).
    [Fact]
    public async Task Build_UserDisplayName_FallsBackToUserId_WhenNoNavUser()
    {
        await SeedTrackAsync(1, StandardPoints());
        var data = await BuildAsync(new[] { (Effort(1, 0, 4), Act(1, userId: "u2", user: null)) });

        Assert.Equal("u2", Assert.Single(data).UserDisplayName);
    }

    // ---------------------------------------------------------------- cost kind (mut 105)

    [Fact]
    public async Task Build_NoPowerMeter_IsEstimated()
    {
        await SeedTrackAsync(1, StandardPoints());   // no Power channel
        var epd = Assert.Single(await BuildAsync(new[] { (Effort(1, 0, 4), Act(1)) }));

        Assert.True(epd.CostEstimated);
        Assert.Equal(CostKinds.Estimated, epd.CostKind);
    }

    [Fact]
    public async Task Build_WithPowerMeter_IsMeasured()
    {
        var pts = StandardPoints();
        foreach (var p in pts) p.Power = 200;        // full power coverage → measured
        await SeedTrackAsync(1, pts);
        var epd = Assert.Single(await BuildAsync(new[] { (Effort(1, 0, 4), Act(1)) }));

        Assert.False(epd.CostEstimated);
        Assert.Equal(CostKinds.Measured, epd.CostKind);
    }

    // ---------------------------------------------------------------- drop guards

    // Mut 73 (`if (track == null) continue;` removed): an effort whose track cannot be decoded is dropped,
    // not projected. Track 2 is never seeded, so its decode returns null.
    [Fact]
    public async Task Build_UndecodableTrack_IsDropped()
    {
        await SeedTrackAsync(1, StandardPoints());
        var data = await BuildAsync(new[]
        {
            (Effort(1, 0, 4, effortId: 1), Act(1)),
            (Effort(2, 0, 4, effortId: 2), Act(2)),   // track 2 not seeded → null decode
        });

        Assert.Equal(1, Assert.Single(data).ActivityId);
    }

    // Mut 79 (`if (slice.Count == 0) continue;` removed): an effort window entirely past the track's points
    // yields an empty slice and is dropped.
    [Fact]
    public async Task Build_EffortWindowOutOfRange_IsDropped()
    {
        await SeedTrackAsync(1, StandardPoints());   // ordinals 0..4
        var data = await BuildAsync(new[] { (Effort(1, 10, 12), Act(1)) });

        Assert.Empty(data);
    }

    // ---------------------------------------------------------------- elapsed / distance accumulation

    // Mut 157 (`first.HasValue && curr.HasValue` → `||`): a point with no timestamp contributes elapsed 0,
    // and the build must not dereference the missing timestamp (the `||` mutant NREs on curr.Value).
    [Fact]
    public async Task Build_PointWithoutTimestamp_HasZeroElapsed()
    {
        var pts = StandardPoints();
        pts[2].DateTimeUtc = null;                   // middle point loses its timestamp
        await SeedTrackAsync(1, pts);
        var epd = Assert.Single(await BuildAsync(new[] { (Effort(1, 0, 4), Act(1)) }, maxPoints: 0));

        Assert.Equal(0, epd.Points.Time[2]);
    }

    // Mut 136 (HaversineLeg conditional-false → always 0): with no Distance channel the cumulative distance
    // is built from the haversine of each leg — it must be strictly positive over a real track.
    [Fact]
    public async Task Build_NoDistanceChannel_AccumulatesHaversineDistance()
    {
        var pts = StandardPoints();
        foreach (var p in pts) p.Distance = null;    // force the haversine fallback path
        await SeedTrackAsync(1, pts);
        var epd = Assert.Single(await BuildAsync(new[] { (Effort(1, 0, 4), Act(1)) }, maxPoints: 0));

        Assert.True(epd.Points.Distance[^1] > 0, "haversine cumulative distance must be positive");
        for (int i = 1; i < epd.Points.Distance.Length; i++)
            Assert.True(epd.Points.Distance[i] >= epd.Points.Distance[i - 1], "distance must be monotone");
    }

    // Mut 136 (HaversineLeg `prev.GpsLat && prev.GpsLong` → `||`): a leg whose PREVIOUS point has only one
    // coordinate contributes zero, and the build must not dereference the missing coordinate.
    [Fact]
    public async Task Build_PartialCoordinatePoint_ContributesNoDistanceAndNeverThrows()
    {
        var pts = StandardPoints();
        foreach (var p in pts) p.Distance = null;    // haversine path
        pts[1].GpsLat = null;                         // point 1 keeps only its longitude
        await SeedTrackAsync(1, pts);
        var epd = Assert.Single(await BuildAsync(new[] { (Effort(1, 0, 4), Act(1)) }, maxPoints: 0));

        Assert.True(epd.Points.Distance[^1] >= 0);
        for (int i = 1; i < epd.Points.Distance.Length; i++)
            Assert.True(epd.Points.Distance[i] >= epd.Points.Distance[i - 1], "distance must be monotone");
    }

    // Mut 163 (distance-channel `curr && prev && first` → `||`): if the FIRST point of the window has no
    // Distance channel value, every leg falls back to haversine — the `||` mutant instead dereferences the
    // missing first.Distance and NREs.
    [Fact]
    public async Task Build_FirstPointMissingDistance_FallsBackWithoutThrowing()
    {
        var pts = StandardPoints();
        pts[0].Distance = null;                       // only the first point lacks Distance
        await SeedTrackAsync(1, pts);
        var epd = Assert.Single(await BuildAsync(new[] { (Effort(1, 0, 4), Act(1)) }, maxPoints: 0));

        Assert.True(epd.Points.Distance[^1] >= 0);
        for (int i = 1; i < epd.Points.Distance.Length; i++)
            Assert.True(epd.Points.Distance[i] >= epd.Points.Distance[i - 1], "distance must be monotone");
    }

    // ---------------------------------------------------------------- rider mass floor (mut 126)

    // A non-positive stored rider mass must be replaced by the default, so an estimated running cost stays
    // strictly positive. The mutants (`<= 0` → `> 0` / `< 0` / negate) leave mass at 0 → zero cost.
    [Fact]
    public async Task Build_NonPositiveRiderMass_UsesDefaultSoCostIsPositive()
    {
        await SeedTrackAsync(1, StandardPoints());
        var settings = new FixedMassSettings(0.0);    // stored setting says 0 kg
        var epd = Assert.Single(await BuildAsync(
            new[] { (Effort(1, 0, 4), Act(1, type: "Run")) }, settings: settings, maxPoints: 0));

        Assert.True(epd.Points.CumulativeCostKj is not null);
        Assert.True(epd.Points.CumulativeCostKj![^1] > 0,
            "a mass floored to the default must yield a positive estimated cost");
    }

    // ---------------------------------------------------------------- segment progress (mut 183)

    // Mut 183 (AssignSegmentProgress statement removed): when the segment has a usable reference polyline,
    // per-point SegmentProgress is computed and surfaces on the wire (non-null column).
    [Fact]
    public async Task Build_WithSegmentReference_AssignsSegmentProgress()
    {
        await SeedTrackAsync(1, StandardPoints());
        var segId = await SeedSegmentReferenceAsync();
        var epd = Assert.Single(await BuildAsync(
            new[] { (Effort(1, 0, 4), Act(1)) }, segId: segId, maxPoints: 0));

        Assert.NotNull(epd.Points.SegmentProgress);
    }

    private async Task<SegmentId> SeedSegmentReferenceAsync()
    {
        var seg = new Segment { UserId = null, SegmentKind = "shapes" };   // let EF generate the id
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        for (int i = 0; i < 5; i++)
            _context.SegmentGps.Add(new SegmentGps
            {
                // SegGpsId is the auto-generated key; insertion order preserves the OrderBy(SegGpsId).
                SegmentId = seg.SegmentId,
                GpsLat = 47.000 + i * 0.001, GpsLong = 11.000, Altitude = 100 + i * 10,
            });
        await _context.SaveChangesAsync();
        return seg.SegmentId;
    }

    /// <summary>Settings whose only meaningful answer is a fixed rider mass (kg) for the RiderWeightKg key;
    /// everything else returns the caller's default.</summary>
    private sealed class FixedMassSettings(double mass) : IProfileSettingsService
    {
        public Task<T?> GetSettingAsync<T>(string category, string key, string? userId = null, T? defaultValue = default)
            => key == SettingKeys.RiderWeightKg && mass is T m
                ? Task.FromResult<T?>(m)
                : Task.FromResult(defaultValue);
        public Task<string?> GetSettingAsync(string category, string key, string? userId = null)
            => Task.FromResult<string?>(null);
        // No `description` parameter and no seed method: step 1b dropped the write-only DataType/
        // Description/DefaultValue columns, and userId is required now that there are no global rows.
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
}
