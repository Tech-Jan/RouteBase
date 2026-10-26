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
/// Moving, renaming and deleting checkpoints.
///
/// The invariant under test is that <c>seq</c> order and chainage order are the SAME order. The pacing
/// model walks checkpoints pairwise by seq, so a seq that disagrees with chainage hands it a negative
/// interval. Dragging a checkpoint past its neighbour is the operation most likely to break that, and
/// there is a UNIQUE index on (race_ID, seq) waiting to catch a renumber that collides mid-save.
/// </summary>
public class RaceCheckpointEditTests : IAsyncLifetime
{
    private const string Me = "me";
    private const double CourseLength = 38910;

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;
    // The course GRID, which is what every chainage is measured against — see FakeRaceCourseService.
    private readonly FakeRaceCourseService Courses = FakeRaceCourseService.OfLength(CourseLength);
    private int _courseTrackId;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = "Me" });
        await _context.SaveChangesAsync();

        var activity = new Activity
        {
            TrackName = "Course",
            UserId = Me,
            Visibility = "friends",
            Lifecycle = LifecycleStates.Released,
            TotalDistance = CourseLength,
            ActivityType = "Ride",
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        _courseTrackId = activity.TrackId.Value;
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

    private RaceCheckpointCrud Crud() => new(_context, Access(), Courses);

    private RaceCrud Races() => new(_context, Access(), Courses, TimeProvider.System);

    private async Task<Race> RaceAsync() =>
        (await Races().CreateRaceAsync(new TrackId(_courseTrackId), "Race", new DateTime(2025, 7, 13, 0, 0, 0, DateTimeKind.Unspecified), "friends"))!;

    /// <summary>Checkpoints as (name, chainage, seq), in seq order — the shape every assertion checks.</summary>
    private async Task<List<(string? Name, double Chainage, int Seq)>> LayoutAsync(RaceId raceId)
    {
        var race = await Races().GetRaceAsync(raceId);
        return race!.Checkpoints.OrderBy(c => c.Seq)
            .Select(c => (c.CheckpointName, c.ChainageMeters, c.Seq)).ToList();
    }

    /// <summary>
    /// Seq must be gapless the moment a checkpoint is INSERTED — not merely after some later edit happens
    /// to renumber it. Every other test here does a move or a delete afterwards, and that second Renumber
    /// would paper over a gap left by the first.
    ///
    /// The hazard: OwnedRaceAsync returns a TRACKED race with Checkpoints included, so
    /// `_context.RaceCheckpoints.Add(checkpoint)` with RaceId set triggers EF relationship fix-up — the new
    /// row is already IN race.Checkpoints. `.Append(checkpoint)` on top of that would enumerate it twice,
    /// and Renumber would assign it two seqs in a row, leaving a hole where the first one was.
    /// </summary>
    [Fact]
    public async Task Add_ACheckpoint_LeavesSeqGaplessImmediately()
    {
        var race = await RaceAsync();

        var (result, _) = await Crud().AddCheckpointAsync(race.RaceId, "A", 10_000);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(
            [("Start", 0, 0), ("A", 10_000, 1), ("Finish", CourseLength, 2)],
            await LayoutAsync(race.RaceId));
    }

    /// <summary>
    /// Two checkpoints SWAPPING seq must not trip the unique index on (race_ID, seq).
    ///
    /// This was latent behind the insert-time gap above: Renumber assigns the final numbers directly, so a
    /// swap asks EF to order two updates that transiently collide (A takes B's seq while B still holds it).
    /// There is no such order, and EF throws "a circular dependency was detected in the data to be saved".
    /// The old gaps happened to leave enough slack that the collision never occurred — so fixing the
    /// numbering EXPOSED this. Both are the same invariant: seq order is chainage order, gaplessly.
    ///
    /// The minimal shape is two checkpoints trading places, with nothing else to absorb the collision.
    /// </summary>
    [Fact]
    public async Task Move_SwappingTwoCheckpoints_DoesNotCollideOnTheUniqueSeqIndex()
    {
        var race = await RaceAsync();
        var crud = Crud();
        var (_, a) = await crud.AddCheckpointAsync(race.RaceId, "A", 10_000);
        await crud.AddCheckpointAsync(race.RaceId, "B", 20_000);

        // A (seq 1) jumps over B (seq 2): they trade places, and both rows must be renumbered at once.
        var (result, _) = await Crud().UpdateCheckpointAsync(
            race.RaceId, a!.CheckpointId, new RaceCheckpointEdit { ChainageMeters = 30_000 });

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(
            [("Start", 0, 0), ("B", 20_000, 1), ("A", 30_000, 2), ("Finish", CourseLength, 3)],
            await LayoutAsync(race.RaceId));
    }

    [Fact]
    public async Task Move_PastANeighbour_RenumbersSeqToMatchChainage()
    {
        var race = await RaceAsync();
        var crud = Crud();
        var (_, first) = await crud.AddCheckpointAsync(race.RaceId, "A", 10_000);
        await crud.AddCheckpointAsync(race.RaceId, "B", 20_000);

        // Drag A from 10 km to 30 km — past B. If seq did not follow, the model would walk B→A and
        // see a negative interval.
        var (result, _) = await Crud().UpdateCheckpointAsync(
            race.RaceId, first!.CheckpointId, new RaceCheckpointEdit { ChainageMeters = 30_000 });

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(
            [("Start", 0, 0), ("B", 20_000, 1), ("A", 30_000, 2), ("Finish", CourseLength, 3)],
            await LayoutAsync(race.RaceId));
    }

    [Fact]
    public async Task Delete_AMiddleCheckpoint_ClosesTheSeqGap()
    {
        var race = await RaceAsync();
        var crud = Crud();
        var (_, a) = await crud.AddCheckpointAsync(race.RaceId, "A", 10_000);
        await crud.AddCheckpointAsync(race.RaceId, "B", 20_000);

        Assert.Equal(RaceWriteResult.Ok, await Crud().DeleteCheckpointAsync(race.RaceId, a!.CheckpointId));

        // Seq must be gapless AND chainage-ordered — a hole would break the unique index on the next insert.
        Assert.Equal(
            [("Start", 0, 0), ("B", 20_000, 1), ("Finish", CourseLength, 2)],
            await LayoutAsync(race.RaceId));
    }

    [Fact]
    public async Task Rename_LeavesChainageAlone()
    {
        var race = await RaceAsync();
        var (_, a) = await Crud().AddCheckpointAsync(race.RaceId, "A", 10_000);

        // Null chainage means "leave it": a rename must not have to restate a number it never touched.
        var (result, updated) = await Crud().UpdateCheckpointAsync(
            race.RaceId, a!.CheckpointId, new RaceCheckpointEdit { Name = "Feed zone" });

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal("Feed zone", updated!.CheckpointName);
        Assert.Equal(10_000, updated.ChainageMeters);
    }

    [Fact]
    public async Task LegNameAndColor_AreStored_OnTheCheckpointTheIntervalFollows()
    {
        var race = await RaceAsync();
        var (_, a) = await Crud().AddCheckpointAsync(race.RaceId, "XC Ski finish", 5_108);

        var (result, updated) = await Crud().UpdateCheckpointAsync(
            race.RaceId, a!.CheckpointId,
            new RaceCheckpointEdit { LegName = "Downhill", LegColor = "#e8590c" });

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal("Downhill", updated!.LegName);
        Assert.Equal("#e8590c", updated.LegColor);
    }

    /// <summary>
    /// Both lines MOVE. They used to be frozen, on the theory that they ARE the course's ends — but the grid
    /// is the whole recorded ride and the race is a window on it: a ride holds the roll-out to the line and
    /// the cooldown past it. Freezing them also meant a Finish seeded from the wrong metric (the course
    /// activity's own distance, 337 m off the geometry on a real import) could never be corrected.
    ///
    /// This is the contract that replaced <c>Move_TheStartOrFinish_IsRejected</c>. Deleting them is still
    /// refused — see below — because a race needs a line to leave and one to reach.
    /// </summary>
    [Theory]
    [InlineData(0, 1_200)]                        // the Start, moved up past a neutral roll-out
    [InlineData(CourseLength, CourseLength - 400)] // the Finish, pulled back off a cooldown
    public async Task Move_TheStartOrFinish_IsAllowed_WithinTheGrid(double from, double to)
    {
        var race = await RaceAsync();
        var endpoint = race.Checkpoints.Single(c => c.ChainageMeters == from);

        var (result, moved) = await Crud().UpdateCheckpointAsync(
            race.RaceId, endpoint.CheckpointId, new RaceCheckpointEdit { ChainageMeters = to });

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(to, moved!.ChainageMeters);
    }

    /// <summary>An endpoint that crossed its neighbour would stop being the endpoint — the extremes rule is
    /// what every other invariant in the slice is stated against.</summary>
    [Fact]
    public async Task Move_AnEndpoint_PastItsNeighbour_IsRejected()
    {
        var race = await RaceAsync();
        var (_, mid) = await Crud().AddCheckpointAsync(race.RaceId, "Halfway", 20_000);
        var finish = race.Checkpoints.Single(c => c.ChainageMeters == CourseLength);

        var (result, _) = await Crud().UpdateCheckpointAsync(
            race.RaceId, finish.CheckpointId, new RaceCheckpointEdit { ChainageMeters = mid!.ChainageMeters - 500 });

        Assert.Equal(RaceWriteResult.InvalidChainage, result);
    }

    /// <summary>The Finish reaches the end of the RIDE and no further — the grid is the bound, not the
    /// course activity's stored distance, which is a different measurement.</summary>
    [Fact]
    public async Task Move_TheFinish_PastTheEndOfTheGrid_IsRejected()
    {
        var race = await RaceAsync();
        var finish = race.Checkpoints.Single(c => c.ChainageMeters == CourseLength);

        var (result, _) = await Crud().UpdateCheckpointAsync(
            race.RaceId, finish.CheckpointId, new RaceCheckpointEdit { ChainageMeters = CourseLength + 10 });

        Assert.Equal(RaceWriteResult.InvalidChainage, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CourseLength)]
    public async Task Delete_TheStartOrFinish_IsRejected(double chainage)
    {
        var race = await RaceAsync();
        var endpoint = race.Checkpoints.Single(c => c.ChainageMeters == chainage);

        Assert.Equal(
            RaceWriteResult.EndpointCheckpoint,
            await Crud().DeleteCheckpointAsync(race.RaceId, endpoint.CheckpointId));
    }

    [Fact]
    public async Task Rename_TheStartOrFinish_IsAllowed()
    {
        var race = await RaceAsync();
        var start = race.Checkpoints.Single(c => c.ChainageMeters == 0);

        // Labels are free everywhere, the ends included.
        var (result, updated) = await Crud().UpdateCheckpointAsync(
            race.RaceId, start.CheckpointId, new RaceCheckpointEdit { Name = "Mt Baker" });

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal("Mt Baker", updated!.CheckpointName);
    }

    [Fact]
    public async Task Move_OffTheCourse_IsRejected()
    {
        var race = await RaceAsync();
        var (_, a) = await Crud().AddCheckpointAsync(race.RaceId, "A", 10_000);

        var (result, _) = await Crud().UpdateCheckpointAsync(
            race.RaceId, a!.CheckpointId, new RaceCheckpointEdit { ChainageMeters = CourseLength + 1 });

        Assert.Equal(RaceWriteResult.InvalidChainage, result);
    }
}
