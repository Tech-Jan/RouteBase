using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments.Rules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// SA-03 pin: <see cref="SegmentsApiController.GetShapeSegmentForEdit"/> now fetches via an owner-scoped
/// query (404-no-leak), so a friend/public viewer can no longer read the raw rule document + owner
/// friend-id list of a shape segment they can only view. The viewer's visibility SEES the segment but
/// does not OWN it — the pre-fix code (visible-scoped) returned 200 here.
/// </summary>
public class SegmentsApiControllerShapeEditTests : IAsyncLifetime
{
    private const string Owner = "owner-user";

    private RouteBaseDbContext _context = null!;

    // A viewer who can SEE every segment (friend/public visibility) but OWNS only their own.
    private sealed class ViewerVisibility(string me) : IVisibilityService
    {
        public string GetCurrentUserId() => me;
        public IQueryable<Segment> VisibleSegments(IQueryable<Segment> q) => q;               // sees all
        public IQueryable<Segment> OwnedSegments(IQueryable<Segment> q) => q.Where(s => s.UserId == me);
        public IQueryable<Activity> VisibleActivities(IQueryable<Activity> q) => q;
        public IQueryable<Activity> OwnedActivities(IQueryable<Activity> q) => q.Where(a => a.UserId == me);
        public IQueryable<MultiDayTour> VisibleTours(IQueryable<MultiDayTour> q) => q;
        public IQueryable<MultiDayTour> OwnedTours(IQueryable<MultiDayTour> q) => q.Where(t => t.UserId == me);
        public IQueryable<AppCredentials> OwnedCredentials(IQueryable<AppCredentials> q) => q.Where(c => c.UserId == me);
        public Task<List<string>> GetVisibleUserIdsAsync() => Task.FromResult(new List<string> { me });
        // Both permissive constants below are deliberate and unreachable: GetShapeSegmentForEdit fetches via
        // GetOwnedSegmentAsync (OwnedSegments scoping) and consults neither CanViewItemAsync nor
        // CanViewActivityAsync. The "sees all" VisibleSegments above is what the SA-03 owner-gate is proved
        // against (a viewer who SEES the segment still gets a 404-no-leak).
        public Task<bool> CanViewItemAsync(string ownerUserId, string? visibility) => Task.FromResult(true);
        public Task<bool> CanViewActivityAsync(TrackId trackId) => Task.FromResult(true);
    }

    public async Task InitializeAsync()
    {
        _context = new RouteBaseDbContext(new DbContextOptionsBuilder<RouteBaseDbContext>()
            .UseSqlite($"Data Source=file:shapeedit_{Guid.NewGuid():N}?mode=memory&cache=shared").Options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser
        {
            UserId = Owner, DisplayName = Owner, Email = "o@t.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    private async Task<int> SeedShapeSegmentAsync()
    {
        var doc = new RuleDocument
        {
            Shapes = { new ShapeDef { No = 1, Kind = "zone",
                Geom = new RectGeom { South = 46.9, West = 10.9, North = 47.1, East = 11.1 } } },
            Membership = { new PassesAllRule { Zones = { 1 } } },
        };
        var seg = new Segment
        {
            UserId = Owner, SegmentKind = "shapes", SegmentName = "S",
            Visibility = "friends", MatchRulesJson = doc.Serialize(),
        };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        return seg.SegmentId.Value;
    }

    private SegmentsApiController ControllerFor(IVisibilityService visibility)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var crud = SegmentServices.Create(
            _context, cache, visibility, new UserCacheInvalidator(), null!, null!, null!, null!, null!).Crud;
        return new SegmentsApiController(
            crud, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<SegmentsApiController>.Instance, null!);
    }

    [Fact]
    public async Task ShapeSegmentForEdit_NonOwnerVisible_Returns404()
    {
        var segId = await SeedShapeSegmentAsync();

        var result = await ControllerFor(new ViewerVisibility("friend-user")).GetShapeSegmentForEdit(segId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ShapeSegmentForEdit_Owner_ReturnsPayload()
    {
        var segId = await SeedShapeSegmentAsync();

        var result = await ControllerFor(new ViewerVisibility(Owner)).GetShapeSegmentForEdit(segId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ShapeSegmentEditResponse>(ok.Value);
        Assert.Equal("S", payload.SegmentName);
        Assert.Null(payload.ActivityType);
        Assert.Null(payload.MatchOwnerIds);            // no OwnerFilter in the doc, no MatchOwnerIds column → own-only

        // The rule document is round-tripped verbatim: its one zone shape + PassesAll membership survive.
        var roundTripped = RuleDocument.TryParse(payload.Rules.GetRawText());
        Assert.NotNull(roundTripped);
        Assert.Single(roundTripped!.Shapes);
        Assert.Equal("zone", roundTripped.Shapes[0].Kind);
        Assert.Single(roundTripped.Membership);
    }
}
