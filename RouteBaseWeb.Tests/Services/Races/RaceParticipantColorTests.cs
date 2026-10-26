using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Races;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// Painting a group.
///
/// This is what the results table does: filter a group (gender, division, club — and the filters
/// COMPOSE) and colour the whole filtered set in one call. So the id list is as large as the field, and
/// the field is 508 teams. Two hazards live here, and both have a test:
///
///   - SQLite caps a statement at 999 host parameters and EF renders `Contains` as one parameter per id.
///     A 424-id `IN` already crashed this slice once.
///   - The colour reaches a MapLibre <c>['get','color']</c> paint expression and a CSS swatch, so free
///     text there is an injection surface.
/// </summary>
public class RaceParticipantColorTests : IAsyncLifetime
{
    private const string Me = "me";

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;
    // The course GRID, which is what every chainage is measured against — see FakeRaceCourseService.
    private readonly FakeRaceCourseService Courses = FakeRaceCourseService.OfLength(142228);
    private Race _race = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = "Me" });
        await _context.SaveChangesAsync();

        var course = new Activity
        {
            TrackName = "Course",
            UserId = Me,
            Visibility = "friends",
            Lifecycle = LifecycleStates.Released,
            TotalDistance = 142_228,
            ActivityType = "Ride",
        };
        _context.Activities.Add(course);
        await _context.SaveChangesAsync();

        _race = (await new RaceCrud(_context, Access(), Courses, TimeProvider.System)
            .CreateRaceAsync(course.TrackId, "Ski to Sea", null, "friends"))!;
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private RaceAccess Access()
    {
        var friendship = new FriendshipService(_context, null!, null!, NullLogger<FriendshipService>.Instance, TimeProvider.System);
        return new RaceAccess(_context, new VisibilityService(new TestCurrentUserService(Me), friendship, _context));
    }

    private RaceParticipantCrud Crud() => new(_context, Access());

    /// <summary>A simulated field of <paramref name="count"/> riders. Returns their ids.</summary>
    private async Task<List<ParticipantId>> SeedFieldAsync(int count)
    {
        var field = Enumerable.Range(0, count)
            .Select(i => new RaceParticipant
            {
                RaceId = _race.RaceId,
                Kind = RaceParticipantKind.Simulated,
                DisplayName = $"Team {i}",
            })
            .ToList();
        _context.RaceParticipants.AddRange(field);
        await _context.SaveChangesAsync();
        return field.Select(p => p.ParticipantId).ToList();
    }

    /// <summary>
    /// The whole point of chunking. 1200 ids is well past SQLite's 999-parameter statement ceiling, so a
    /// single `IN` would throw — the crash this slice already shipped once.
    /// </summary>
    [Fact]
    public async Task Paint_AFieldLargerThanSqlitesParameterCeiling_Succeeds()
    {
        var ids = await SeedFieldAsync(1200);

        var (result, painted) = await Crud().SetParticipantColorsAsync(_race.RaceId, ids, "#e64980");

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(1200, painted);
        Assert.Equal(1200, await _context.RaceParticipants.CountAsync(p => p.Color == "#e64980"));
    }

    [Fact]
    public async Task Paint_ASubset_LeavesTheRestAlone()
    {
        var ids = await SeedFieldAsync(10);

        await Crud().SetParticipantColorsAsync(_race.RaceId, ids.Take(4).ToList(), "#4dabf7");

        Assert.Equal(4, await _context.RaceParticipants.CountAsync(p => p.Color == "#4dabf7"));
        Assert.Equal(6, await _context.RaceParticipants.CountAsync(p => p.Color == null));
    }

    [Fact]
    public async Task Paint_WithNull_ClearsBackToTheDefault()
    {
        var ids = await SeedFieldAsync(3);
        await Crud().SetParticipantColorsAsync(_race.RaceId, ids, "#e64980");

        var (result, painted) = await Crud().SetParticipantColorsAsync(_race.RaceId, ids, null);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(3, painted);
        Assert.Equal(3, await _context.RaceParticipants.CountAsync(p => p.Color == null));
    }

    [Theory]
    [InlineData("red")]                                   // a CSS keyword is still not #rrggbb
    [InlineData("#fff")]                                  // short form — not the one shape we accept
    [InlineData("#e64980; fill: url(#evil)")]             // the reason this is validated at all
    [InlineData("javascript:alert(1)")]
    public async Task Paint_WithAColorThatIsNotHex_IsRejected(string color)
    {
        var ids = await SeedFieldAsync(2);

        var (result, painted) = await Crud().SetParticipantColorsAsync(_race.RaceId, ids, color);

        Assert.Equal(RaceWriteResult.InvalidColor, result);
        Assert.Equal(0, painted);
        Assert.Equal(2, await _context.RaceParticipants.CountAsync(p => p.Color == null));
    }

    /// <summary>
    /// The ids come from the browser and are NOT trusted: every UPDATE is scoped by race_ID, so an id
    /// from someone else's race cannot be painted through this race.
    /// </summary>
    [Fact]
    public async Task Paint_AnIdFromAnotherRace_IsNotPainted()
    {
        var mine = await SeedFieldAsync(2);

        var otherCourse = new Activity
        {
            TrackName = "Other course",
            UserId = Me,
            Visibility = "friends",
            Lifecycle = LifecycleStates.Released,
            TotalDistance = 40_000,
            ActivityType = "Ride",
        };
        _context.Activities.Add(otherCourse);
        await _context.SaveChangesAsync();
        var otherRace = (await new RaceCrud(_context, Access(), Courses, TimeProvider.System)
            .CreateRaceAsync(otherCourse.TrackId, "Another race", null, "friends"))!;
        var stranger = new RaceParticipant { RaceId = otherRace.RaceId, DisplayName = "Not mine" };
        _context.RaceParticipants.Add(stranger);
        await _context.SaveChangesAsync();

        var (result, painted) = await Crud().SetParticipantColorsAsync(
            _race.RaceId, [.. mine, stranger.ParticipantId], "#e64980");

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(2, painted);   // mine only — the foreign id matched no row in THIS race
        Assert.Null(await _context.RaceParticipants
            .Where(p => p.ParticipantId == stranger.ParticipantId)
            .Select(p => p.Color)
            .SingleAsync());
    }

    [Fact]
    public async Task Paint_ARaceYouDoNotOwn_IsRejected()
    {
        _context.Users.Add(new AppUser { UserId = "someone-else", DisplayName = "Else" });
        await _context.SaveChangesAsync();

        var friendship = new FriendshipService(_context, null!, null!, NullLogger<FriendshipService>.Instance, TimeProvider.System);
        var theirCrud = new RaceParticipantCrud(_context, new RaceAccess(
            _context, new VisibilityService(new TestCurrentUserService("someone-else"), friendship, _context)));

        var (result, _) = await theirCrud.SetParticipantColorsAsync(_race.RaceId, [new ParticipantId(1)], "#e64980");

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
    }
}

