using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// Segment visibility is WRITTEN now.
///
/// Before this, SettingKeys.DefaultVisibility described itself as covering "new activities and segments"
/// and was read by nothing: every segment ever created sat at the entity's CLR default "friends", and no
/// UI or API could change it. These pin the write path and the forward-only choice.
/// </summary>
public class SegmentVisibilityWriteTests : IAsyncLifetime
{
    private const string Owner = "owner-user";
    private const string Other = "other-user";

    private RouteBaseDbContext _db = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:segvis_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _db = new RouteBaseDbContext(options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.EnsureCreatedAsync();

        _db.Users.Add(new AppUser { UserId = Owner, DisplayName = "Owner", Email = "o@t.com", IsAdmin = false });
        _db.Users.Add(new AppUser { UserId = Other, DisplayName = "Other", Email = "x@t.com", IsAdmin = false });
        await _db.SaveChangesAsync();
        return;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private SegmentServices Services(string userId, string? defaultVisibility = null) =>
        SegmentServices.Create(
            _db,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            new TestVisibilityService(userId),
            new UserCacheInvalidator(),
            new FixedSettings(defaultVisibility),
            shapeMatcher: null!, matchEngine: null!, effortMath: null!, trackReader: null!);

    private async Task<Segment> SeedSegmentAsync(string ownerId, string? visibility)
    {
        var seg = new Segment
        {
            SegmentName = "Test climb", UserId = ownerId, SegmentKind = "polyline",
        };
        if (visibility != null) seg.Visibility = visibility;
        _db.Segments.Add(seg);
        await _db.SaveChangesAsync();
        return seg;
    }

    // ---- create honours the setting ----

    [Fact]
    public async Task CreateShapeSegment_TakesTheOwnersDefaultVisibility()
    {
        var svc = Services(Owner, defaultVisibility: Visibilities.Public);

        var created = await svc.Crud.CreateShapeSegmentAsync("painted", "Ride", "{}");

        Assert.Equal(Visibilities.Public, created.Visibility);
    }

    [Fact]
    public async Task CreateShapeSegment_FallsBackToFriends_WhenTheSettingIsUnset()
    {
        var svc = Services(Owner, defaultVisibility: null);

        var created = await svc.Crud.CreateShapeSegmentAsync("painted", "Ride", "{}");

        Assert.Equal(Visibilities.Friends, created.Visibility);
    }

    [Fact]
    public async Task CreateShapeSegment_FallsBackToFriends_WhenTheStoredSettingIsGarbage()
    {
        // Persisting an unrecognised value would make the segment invisible to EVERYONE including its
        // owner, because VisibilityService matches on these exact strings.
        var svc = Services(Owner, defaultVisibility: "everyone-on-earth");

        var created = await svc.Crud.CreateShapeSegmentAsync("painted", "Ride", "{}");

        Assert.Equal(Visibilities.Friends, created.Visibility);
    }

    // ---- the update path ----

    [Fact]
    public async Task UpdateVisibility_SetsIt_ForTheOwner()
    {
        var seg = await SeedSegmentAsync(Owner, Visibilities.Friends);

        Assert.True(await Services(Owner).Crud.UpdateSegmentVisibilityAsync(seg.SegmentId, Visibilities.Public));

        Assert.Equal(Visibilities.Public, (await _db.Segments.FindAsync(seg.SegmentId))!.Visibility);
    }

    [Fact]
    public async Task UpdateVisibility_IsRefused_ForANonOwner()
    {
        var seg = await SeedSegmentAsync(Owner, Visibilities.Friends);

        Assert.False(await Services(Other).Crud.UpdateSegmentVisibilityAsync(seg.SegmentId, Visibilities.Public));

        _db.ChangeTracker.Clear();
        Assert.Equal(Visibilities.Friends, (await _db.Segments.FindAsync(seg.SegmentId))!.Visibility);
    }

    [Fact]
    public async Task UpdateVisibility_ForANonOwnedSegment_IsTheSameAnswerAsAMissingOne()
    {
        // Not-yours and not-there must be indistinguishable, or this enumerates other people's ids.
        var seg = await SeedSegmentAsync(Owner, Visibilities.Friends);
        var svc = Services(Other);

        var notMine = await svc.Crud.UpdateSegmentVisibilityAsync(seg.SegmentId, Visibilities.Public);
        var missing = await svc.Crud.UpdateSegmentVisibilityAsync(new SegmentId(999_999), Visibilities.Public);

        Assert.Equal(missing, notMine);
    }

    // ---- SEC-01: a heatmap is painted for the segment's audience ----

    [Theory]
    [InlineData(Visibilities.Friends, Visibilities.Public,  "failed")]   // wider → offline until regenerated
    [InlineData(Visibilities.Private, Visibilities.Friends, "failed")]
    [InlineData(Visibilities.Public,  Visibilities.Friends, "stale")]    // narrower → still safe, offer regen
    [InlineData(Visibilities.Friends, Visibilities.Friends, "done")]
    public async Task UpdateVisibility_KeepsTheHeatmapWithinItsAudience(string from, string to, string expected)
    {
        var seg = await SeedSegmentAsync(Owner, from);
        _db.Heatmaps.Add(new Heatmap { SegmentId = seg.SegmentId, Status = "done" });
        await _db.SaveChangesAsync();

        Assert.True(await Services(Owner).Crud.UpdateSegmentVisibilityAsync(seg.SegmentId, to));

        var hm = await _db.Heatmaps.AsNoTracking().FirstAsync(h => h.SegmentId == seg.SegmentId);
        Assert.Equal(expected, hm.Status);
    }

    [Fact]
    public async Task UpdateVisibility_RejectsAnUnknownValue_WithoutWriting()
    {
        var seg = await SeedSegmentAsync(Owner, Visibilities.Friends);

        Assert.False(await Services(Owner).Crud.UpdateSegmentVisibilityAsync(seg.SegmentId, "everyone"));

        _db.ChangeTracker.Clear();
        Assert.Equal(Visibilities.Friends, (await _db.Segments.FindAsync(seg.SegmentId))!.Visibility);
    }

    // ---- forward-only ----

    [Fact]
    public async Task ExistingSegments_AreNotBackfilled()
    {
        // The deliberate choice: only NEW segments honour the setting. Rewriting existing rows to a
        // user's default could silently drop friends' access to segments they can see today, with no undo.
        var legacy = await SeedSegmentAsync(Owner, visibility: null);   // the CLR default, as every old row has

        _ = await Services(Owner, defaultVisibility: Visibilities.Private)
            .Crud.CreateShapeSegmentAsync("new one", "Ride", "{}");

        _db.ChangeTracker.Clear();
        Assert.Equal(Visibilities.Friends, (await _db.Segments.FindAsync(legacy.SegmentId))!.Visibility);
    }

    /// <summary>Returns a fixed DefaultVisibility and the caller's default for everything else.</summary>
    private sealed class FixedSettings(string? defaultVisibility) : IProfileSettingsService
    {
        public Task<T?> GetSettingAsync<T>(string category, string key, string? userId = null, T? defaultValue = default)
            => key == SettingKeys.DefaultVisibility && defaultVisibility is T typed
                ? Task.FromResult<T?>(typed)
                : Task.FromResult(defaultValue);
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
}
