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
/// Hiding a rider's CALL-OUT — his name label, his pinned sidebar row and his line in the speed chart.
///
/// The switch exists because a real rider earns all three by BEING real: two real riders are two lines on
/// the speed chart, and before this the only way to take one off was to delete him from the race.
///
/// Its hazard is the write shape, and it is the emphasis flag's hazard INVERTED: here the DEFAULT is
/// "called out", so the default is what must be stored as NULL and `false` must never reach the column.
/// Both read identically through the DTO, so only a direct column assertion can catch a regression.
/// </summary>
public class RaceParticipantCalloutTests : IAsyncLifetime
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

    [Fact]
    public async Task Hide_ASubset_LeavesTheRestCalledOut()
    {
        var ids = await SeedFieldAsync(10);

        var (result, updated) = await Crud().SetParticipantCalloutOffAsync(_race.RaceId, ids.Take(4).ToList(), true);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(4, updated);
        Assert.Equal(4, await _context.RaceParticipants.CountAsync(p => p.CalloutOff == true));
        Assert.Equal(6, await _context.RaceParticipants.CountAsync(p => p.CalloutOff == null));
    }

    /// <summary>
    /// Restoring writes NULL, never false — the default state has exactly one representation, and rows
    /// predating the column are already null.
    /// </summary>
    [Fact]
    public async Task Restore_StoresNullRatherThanFalse()
    {
        var ids = await SeedFieldAsync(3);
        await Crud().SetParticipantCalloutOffAsync(_race.RaceId, ids, true);

        var (result, updated) = await Crud().SetParticipantCalloutOffAsync(_race.RaceId, ids, false);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(3, updated);
        Assert.Equal(3, await _context.RaceParticipants.CountAsync(p => p.CalloutOff == null));
        Assert.Equal(0, await _context.RaceParticipants.CountAsync(p => p.CalloutOff == false));
    }

    /// <summary>Same 999-parameter ceiling as the paint and the emphasis; same chunking answer.</summary>
    [Fact]
    public async Task Hide_AFieldLargerThanSqlitesParameterCeiling_Succeeds()
    {
        var ids = await SeedFieldAsync(1200);

        var (result, updated) = await Crud().SetParticipantCalloutOffAsync(_race.RaceId, ids, true);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(1200, updated);
        Assert.Equal(1200, await _context.RaceParticipants.CountAsync(p => p.CalloutOff == true));
    }

    /// <summary>A third independent channel: hiding a rider must not repaint or un-emphasise him.</summary>
    [Fact]
    public async Task Hide_DoesNotDisturbThePaintOrTheEmphasis()
    {
        var ids = await SeedFieldAsync(4);
        await Crud().SetParticipantColorsAsync(_race.RaceId, ids, "#e64980");
        await Crud().SetParticipantEmphasisAsync(_race.RaceId, ids, true);

        await Crud().SetParticipantCalloutOffAsync(_race.RaceId, ids.Take(2).ToList(), true);

        Assert.Equal(4, await _context.RaceParticipants.CountAsync(p => p.Color == "#e64980"));
        Assert.Equal(4, await _context.RaceParticipants.CountAsync(p => p.Emphasised == true));
        Assert.Equal(2, await _context.RaceParticipants.CountAsync(p => p.CalloutOff == true));
    }

    /// <summary>Ids are not trusted: every UPDATE is scoped by race_ID, exactly as the paint's is.</summary>
    [Fact]
    public async Task Hide_AnIdFromAnotherRace_IsNotTouched()
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

        var (result, updated) = await Crud().SetParticipantCalloutOffAsync(
            _race.RaceId, [.. mine, stranger.ParticipantId], true);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(2, updated);
        Assert.Null(await _context.RaceParticipants
            .Where(p => p.ParticipantId == stranger.ParticipantId)
            .Select(p => p.CalloutOff)
            .SingleAsync());
    }

    [Fact]
    public async Task Hide_ARaceYouDoNotOwn_IsRejected()
    {
        _context.Users.Add(new AppUser { UserId = "someone-else", DisplayName = "Else" });
        await _context.SaveChangesAsync();

        var friendship = new FriendshipService(_context, null!, null!, NullLogger<FriendshipService>.Instance, TimeProvider.System);
        var theirCrud = new RaceParticipantCrud(_context, new RaceAccess(
            _context, new VisibilityService(new TestCurrentUserService("someone-else"), friendship, _context)));

        var (result, _) = await theirCrud.SetParticipantCalloutOffAsync(_race.RaceId, [new ParticipantId(1)], true);

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
    }
}
