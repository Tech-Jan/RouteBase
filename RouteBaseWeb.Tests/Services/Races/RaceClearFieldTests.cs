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
/// Emptying the field so a corrected results CSV can be imported over a clean slate.
///
/// It deletes the FIELD, not the race: the course and the checkpoints are the work the user did by hand,
/// and re-importing a CSV must not cost him that. Split times go with their riders, or the next import
/// would rank the field against a dead rider's splits.
/// </summary>
public class RaceClearFieldTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Stranger = "stranger";

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;
    // The course GRID, which is what every chainage is measured against — see FakeRaceCourseService.
    private readonly FakeRaceCourseService Courses = FakeRaceCourseService.OfLength(142228);
    private Race _race = null!;
    private RaceCheckpoint _finish = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = "Me" });
        _context.Users.Add(new AppUser { UserId = Stranger, DisplayName = "Stranger" });
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

        _race = (await new RaceCrud(_context, Access(Me), Courses, TimeProvider.System)
            .CreateRaceAsync(course.TrackId, "Ski to Sea", null, "friends"))!;

        _finish = await _context.RaceCheckpoints
            .Where(c => c.RaceId == _race.RaceId)
            .OrderByDescending(c => c.Seq)
            .FirstAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private RaceAccess Access(string userId)
    {
        var friendship = new FriendshipService(_context, null!, null!, NullLogger<FriendshipService>.Instance, TimeProvider.System);
        return new RaceAccess(_context, new VisibilityService(new TestCurrentUserService(userId), friendship, _context));
    }

    private RaceParticipantCrud Crud(string userId = Me) => new(_context, Access(userId));

    /// <summary>A field of both kinds, each rider carrying a split at the finish.</summary>
    private async Task SeedFieldAsync(int simulated, int real)
    {
        var field = Enumerable.Range(0, simulated)
            .Select(i => new RaceParticipant
            {
                RaceId = _race.RaceId,
                Kind = RaceParticipantKind.Simulated,
                DisplayName = $"Team {i}",
            })
            .Concat(Enumerable.Range(0, real).Select(i => new RaceParticipant
            {
                RaceId = _race.RaceId,
                Kind = RaceParticipantKind.Real,
                DisplayName = $"Rider {i}",
            }))
            .ToList();
        _context.RaceParticipants.AddRange(field);
        await _context.SaveChangesAsync();

        _context.RaceSplitTimes.AddRange(field.Select(p => new RaceSplitTime
        {
            ParticipantId = p.ParticipantId,
            CheckpointId = _finish.CheckpointId,
            ElapsedSeconds = 7200,
        }));
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task ClearField_RemovesBothKindsAndTheirSplits()
    {
        await SeedFieldAsync(simulated: 424, real: 2);

        var (result, removed) = await Crud().ClearFieldAsync(_race.RaceId);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(426, removed);
        Assert.Equal(0, await _context.RaceParticipants.CountAsync(p => p.RaceId == _race.RaceId));
        Assert.Equal(0, await _context.RaceSplitTimes.CountAsync());
    }

    /// <summary>The point of the button: re-import onto a clean slate, not onto a demolished race.</summary>
    [Fact]
    public async Task ClearField_KeepsTheRaceAndItsCheckpoints()
    {
        await SeedFieldAsync(simulated: 3, real: 0);

        await Crud().ClearFieldAsync(_race.RaceId);

        Assert.NotNull(await _context.Races.FindAsync(_race.RaceId));
        Assert.Equal(2, await _context.RaceCheckpoints.CountAsync(c => c.RaceId == _race.RaceId));
    }

    [Fact]
    public async Task ClearField_OnAnEmptyField_IsANoOp()
    {
        var (result, removed) = await Crud().ClearFieldAsync(_race.RaceId);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task ClearField_ByANonOwner_IsRefused()
    {
        await SeedFieldAsync(simulated: 5, real: 0);

        var (result, removed) = await Crud(Stranger).ClearFieldAsync(_race.RaceId);

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
        Assert.Equal(0, removed);
        Assert.Equal(5, await _context.RaceParticipants.CountAsync(p => p.RaceId == _race.RaceId));
    }
}
