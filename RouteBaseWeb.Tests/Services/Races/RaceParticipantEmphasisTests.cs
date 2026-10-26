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
/// Emphasising a group — the second presentation channel beside the paint (see
/// <see cref="RaceParticipantColorTests"/>), and driven by the same filtered set, so it inherits the same
/// 999-parameter chunking hazard.
///
/// Its own hazard is the WRITE shape: off is stored as NULL, not false, so the column has exactly one
/// representation of "not emphasised" — rows predating the column are already null. A test pins that,
/// because "it works" looks identical either way until someone hand-writes a `WHERE emphasised` query.
/// </summary>
public class RaceParticipantEmphasisTests : IAsyncLifetime
{
    private const string Me = "me";

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;
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

    /// <summary>Same chunking as the paint, and the same reason: 1200 ids is past SQLite's ceiling.</summary>
    [Fact]
    public async Task Emphasise_AFieldLargerThanSqlitesParameterCeiling_Succeeds()
    {
        var ids = await SeedFieldAsync(1200);

        var (result, updated) = await Crud().SetParticipantEmphasisAsync(_race.RaceId, ids, true);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(1200, updated);
        Assert.Equal(1200, await _context.RaceParticipants.CountAsync(p => p.Emphasised == true));
    }

    [Fact]
    public async Task Emphasise_ASubset_LeavesTheRestAlone()
    {
        var ids = await SeedFieldAsync(10);

        await Crud().SetParticipantEmphasisAsync(_race.RaceId, ids.Take(4).ToList(), true);

        Assert.Equal(4, await _context.RaceParticipants.CountAsync(p => p.Emphasised == true));
        Assert.Equal(6, await _context.RaceParticipants.CountAsync(p => p.Emphasised == null));
    }

    /// <summary>
    /// OFF is NULL, never false. Both read as "not emphasised" through the DTO, so only a direct column
    /// assertion can catch a regression here — and a mixed column is what makes a hand-run query lie.
    /// </summary>
    [Fact]
    public async Task Emphasise_TurnedOffAgain_StoresNullRatherThanFalse()
    {
        var ids = await SeedFieldAsync(3);
        await Crud().SetParticipantEmphasisAsync(_race.RaceId, ids, true);

        var (result, updated) = await Crud().SetParticipantEmphasisAsync(_race.RaceId, ids, false);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(3, updated);
        Assert.Equal(3, await _context.RaceParticipants.CountAsync(p => p.Emphasised == null));
        Assert.Equal(0, await _context.RaceParticipants.CountAsync(p => p.Emphasised == false));
    }

    /// <summary>The two channels are independent — emphasising a group must not touch its colour.</summary>
    [Fact]
    public async Task Emphasise_DoesNotDisturbThePaint()
    {
        var ids = await SeedFieldAsync(4);
        await Crud().SetParticipantColorsAsync(_race.RaceId, ids, "#e64980");

        await Crud().SetParticipantEmphasisAsync(_race.RaceId, ids.Take(2).ToList(), true);

        Assert.Equal(4, await _context.RaceParticipants.CountAsync(p => p.Color == "#e64980"));
        Assert.Equal(2, await _context.RaceParticipants.CountAsync(p => p.Emphasised == true));
    }

    /// <summary>Ids are not trusted: every UPDATE is scoped by race_ID. Mirrors the paint's own test.</summary>
    [Fact]
    public async Task Emphasise_AnIdFromAnotherRace_IsNotTouched()
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

        var (result, updated) = await Crud().SetParticipantEmphasisAsync(
            _race.RaceId, [.. mine, stranger.ParticipantId], true);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(2, updated);
        Assert.Null(await _context.RaceParticipants
            .Where(p => p.ParticipantId == stranger.ParticipantId)
            .Select(p => p.Emphasised)
            .SingleAsync());
    }

    [Fact]
    public async Task Emphasise_ARaceYouDoNotOwn_IsRejected()
    {
        _context.Users.Add(new AppUser { UserId = "someone-else", DisplayName = "Else" });
        await _context.SaveChangesAsync();

        var friendship = new FriendshipService(_context, null!, null!, NullLogger<FriendshipService>.Instance, TimeProvider.System);
        var theirCrud = new RaceParticipantCrud(_context, new RaceAccess(
            _context, new VisibilityService(new TestCurrentUserService("someone-else"), friendship, _context)));

        var (result, _) = await theirCrud.SetParticipantEmphasisAsync(_race.RaceId, [new ParticipantId(1)], true);

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
    }
}
