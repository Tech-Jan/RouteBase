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
/// Races born broken.
///
/// <c>CreateRaceAsync</c> seeds the Finish checkpoint at the course GRID's length. A course the grid cannot
/// be built from — no GPS at all, or a track shorter than one grid step — would put Start AND Finish at
/// chainage 0, and every invariant in the slice is stated in terms of chainage EXTREMES:
///
///   - <c>IsEndpoint</c> is <c>MinBy(chainage) || MaxBy(chainage)</c>. On a tie both return the FIRST row,
///     so Finish is no longer recognised as an endpoint — it can be deleted, which the rest of the slice
///     assumes is impossible.
///   - <c>RaceCheckpointBounds</c> gives an intermediate the range <c>(Start + gap, Finish − gap)</c>, which
///     on a zero-length course is empty — so EVERY checkpoint add is refused.
///   - the replay snaps Finish to node 0, <c>anchorsFor</c> finds no interval, and the field comes back
///     empty.
///
/// The race would be created successfully, report "1 checkpoint pair", and be silently unusable. A course
/// with no geometry is not a course; it must be refused at creation, where the user can still pick another
/// ride.
///
/// The GRID is the test, not <c>Activity.TotalDistance</c> — those are different measurements, and a stored
/// distance says nothing about whether there is geometry to race over.
/// </summary>
public class RaceDegenerateCourseTests : IAsyncLifetime
{
    private const string Me = "me";

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = "Me" });
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    /// <summary>Gridable by default; a test that wants a broken course marks its own track unraceable.</summary>
    private readonly FakeRaceCourseService _courses = FakeRaceCourseService.OfLength(38910);

    private async Task<int> SeedCourseAsync(double? totalDistance)
    {
        var activity = new Activity
        {
            TrackName = "Course",
            UserId = Me,
            Visibility = "friends",
            Lifecycle = LifecycleStates.Released,
            TotalDistance = totalDistance,
            ActivityType = "Ride",
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        return activity.TrackId.Value;
    }

    private (RaceCrud Races, RaceCheckpointCrud Checkpoints) CrudAs(string userId)
    {
        var currentUser = new TestCurrentUserService(userId);
        var friendship = new FriendshipService(_context, null!, null!, NullLogger<FriendshipService>.Instance, TimeProvider.System);
        var access = new RaceAccess(_context, new VisibilityService(currentUser, friendship, _context));
        return (new RaceCrud(_context, access, _courses, TimeProvider.System), new RaceCheckpointCrud(_context, access, _courses));
    }

    /// <summary>
    /// A course with no gridable geometry cannot carry a race — refuse it at the door.
    ///
    /// The stored distance is varied ALONGSIDE the missing grid, and deliberately includes a course that
    /// claims a healthy 12 km: the two are different measurements, and the number in the column has never
    /// been evidence that there is geometry to race over.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(12000d)]
    public async Task CreateRace_OverACourseWithNoGeometry_IsRefused(double? totalDistance)
    {
        var courseTrackId = await SeedCourseAsync(totalDistance);
        _courses.WithUnraceableTrack(courseTrackId);
        var (races, _) = CrudAs(Me);

        var race = await races.CreateRaceAsync(new TrackId(courseTrackId), "Nowhere", null, "friends");

        Assert.Null(race);
    }

    /// <summary>
    /// The refusal must not be collateral damage: a course WITH geometry still works, and its checkpoint
    /// editor still works. Without this, "refuse everything" would pass the test above.
    /// </summary>
    [Fact]
    public async Task OnARealCourse_TheRaceIsCreated_AndTheCheckpointEditorWorks()
    {
        var courseTrackId = await SeedCourseAsync(38910);
        var (races, checkpoints) = CrudAs(Me);

        var race = await races.CreateRaceAsync(new TrackId(courseTrackId), "Ski to Sea", null, "friends");

        Assert.NotNull(race);

        // Start at 0 and Finish at the course length — and an intermediate checkpoint lands between them.
        var seeded = race!.Checkpoints.OrderBy(c => c.ChainageMeters).ToList();
        Assert.Equal(0, seeded[0].ChainageMeters);
        Assert.Equal(38910, seeded[^1].ChainageMeters);

        var (result, checkpoint) = await checkpoints.AddCheckpointAsync(race.RaceId, "Halfway", 20000);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(20000, checkpoint!.ChainageMeters);
    }
}