/// <summary>
/// The palette a real rider is handed as he joins. Pure — no DB, no race.
///
/// The rule it encodes: a colour ALREADY IN USE in this race is not offered again, whoever is wearing it.
/// Comparing against the whole field rather than the real riders is deliberate — a painted division and a
/// real rider in the same pink is the same collision seen from the other side.
/// </summary>
public class RaceRiderPaletteTests
{
    [Fact]
    public void NextRealColor_AnEmptyRace_StartsAtGold()
    {
        // Gold first so a race with one real rider looks exactly as it did before the palette existed.
        Assert.Equal(RaceColors.DefaultRealColor, RaceColors.NextRealColor([]));
    }

    [Fact]
    public void NextRealColor_SkipsColoursTheFieldAlreadyWears()
    {
        var used = new[] { RaceColors.RealRiderPalette[0], null, RaceColors.RealRiderPalette[1] };

        Assert.Equal(RaceColors.RealRiderPalette[2], RaceColors.NextRealColor(used));
    }

    [Fact]
    public void NextRealColor_IgnoresCase_BecauseTheColumnIsNotNormalizedOnEveryPath()
    {
        Assert.NotEqual("#FFD43B".ToLowerInvariant(), RaceColors.NextRealColor(["#FFD43B"]));
    }

    [Fact]
    public void NextRealColor_ANullColour_IsNotAColour()
    {
        // Null means "the default" — a simulated rider's is the field blue, which no palette entry is.
        Assert.Equal(RaceColors.DefaultRealColor, RaceColors.NextRealColor([null, null, null]));
    }

    [Fact]
    public void NextRealColor_AFieldWearingEverything_Cycles()
    {
        // Repeating a colour the user can then repaint beats refusing the add, or inventing a hue outside
        // the validated set.
        var everything = RaceColors.RealRiderPalette.ToArray();

        Assert.Contains(RaceColors.NextRealColor(everything), RaceColors.RealRiderPalette);
    }

    [Fact]
    public void RealRiderPalette_IsAllValidWireColours()
    {
        // Every entry reaches a MapLibre ['get','color'] expression and a CSS swatch through the same
        // validator the user's own picks go through.
        Assert.All(RaceColors.RealRiderPalette, c => Assert.True(RaceColors.IsValid(c)));
        Assert.Equal(RaceColors.RealRiderPalette.Count, RaceColors.RealRiderPalette.Distinct(StringComparer.Ordinal).Count());
    }
}
