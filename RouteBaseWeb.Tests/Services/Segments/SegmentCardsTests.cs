using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// PERF-4: the dashboard segment card list. The point of the endpoint is that it fetches N rows instead
/// of every visible segment — so what must be pinned is that the N-limited path keeps the SAME
/// visibility gate and the SAME ordering as the full list. A cheaper read path that is also a WIDER one
/// would be a bad trade.
/// </summary>
public class SegmentCardsTests : IAsyncLifetime
{
    private const string Viewer = "viewer-A";
    private const string Stranger = "stranger-B";
    private static readonly DateTime T0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private RouteBaseDbContext _context = null!;
    private ISegmentCrudSurface _crud = null!;

    /// <summary>The two methods under comparison, so the test reads as the contract it pins.</summary>
    private interface ISegmentCrudSurface
    {
        Task<List<Segment>> GetAllSegmentsAsync();
        Task<List<SegmentCardDto>> GetTopSegmentCardsAsync(int limit);
    }

    private sealed class CrudSurface(SegmentCrud inner) : ISegmentCrudSurface
    {
        public Task<List<Segment>> GetAllSegmentsAsync() => inner.GetAllSegmentsAsync();
        public Task<List<SegmentCardDto>> GetTopSegmentCardsAsync(int limit) => inner.GetTopSegmentCardsAsync(limit);
    }

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:segcards_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options);
        await _context.Database.EnsureCreatedAsync();

        foreach (var u in new[] { Viewer, Stranger })
            _context.Users.Add(new AppUser
            {
                UserId = u, DisplayName = u, Email = $"{u}@t.com", IsAdmin = false,
                CreatedAt = T0.ToString("O"), LastLogin = T0.ToString("O"),
            });
        await _context.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new ProfileSettingsService(_context, NullLogger<ProfileSettingsService>.Instance, cache, TimeProvider.System);
        var reader = new DirectTrackReader(_context);
        var shapeMatcher = new ShapeSegmentMatcher(_context, NullLogger<ShapeSegmentMatcher>.Instance, reader);
        var engine = new MatchEngineService(_context, reader, NullLogger<MatchEngineService>.Instance);
        var effortMath = new EffortMathService(_context, reader, NullLogger<EffortMathService>.Instance);

        var services = SegmentServices.Create(
            _context, cache, new TestVisibilityService(Viewer), new UserCacheInvalidator(), settings,
            shapeMatcher, engine, effortMath, reader);
        _crud = new CrudSurface((SegmentCrud)services.Crud);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    private async Task SeedSegmentAsync(int id, string owner, string name, double? distance = 1000, int? ascent = 50)
    {
        _context.Segments.Add(new Segment
        {
            SegmentId = new SegmentId(id), UserId = owner, SegmentName = name,
            SegmentDistance = distance, SegmentAscent = ascent,
        });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task TheCards_AreTheHeadOfTheFullList_SameOrder()
    {
        await SeedSegmentAsync(1, Viewer, "Charlie");
        await SeedSegmentAsync(2, Viewer, "Alpha");
        await SeedSegmentAsync(3, Viewer, "Bravo");

        var cards = await _crud.GetTopSegmentCardsAsync(2);
        var all = await _crud.GetAllSegmentsAsync();

        Assert.Equal(["Alpha", "Bravo"], cards.Select(c => c.SegmentName), StringComparer.Ordinal);
        Assert.Equal(all.Take(2).Select(s => s.SegmentId.Value), cards.Select(c => c.SegmentId));
    }

    [Fact]
    public async Task AStrangersSegment_IsInvisibleHereToo_TheCheaperPathIsNotAWiderOne()
    {
        await SeedSegmentAsync(1, Stranger, "Aaa-secret");
        await SeedSegmentAsync(2, Viewer, "Bbb-mine");

        var cards = await _crud.GetTopSegmentCardsAsync(10);

        // Alphabetically the stranger's segment sorts FIRST — if the gate were missing it would be card #1.
        Assert.Equal(["Bbb-mine"], cards.Select(c => c.SegmentName), StringComparer.Ordinal);
    }

    [Fact]
    public async Task TheLimit_IsHonoured_EvenWhenMoreAreVisible()
    {
        for (int i = 1; i <= 7; i++) await SeedSegmentAsync(i, Viewer, $"S{i}");

        Assert.Equal(3, (await _crud.GetTopSegmentCardsAsync(3)).Count);
        Assert.Equal(7, (await _crud.GetTopSegmentCardsAsync(20)).Count); // fewer rows than asked for is fine
    }

    [Fact]
    public async Task TheCard_CarriesWhatTheWidgetPaints()
    {
        await SeedSegmentAsync(1, Viewer, "Climb", distance: 2500.5, ascent: 180);

        var card = Assert.Single(await _crud.GetTopSegmentCardsAsync(3));

        Assert.Equal(1, card.SegmentId);
        Assert.Equal("Climb", card.SegmentName);
        Assert.Equal(2500.5, card.SegmentDistance);
        Assert.Equal(180, card.SegmentAscent);
    }
}
