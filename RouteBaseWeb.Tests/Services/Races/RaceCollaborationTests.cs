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
/// Race collaboration: the owner opens a race up with an edit policy, and a permitted collaborator may
/// then build the field. Pins the NEW contract that supersedes "writes are owner-only":
///
///   - owner policy (the default) — only the owner writes;
///   - friends policy — the owner and accepted friends write;
///   - public policy — anyone who may VIEW the race writes — and edit never exceeds view, so a public
///     edit policy on a friends-only race still admits no stranger;
///   - setting the policy, deleting the race, and defining a course stay owner-only under EVERY policy.
///
/// Wired over the REAL VisibilityService + a seeded SQLite DB — the permissive test double cannot model
/// the friendship gate this feature turns on.
/// </summary>
public class RaceCollaborationTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Friend = "friend";
    private const string Stranger = "stranger";

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;
    // The course GRID, which is what every chainage is measured against — see FakeRaceCourseService.
    private readonly FakeRaceCourseService Courses = FakeRaceCourseService.OfLength(38910);

    private int _myCourseTrackId;
    private int _friendTrackId;
    private int _strangerTrackId;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.AddRange(
            new AppUser { UserId = Me, DisplayName = "Me" },
            new AppUser { UserId = Friend, DisplayName = "Friend" },
            new AppUser { UserId = Stranger, DisplayName = "Stranger" });
        _context.Friendships.Add(new Friendship { RequesterId = Me, AddresseeId = Friend, Status = "accepted" });
        await _context.SaveChangesAsync();

        // Each rider adds a ride HE can view — his own. The course is mine (a course must be owned).
        _myCourseTrackId = await SeedActivityAsync(Me, "My course ride", "friends");
        _friendTrackId = await SeedActivityAsync(Friend, "Friend's ride", "friends");
        _strangerTrackId = await SeedActivityAsync(Stranger, "Stranger's ride", "private");
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private async Task<int> SeedActivityAsync(string userId, string name, string visibility)
    {
        var activity = new Activity
        {
            TrackName = name, UserId = userId, Visibility = visibility,
            Lifecycle = LifecycleStates.Released, TotalDistance = 38910, ActivityType = "Ride",
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        return activity.TrackId.Value;
    }

    private RaceAccess AccessAs(string userId)
    {
        var friendship = new FriendshipService(_context, null!, null!, NullLogger<FriendshipService>.Instance, TimeProvider.System);
        return new RaceAccess(_context, new VisibilityService(new TestCurrentUserService(userId), friendship, _context));
    }

    private sealed record RaceServices(
        RaceCrud Races, RaceCheckpointCrud Checkpoints, RaceParticipantCrud Participants);

    private RaceServices CrudAs(string userId)
    {
        var access = AccessAs(userId);
        return new RaceServices(
            new RaceCrud(_context, access, Courses, TimeProvider.System),
            new RaceCheckpointCrud(_context, access, Courses),
            new RaceParticipantCrud(_context, access));
    }

    /// <summary>A race I own, with the given VIEW visibility and EDIT policy.</summary>
    private async Task<Race> CreateRaceAsync(string visibility, string editPolicy)
    {
        var race = (await CrudAs(Me).Races.CreateRaceAsync(new TrackId(_myCourseTrackId), "Collab race", null, visibility))!;
        if (editPolicy != RaceEditPolicy.Owner)
            Assert.Equal(RaceWriteResult.Ok, await CrudAs(Me).Races.SetEditPolicyAsync(race.RaceId, editPolicy));
        return race;
    }

    // --- Adding a rider under each policy -----------------------------------------------------------

    [Fact]
    public async Task FriendsPolicy_FriendAddsHisOwnRide()
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Friends);

        var (result, participant) = await CrudAs(Friend).Participants
            .AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(new TrackId(_friendTrackId), participant!.TrackId);
    }

    [Fact]
    public async Task FriendsPolicy_StrangerIsRefused()
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Friends);

        var (result, _) = await CrudAs(Stranger).Participants
            .AddRealParticipantAsync(race.RaceId, new TrackId(_strangerTrackId), null);

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
    }

    [Fact]
    public async Task DefaultOwnerPolicy_EvenAFriendIsRefused()
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Owner);

        var (result, _) = await CrudAs(Friend).Participants
            .AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
    }

    /// <summary>The load-bearing invariant: edit never exceeds view. A public EDIT policy on a race only
    /// FRIENDS can view must still admit no stranger.</summary>
    [Fact]
    public async Task PublicPolicy_ButFriendsVisibility_StrangerStillRefused()
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Public);

        var (result, _) = await CrudAs(Stranger).Participants
            .AddRealParticipantAsync(race.RaceId, new TrackId(_strangerTrackId), null);

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
    }

    [Fact]
    public async Task PublicPolicy_AndPublicVisibility_StrangerCanAdd()
    {
        var race = await CreateRaceAsync("public", RaceEditPolicy.Public);

        var (result, participant) = await CrudAs(Stranger).Participants
            .AddRealParticipantAsync(race.RaceId, new TrackId(_strangerTrackId), null);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(new TrackId(_strangerTrackId), participant!.TrackId);
    }

    // --- The other editable paths open up too -------------------------------------------------------

    [Fact]
    public async Task FriendsPolicy_FriendCanAddACheckpoint()
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Friends);

        var (result, checkpoint) = await CrudAs(Friend).Checkpoints
            .AddCheckpointAsync(race.RaceId, "Aid station", 10_000);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.NotNull(checkpoint);
    }

    [Fact]
    public async Task FriendsPolicy_FriendCanSetThePacingReference()
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Friends);

        // The reference need only be VIEWABLE to the setter — the friend's own ride is.
        var result = await CrudAs(Friend).Races.SetReferenceEffortAsync(race.RaceId, new TrackId(_friendTrackId));

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(new TrackId(_friendTrackId), (await _context.Races.FindAsync(race.RaceId))!.ReferenceTrackId);
    }

    [Fact]
    public async Task FriendsPolicy_FriendCanImportResults()
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Friends);
        var import = new RaceImportService(_context, AccessAs(Friend), NullLogger<RaceImportService>.Instance);

        var result = await import.ImportAsync(
            race.RaceId,
            System.Text.Encoding.UTF8.GetBytes("bib,name,finish_time\n1,KURZ Felix,\"1:16:59,4\"\n"),
            new RaceCsvMapping { NameColumn = "name", FinishColumn = "finish_time", BibColumn = "bib" });

        Assert.NotNull(result);
        Assert.Single(result!.Participants);
    }

    // --- Owner-only under EVERY policy --------------------------------------------------------------

    [Fact]
    public async Task SetEditPolicy_ByOwner_OpensTheRaceUp()
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Owner);

        // Before: default owner policy refuses the friend.
        var (before, _) = await CrudAs(Friend).Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);
        Assert.Equal(RaceWriteResult.RaceNotFound, before);

        Assert.Equal(RaceWriteResult.Ok, await CrudAs(Me).Races.SetEditPolicyAsync(race.RaceId, RaceEditPolicy.Friends));

        var (after, _) = await CrudAs(Friend).Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);
        Assert.Equal(RaceWriteResult.Ok, after);
    }

    [Fact]
    public async Task SetEditPolicy_IsOwnerOnly_EvenForACollaborator()
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Friends);

        // The friend may edit the FIELD, but not re-open the race's edit policy.
        var result = await CrudAs(Friend).Races.SetEditPolicyAsync(race.RaceId, RaceEditPolicy.Public);

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
        Assert.Equal(RaceEditPolicy.Friends, (await _context.Races.FindAsync(race.RaceId))!.EditPolicy);
    }

    [Fact]
    public async Task SetEditPolicy_RejectsAnUnknownValue()
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Owner);

        var result = await CrudAs(Me).Races.SetEditPolicyAsync(race.RaceId, "everyone");

        Assert.Equal(RaceWriteResult.InvalidEditPolicy, result);
        Assert.Equal(RaceEditPolicy.Owner, (await _context.Races.FindAsync(race.RaceId))!.EditPolicy);
    }

    [Fact]
    public async Task DeleteRace_IsOwnerOnly_EvenUnderPublicPolicy()
    {
        var race = await CreateRaceAsync("public", RaceEditPolicy.Public);

        var result = await CrudAs(Stranger).Races.DeleteRaceAsync(race.RaceId);

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
        Assert.NotNull(await _context.Races.FindAsync(race.RaceId));
    }

    /// <summary>AC-01: a duplicated race must NOT inherit the source's edit policy. The copy is the
    /// caller's, and it must be owner-edit until the new owner opts in — otherwise duplicating a
    /// public/friends-edit race silently hands out edit rights over a copy its owner never opened up
    /// (edit ⊆ view, and the new owner never chose to share the edit axis).</summary>
    [Fact]
    public async Task DuplicateRace_DoesNotInheritEditPolicy_CopyIsOwnerEditUntilReopened()
    {
        // A public race anyone may view AND edit; the stranger can edit the ORIGINAL
        // (pinned by PublicPolicy_AndPublicVisibility_StrangerCanAdd).
        var race = await CreateRaceAsync("public", RaceEditPolicy.Public);

        // I duplicate it — the copy is mine.
        var copy = await CrudAs(Me).Races.DuplicateRaceAsync(race.RaceId);
        Assert.NotNull(copy);
        Assert.Equal(Me, copy!.UserId);

        // The copy is owner-edit, not public-edit: I never opted the copy open.
        Assert.Equal(RaceEditPolicy.Owner, copy.EditPolicy);
        Assert.Equal(RaceEditPolicy.Owner, (await _context.Races.FindAsync(copy.RaceId))!.EditPolicy);

        // And behaviourally: the stranger who could edit the source is REFUSED on the copy — even though
        // the copy's VIEW visibility is still public, its edit axis is closed.
        var (result, _) = await CrudAs(Stranger).Participants
            .AddRealParticipantAsync(copy.RaceId, new TrackId(_strangerTrackId), null);
        Assert.Equal(RaceWriteResult.RaceNotFound, result);
    }

    // --- The page/UI predicate agrees with the write seam -------------------------------------------

    [Theory]
    [InlineData(Me, true)]
    [InlineData(Friend, true)]
    [InlineData(Stranger, false)]
    public async Task CanEditRaceAsync_UnderFriendsPolicy_MatchesWhoMayWrite(string userId, bool expected)
    {
        var race = await CreateRaceAsync("friends", RaceEditPolicy.Friends);

        Assert.Equal(expected, await AccessAs(userId).CanEditRaceAsync(race.RaceId));
    }
}
