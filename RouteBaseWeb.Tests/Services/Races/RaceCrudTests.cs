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
/// RaceCrud's authorization seam, over the REAL VisibilityService and a seeded SQLite DB — the
/// TestVisibilityService double is permissive on CanViewActivityAsync, so it cannot test the gate
/// this slice depends on.
///
/// The three questions this fixture answers:
///   - courses come from activities you OWN;
///   - a friend joins the field on VIEW rights, not ownership;
///   - and those rights are re-checked on every read, so a revoked friendship drops him.
/// </summary>
public class RaceCrudTests : IAsyncLifetime
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
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        // Activity FKs Users — the rows must exist before any activity does.
        _context.Users.AddRange(
            new AppUser { UserId = Me, DisplayName = "Me" },
            new AppUser { UserId = Friend, DisplayName = "Friend" },
            new AppUser { UserId = Stranger, DisplayName = "Stranger" });
        _context.Friendships.Add(new Friendship { RequesterId = Me, AddresseeId = Friend, Status = "accepted" });
        await _context.SaveChangesAsync();

        _myCourseTrackId = await SeedActivityAsync(Me, "My race ride", "friends", 38910);
        _friendTrackId = await SeedActivityAsync(Friend, "Friend's race ride", "friends", 39100);
        _strangerTrackId = await SeedActivityAsync(Stranger, "Stranger's ride", "private", 40000);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private async Task<int> SeedActivityAsync(string userId, string name, string visibility, double distance)
    {
        var activity = new Activity
        {
            TrackName = name,
            UserId = userId,
            Visibility = visibility,
            Lifecycle = LifecycleStates.Released,
            TotalDistance = distance,
            ActivityType = "Ride",
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();
        return activity.TrackId.Value;
    }

    /// <summary>
    /// The three CRUD services, all sharing ONE <see cref="RaceAccess"/> — which is the point: the seam is
    /// singular, the services that sit on it are not. Wired to the real visibility stack (the
    /// TestVisibilityService double is permissive on CanViewActivityAsync, so it cannot test the gate
    /// this slice depends on), acting as <paramref name="userId"/>.
    /// </summary>
    private sealed record RaceServices(
        RaceCrud Races, RaceCheckpointCrud Checkpoints, RaceParticipantCrud Participants);

    private RaceServices CrudAs(string userId)
    {
        var currentUser = new TestCurrentUserService(userId);
        var friendship = new FriendshipService(_context, null!, null!, NullLogger<FriendshipService>.Instance, TimeProvider.System);
        var access = new RaceAccess(_context, new VisibilityService(currentUser, friendship, _context));
        return new RaceServices(
            new RaceCrud(_context, access, Courses, TimeProvider.System),
            new RaceCheckpointCrud(_context, access, Courses),
            new RaceParticipantCrud(_context, access));
    }

    private async Task<Race> CreateMyRaceAsync() =>
        (await CrudAs(Me).Races.CreateRaceAsync(new TrackId(_myCourseTrackId), "Black Forest SHORT TRACK", new DateTime(2025, 7, 13, 0, 0, 0, DateTimeKind.Unspecified), "friends"))!;

    /// <summary>A simulated finisher with a split at each given checkpoint — imports are the only path that
    /// makes these, so tests seed them directly.</summary>
    private async Task<RaceParticipant> SeedSimulatedFinisherAsync(
        Race race, string name, params (CheckpointId checkpointId, double elapsed)[] splits)
    {
        var participant = new RaceParticipant
        {
            RaceId = race.RaceId,
            Kind = RaceParticipantKind.Simulated,
            DisplayName = name,
            Seed = 7,
            OfficialFinishSeconds = splits.Length > 0 ? splits[^1].elapsed : null,
        };
        _context.RaceParticipants.Add(participant);
        await _context.SaveChangesAsync();

        foreach (var (checkpointId, elapsed) in splits)
            _context.RaceSplitTimes.Add(new RaceSplitTime
            {
                ParticipantId = participant.ParticipantId, CheckpointId = checkpointId, ElapsedSeconds = elapsed,
            });
        await _context.SaveChangesAsync();
        return participant;
    }

    // --- Course ------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateRace_OverOwnedActivity_SeedsStartAndFinishCheckpoints()
    {
        var race = await CreateMyRaceAsync();

        Assert.NotNull(race);
        Assert.Equal(new TrackId(_myCourseTrackId), race.CourseTrackId);
        Assert.Equal(Me, race.UserId);

        // Without these two there is no interval, and nothing can be simulated.
        var checkpoints = race.Checkpoints.OrderBy(c => c.Seq).ToList();
        Assert.Equal(2, checkpoints.Count);
        Assert.Equal("Start", checkpoints[0].CheckpointName);
        Assert.Equal(0, checkpoints[0].ChainageMeters);
        Assert.Equal("Finish", checkpoints[1].CheckpointName);
        Assert.Equal(38910, checkpoints[1].ChainageMeters);   // the course GRID's length
    }

    /// <summary>
    /// The Finish is seeded from the course GRID, never from <c>Activity.TotalDistance</c>.
    ///
    /// They are different measurements of the same ride and they disagree: the grid is the raw haversine sum
    /// over the GPS points, while an imported activity's distance is whatever the source computed (Strava's,
    /// filtered). On the 70 km Black Forest course that is 337 m — and seeded from the wrong one, the Finish
    /// sat 337 m inside the ride, the course polyline (drawn leg by leg) stopped there, and the endpoint was
    /// frozen so the user could not drag it back.
    ///
    /// The two numbers DIVERGING is the whole test. Keep them apart.
    /// </summary>
    [Fact]
    public async Task CreateRace_SeedsTheFinishFromTheGrid_NotTheActivitysOwnDistance()
    {
        var courseTrackId = await SeedActivityAsync(Me, "Imported ride", "friends", 70593);
        Courses.WithTrack(courseTrackId, 70930);

        var race = (await CrudAs(Me).Races.CreateRaceAsync(new TrackId(courseTrackId), "Ultra", null, "friends"))!;

        Assert.Equal(70930, race.Checkpoints.Max(c => c.ChainageMeters));
    }

    /// <summary>A course the grid cannot be built from (no GPS, or shorter than one step) is not raceable.</summary>
    [Fact]
    public async Task CreateRace_OverACourseWithNoGeometry_IsRefused()
    {
        var courseTrackId = await SeedActivityAsync(Me, "Distance but no GPS", "friends", 12000);
        Courses.WithUnraceableTrack(courseTrackId);

        Assert.Null(await CrudAs(Me).Races.CreateRaceAsync(new TrackId(courseTrackId), "Nowhere", null, "friends"));
    }

    [Fact]
    public async Task CreateRace_OverFriendsActivity_IsRejected()
    {
        // Viewable is not the same as ownable: you may race over a friend's ride, not define a course from it.
        var race = await CrudAs(Me).Races.CreateRaceAsync(new TrackId(_friendTrackId), "Nope", null, "friends");
        Assert.Null(race);
    }

    // --- Real participants (the friend question) -----------------------------------------------------

    [Fact]
    public async Task AddRealParticipant_FriendsVisibleActivity_IsAccepted()
    {
        var race = await CreateMyRaceAsync();

        var (result, participant) = await CrudAs(Me).Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.NotNull(participant);
        Assert.Equal(RaceParticipantKind.Real, participant!.Kind);
        Assert.Equal(new TrackId(_friendTrackId), participant.TrackId);
        Assert.Equal("Friend's race ride", participant.DisplayName);
    }

    [Fact]
    public async Task AddRealParticipant_StrangersPrivateActivity_IsRejected()
    {
        var race = await CreateMyRaceAsync();

        var (result, participant) = await CrudAs(Me).Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_strangerTrackId), null);

        Assert.Equal(RaceWriteResult.ActivityNotVisible, result);
        Assert.Null(participant);
    }

    [Fact]
    public async Task AddRealParticipant_SameActivityTwice_IsRejected()
    {
        var race = await CreateMyRaceAsync();
        var crud = CrudAs(Me);
        await crud.Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        var (result, _) = await crud.Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        Assert.Equal(RaceWriteResult.DuplicateParticipant, result);
    }

    [Fact]
    public async Task AddRealParticipant_WithAName_UsesItInsteadOfTheRidesName()
    {
        // The sibling of the fallback above: a rider is called after the PERSON, not after the file.
        var race = await CreateMyRaceAsync();

        var (result, participant) = await CrudAs(Me).Participants.AddRealParticipantAsync(
            race.RaceId, new TrackId(_friendTrackId), "  Marco  ");

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal("Marco", participant!.DisplayName);
    }

    /// <summary>
    /// The bug this fixes: colour used to be a function of KIND, so every real rider came out the same
    /// gold — on the map, in the results swatches, and as two indistinguishable solid lines on the speed
    /// chart, which is the surface you add a second rider FOR.
    /// </summary>
    [Fact]
    public async Task AddRealParticipant_TwoRiders_GetDifferentColours()
    {
        var race = await CreateMyRaceAsync();
        var crud = CrudAs(Me);

        var (_, first) = await crud.Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_myCourseTrackId), null);
        var (_, second) = await crud.Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        // Gold first, so a race with ONE real rider looks exactly as it did before this existed.
        Assert.Equal(RaceColors.DefaultRealColor, first!.Color);
        Assert.NotEqual(first.Color, second!.Color);
        Assert.Contains(second.Color, RaceColors.RealRiderPalette);
    }

    // --- Renaming a rider ----------------------------------------------------------------------------

    [Fact]
    public async Task RenameParticipant_SetsTheNameAndTrimsIt()
    {
        var race = await CreateMyRaceAsync();
        var crud = CrudAs(Me);
        var (_, added) = await crud.Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        var (result, renamed) = await crud.Participants.RenameParticipantAsync(
            race.RaceId, added!.ParticipantId, "  Marco  ");

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal("Marco", renamed!.DisplayName);
        Assert.Equal("Marco", (await _context.RaceParticipants.FindAsync(added.ParticipantId))!.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RenameParticipant_BlankName_FallsBackToTheRidesOwnName(string blank)
    {
        // THE contract behind the empty box: clearing the name is not "no name", it is "the ride's name
        // again". The client sends the raw value and the server owns this rule — one copy of it.
        var race = await CreateMyRaceAsync();
        var crud = CrudAs(Me);
        var (_, added) = await crud.Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), "Marco");

        var (result, renamed) = await crud.Participants.RenameParticipantAsync(race.RaceId, added!.ParticipantId, blank);

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal("Friend's race ride", renamed!.DisplayName);
    }

    [Fact]
    public async Task RenameParticipant_ASimulatedRider_HasNoRideToFallBackTo()
    {
        // No track_id, so a blank name leaves him nameless and the board shows his bib. Not a special
        // case in the code — just what "the ride's own name" means when there is no ride.
        var race = await CreateMyRaceAsync();
        var sim = await SeedSimulatedFinisherAsync(race, "Rider 214");

        var (result, renamed) = await CrudAs(Me).Participants.RenameParticipantAsync(race.RaceId, sim.ParticipantId, "");

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Null(renamed!.DisplayName);
    }

    [Fact]
    public async Task RenameParticipant_TooLong_IsRejectedAndChangesNothing()
    {
        // Rejected, never truncated: a silently shortened name is a name the user did not choose.
        var race = await CreateMyRaceAsync();
        var crud = CrudAs(Me);
        var (_, added) = await crud.Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), "Marco");

        var (result, renamed) = await crud.Participants.RenameParticipantAsync(
            race.RaceId, added!.ParticipantId, new string('x', 256));

        Assert.Equal(RaceWriteResult.InvalidDisplayName, result);
        Assert.Null(renamed);
        Assert.Equal("Marco", (await _context.RaceParticipants.FindAsync(added.ParticipantId))!.DisplayName);
    }

    [Fact]
    public async Task RenameParticipant_ThroughAnotherRace_IsRejected()
    {
        // The id comes from the request, so the gate has to be the RACE: a participant is looked up inside
        // its own field, never by id alone.
        var mine = await CreateMyRaceAsync();
        var other = (await CrudAs(Me).Races.CreateRaceAsync(new TrackId(_myCourseTrackId), "Another race", null, "friends"))!;
        var crud = CrudAs(Me);
        var (_, added) = await crud.Participants.AddRealParticipantAsync(mine.RaceId, new TrackId(_friendTrackId), "Marco");

        var (result, _) = await crud.Participants.RenameParticipantAsync(other.RaceId, added!.ParticipantId, "Sarah");

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
        Assert.Equal("Marco", (await _context.RaceParticipants.FindAsync(added.ParticipantId))!.DisplayName);
    }

    [Fact]
    public async Task RenameParticipant_ByAStranger_IsRejected()
    {
        var race = await CreateMyRaceAsync();
        var (_, added) = await CrudAs(Me).Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), "Marco");

        var (result, _) = await CrudAs(Stranger).Participants.RenameParticipantAsync(race.RaceId, added!.ParticipantId, "Sarah");

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
        Assert.Equal("Marco", (await _context.RaceParticipants.FindAsync(added.ParticipantId))!.DisplayName);
    }

    [Fact]
    public async Task GetRace_AfterFriendshipRevoked_DropsThatRealParticipant()
    {
        // THE stability property: the stored track_id is a pointer, not a standing grant. A rider added
        // while we were friends must disappear from the field the moment the friendship goes away — even
        // though nothing about the race row changed.
        var race = await CreateMyRaceAsync();
        await CrudAs(Me).Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        var before = await CrudAs(Me).Races.GetRaceAsync(race.RaceId);
        Assert.Single(before!.Participants);

        _context.Friendships.RemoveRange(_context.Friendships);
        await _context.SaveChangesAsync();

        var after = await CrudAs(Me).Races.GetRaceAsync(race.RaceId);

        Assert.Empty(after!.Participants);
        // The row is still there — it was filtered on read, not deleted.
        Assert.Equal(1, await _context.RaceParticipants.CountAsync(p => p.RaceId == race.RaceId));
    }

    // --- The races list (counts must be REAL — they used to render as 0) -----------------------------

    [Fact]
    public async Task GetRacesAsync_ReturnsParticipantAndCheckpointCounts()
    {
        // The list read has no Include and there is no lazy-loading proxy, so reading race.Participants
        // .Count off the entity rendered 0 for every race. The counts are projected in SQL now.
        var race = await CreateMyRaceAsync();
        var crud = CrudAs(Me);
        await crud.Checkpoints.AddCheckpointAsync(race.RaceId, "Hinterzarten", 9000);
        await crud.Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);
        await crud.Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_myCourseTrackId), null);
        _context.RaceParticipants.Add(new RaceParticipant
        {
            RaceId = race.RaceId,
            Kind = RaceParticipantKind.Simulated,
            DisplayName = "Simulated finisher",
        });
        await _context.SaveChangesAsync();

        var summaries = await crud.Races.GetRacesAsync();

        var summary = Assert.Single(summaries);
        Assert.Equal(race.RaceId.Value, summary.RaceId);
        Assert.Equal(3, summary.CheckpointCount);    // Start + Hinterzarten + Finish
        Assert.Equal(3, summary.ParticipantCount);   // 2 real + 1 simulated
        // The list card renders the course shape + its length: both come off the course activity.
        Assert.Equal(_myCourseTrackId, summary.CourseTrackId);
        Assert.Equal(38910, summary.RaceDistanceMeters);
    }

    [Fact]
    public async Task GetRacesAsync_ExcludesRacesTheCallerCannotSee()
    {
        var race = await CreateMyRaceAsync();   // visibility "friends"

        Assert.Single(await CrudAs(Me).Races.GetRacesAsync());
        Assert.Equal(race.RaceId.Value, (await CrudAs(Friend).Races.GetRacesAsync()).Single().RaceId);
        Assert.Empty(await CrudAs(Stranger).Races.GetRacesAsync());
    }

    // AUTHZ-P2: a race the caller can SEE does not authorize its COURSE. A friends-race built over the
    // owner's OWN private ride must withhold that ride's track_id AND distance from a race viewer — the card
    // mirrors the replay's CanViewActivityAsync gate. The owner still sees their own course.
    [Fact]
    public async Task GetRacesAsync_PrivateCourse_IsWithheldFromViewer_ButShownToOwner()
    {
        // Me may course from their OWN ride regardless of its visibility, so a private course is legitimate.
        var privateCourse = await SeedActivityAsync(Me, "My secret route", "private", 12345);
        Courses.WithTrack(privateCourse, 12345);
        var race = (await CrudAs(Me).Races.CreateRaceAsync(new TrackId(privateCourse), "Secret course race", null, "friends"))!;

        // Owner: the card carries the course id + length.
        var mine = Assert.Single(await CrudAs(Me).Races.GetRacesAsync());
        Assert.Equal(privateCourse, mine.CourseTrackId);
        // 12340, not the activity's 12345: the card reports the RACE — Finish minus Start, in grid metres,
        // which land on whole steps. The stored distance is a different measurement and is not this number.
        Assert.Equal(12340, mine.RaceDistanceMeters);

        // Friend: can see the friends-race, but NOT the private course — so the card withholds both, exactly
        // as the replay 404s the course (RaceReplayService.GetReplayAsync).
        var friendsView = Assert.Single(await CrudAs(Friend).Races.GetRacesAsync());
        Assert.Equal(race.RaceId.Value, friendsView.RaceId);       // the race is still listed
        Assert.Null(friendsView.CourseTrackId);              // ...but course-less
        Assert.Null(friendsView.RaceDistanceMeters);
    }

    // The gate must not OVER-withhold: a viewer who CAN see the course (a friend, released friends-course)
    // still gets its id + length. Guards against the fix quietly blanking every viewer's card.
    [Fact]
    public async Task GetRacesAsync_FriendsVisibleCourse_IsShownToTheViewingFriend()
    {
        var race = await CreateMyRaceAsync();   // course _myCourseTrackId is friends + released, dist 38910

        var friendsView = Assert.Single(await CrudAs(Friend).Races.GetRacesAsync());
        Assert.Equal(race.RaceId.Value, friendsView.RaceId);
        Assert.Equal(_myCourseTrackId, friendsView.CourseTrackId);
        Assert.Equal(38910, friendsView.RaceDistanceMeters);
    }

    [Fact]
    public async Task GetRaceAsync_DoesNotDeleteHiddenParticipantsOnLaterSave()
    {
        // GetRaceAsync REASSIGNS race.Participants to the re-authorized subset. Read tracked, that looks
        // to EF like "these required-FK children were removed" — the next save in the same scope would
        // cascade-delete the friend's rider row. Latent today; silent data loss the day a write path
        // calls GetRaceAsync before saving.
        var race = await CreateMyRaceAsync();
        await CrudAs(Me).Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        _context.Friendships.RemoveRange(_context.Friendships);
        await _context.SaveChangesAsync();

        var loaded = await CrudAs(Me).Races.GetRaceAsync(race.RaceId);
        Assert.Empty(loaded!.Participants);   // dropped from the field on read...

        await _context.SaveChangesAsync();    // ...and NOT from the database.

        _context.ChangeTracker.Clear();
        Assert.Equal(1, await _context.RaceParticipants.CountAsync(p => p.RaceId == race.RaceId));
    }

    // --- Checkpoints -------------------------------------------------------------------------------

    [Fact]
    public async Task AddCheckpoint_RenumbersSeqIntoChainageOrder()
    {
        // The pacing model walks checkpoints pairwise BY SEQ; a seq that disagrees with chainage order
        // would hand it a negative interval.
        var race = await CreateMyRaceAsync();
        var crud = CrudAs(Me);

        await crud.Checkpoints.AddCheckpointAsync(race.RaceId, "Raimartihof", 21000);
        await crud.Checkpoints.AddCheckpointAsync(race.RaceId, "200m to go", 38710);
        await crud.Checkpoints.AddCheckpointAsync(race.RaceId, "Hinterzarten", 9000);

        var reloaded = await crud.Races.GetRaceAsync(race.RaceId);
        var names = reloaded!.Checkpoints.OrderBy(c => c.Seq).Select(c => c.CheckpointName).ToList();

        Assert.Equal(new[] { "Start", "Hinterzarten", "Raimartihof", "200m to go", "Finish" }, names, StringComparer.Ordinal);
        var chainages = reloaded.Checkpoints.OrderBy(c => c.Seq).Select(c => c.ChainageMeters).ToList();
        Assert.Equal(chainages.OrderBy(c => c).ToList(), chainages);
    }

    [Fact]
    public async Task AddCheckpoint_BeyondFinish_IsRejected()
    {
        var race = await CreateMyRaceAsync();

        var (result, checkpoint) = await CrudAs(Me).Checkpoints.AddCheckpointAsync(race.RaceId, "Off the end", 99999);

        Assert.Equal(RaceWriteResult.InvalidChainage, result);
        Assert.Null(checkpoint);
    }

    // --- Race-level authorization -------------------------------------------------------------------

    [Fact]
    public async Task AddParticipant_ToSomeoneElsesRace_IsRejected()
    {
        var race = await CreateMyRaceAsync();

        // The friend can SEE my race, but a race is owner-only by DEFAULT (edit policy 'owner'). Opening it
        // up to friends/public is covered in RaceCollaborationTests.
        var (result, _) = await CrudAs(Friend).Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
    }

    [Fact]
    public async Task GetRace_FriendsRace_IsVisible_StrangersIsNot()
    {
        var race = await CreateMyRaceAsync();

        Assert.NotNull(await CrudAs(Friend).Races.GetRaceAsync(race.RaceId));
        Assert.Null(await CrudAs(Stranger).Races.GetRaceAsync(race.RaceId));
    }

    [Fact]
    public async Task CreatedRace_SerializesToJson_WithoutAnObjectCycle()
    {
        // Found in smoke: the API returned the EF entity, and EF's relationship fix-up had populated the
        // back-references — Race → Checkpoints → Race → ... System.Text.Json bailed at depth 32 with a
        // 500, AFTER the race had already been written. The worst shape of failure there is: the user
        // sees an error for something that actually succeeded.
        var race = await CreateMyRaceAsync();
        var reloaded = await CrudAs(Me).Races.GetRaceAsync(race.RaceId);

        var json = System.Text.Json.JsonSerializer.Serialize(reloaded);

        Assert.Contains("Black Forest SHORT TRACK", json, StringComparison.Ordinal);
    }

    // --- Duplicate ("Save as") -----------------------------------------------------------------------

    [Fact]
    public async Task DuplicateRace_DeepCopiesCheckpointsFieldAndSplits_UnderANewOwnedRace()
    {
        var source = await CreateMyRaceAsync();
        var crud = CrudAs(Me);
        await crud.Checkpoints.AddCheckpointAsync(source.RaceId, "Hinterzarten", 9000);
        await crud.Races.SetReferenceEffortAsync(source.RaceId, new TrackId(_friendTrackId));
        await crud.Participants.AddRealParticipantAsync(source.RaceId, new TrackId(_friendTrackId), "Friend");

        var sourceCps = (await crud.Races.GetRaceAsync(source.RaceId))!.Checkpoints.OrderBy(c => c.Seq).ToList();
        var sim = await SeedSimulatedFinisherAsync(source, "KURZ Felix",
            (sourceCps[1].CheckpointId, 1800), (sourceCps[2].CheckpointId, 4619.4));   // Hinterzarten, Finish

        var copy = await crud.Races.DuplicateRaceAsync(source.RaceId);

        Assert.NotNull(copy);
        Assert.NotEqual(source.RaceId, copy!.RaceId);
        Assert.Equal(Me, copy.UserId);
        Assert.Equal("Black Forest SHORT TRACK (copy)", copy.RaceName);
        Assert.Equal(source.CourseTrackId, copy.CourseTrackId);   // the ride is shared, not duplicated
        Assert.Equal(new TrackId(_friendTrackId), copy.ReferenceTrackId);

        // Checkpoints: same shape, brand-new ids.
        var copyCps = copy.Checkpoints.OrderBy(c => c.Seq).ToList();
        Assert.Equal(new[] { "Start", "Hinterzarten", "Finish" }, copyCps.Select(c => c.CheckpointName), StringComparer.Ordinal);
        Assert.Equal(new double[] { 0, 9000, 38910 }, copyCps.Select(c => c.ChainageMeters));
        var sourceCpIds = sourceCps.Select(c => c.CheckpointId).ToHashSet();
        Assert.All(copyCps, c => Assert.DoesNotContain(c.CheckpointId, sourceCpIds));

        // Field: the real rider shares his track; the simulated finisher is a new row.
        Assert.Equal(2, copy.Participants.Count);
        Assert.Equal(new TrackId(_friendTrackId), copy.Participants.Single(p => p.Kind == RaceParticipantKind.Real).TrackId);
        var copySim = copy.Participants.Single(p => p.Kind == RaceParticipantKind.Simulated);
        Assert.NotEqual(sim.ParticipantId, copySim.ParticipantId);

        // Split times: the crux — remapped to the NEW participant AND the NEW checkpoints, values intact.
        var copySplits = await _context.RaceSplitTimes.AsNoTracking()
            .Where(s => s.ParticipantId == copySim.ParticipantId).ToListAsync();
        Assert.Equal(2, copySplits.Count);
        var copyCpIds = copyCps.Select(c => c.CheckpointId).ToHashSet();
        Assert.All(copySplits, s => Assert.Contains(s.CheckpointId, copyCpIds));
        var finishCpId = copyCps.Single(c => c.CheckpointName == "Finish").CheckpointId;
        Assert.Equal(4619.4, copySplits.Single(s => s.CheckpointId == finishCpId).ElapsedSeconds, 3);
    }

    [Fact]
    public async Task DuplicateRace_LeavesTheSourceUntouched()
    {
        var source = await CreateMyRaceAsync();
        var crud = CrudAs(Me);
        await crud.Participants.AddRealParticipantAsync(source.RaceId, new TrackId(_friendTrackId), null);
        var finishId = (await crud.Races.GetRaceAsync(source.RaceId))!
            .Checkpoints.OrderByDescending(c => c.Seq).First().CheckpointId;
        await SeedSimulatedFinisherAsync(source, "Sim", (finishId, 4000));

        var racesBefore = await _context.Races.CountAsync();
        await crud.Races.DuplicateRaceAsync(source.RaceId);

        Assert.Equal(racesBefore + 1, await _context.Races.CountAsync());
        Assert.Equal(2, await _context.RaceCheckpoints.CountAsync(c => c.RaceId == source.RaceId));  // Start + Finish
        Assert.Equal(2, await _context.RaceParticipants.CountAsync(p => p.RaceId == source.RaceId)); // real + sim
        var sourceParticipantIds = await _context.RaceParticipants
            .Where(p => p.RaceId == source.RaceId).Select(p => p.ParticipantId).ToListAsync();
        Assert.Equal(1, await _context.RaceSplitTimes.CountAsync(s => sourceParticipantIds.Contains(s.ParticipantId)));
    }

    // AUTHZ-P1 (decided 2026-07-29): duplicating a visible race used to hand the caller a race THEY
    // own pointing at someone else's activity as its course — and the replay then served that activity's
    // full GPS, because owning the copy makes it visible to you. This test previously pinned that as
    // intended ("MakesTheCopyMine", asserting copy.CourseTrackId == _friendTrackId); that contract is gone,
    // so the case is rewritten rather than weakened. The bar is now CreateRaceAsync's: you may race over a
    // friend's ride, but you may not define — or inherit — a course from it.
    [Fact]
    public async Task DuplicateRace_OfARaceOverSomeoneElsesCourse_ReturnsNull()
    {
        var friendsRace = (await CrudAs(Friend).Races.CreateRaceAsync(new TrackId(_friendTrackId), "Friend's race", null, "friends"))!;

        Assert.Null(await CrudAs(Me).Races.DuplicateRaceAsync(friendsRace.RaceId));
        Assert.Empty(await _context.Races.Where(r => r.UserId == Me).ToListAsync());   // no row created either
    }

    // Note: there is deliberately no "visible race over MY course" case here — CreateRaceAsync already
    // requires the author to own the course, so such a race cannot exist through the public API. The
    // surviving half of the old contract (the copy becomes mine) is pinned by
    // DuplicateRace_DeepCopiesCheckpointsFieldAndSplits_UnderANewOwnedRace above.

    [Fact]
    public async Task DuplicateRace_OfARaceICannotSee_ReturnsNull()
    {
        var strangersRace = (await CrudAs(Stranger).Races.CreateRaceAsync(new TrackId(_strangerTrackId), "Secret", null, "private"))!;

        Assert.Null(await CrudAs(Me).Races.DuplicateRaceAsync(strangersRace.RaceId));
    }

    // --- Delete seams (the activity appears in a race in two roles; both must clean up) ---------------

    [Fact]
    public async Task DeletingCourseActivity_TakesTheRaceAndItsChildrenWithIt()
    {
        var race = await CreateMyRaceAsync();
        await CrudAs(Me).Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM Activity WHERE track_ID = {0}", _myCourseTrackId);
        _context.ChangeTracker.Clear();

        // A race whose course is gone can never be replayed — it must not survive as a husk.
        Assert.False(await _context.Races.AnyAsync(r => r.RaceId == race.RaceId));
        Assert.False(await _context.RaceCheckpoints.AnyAsync(c => c.RaceId == race.RaceId));
        Assert.False(await _context.RaceParticipants.AnyAsync(p => p.RaceId == race.RaceId));
    }

    [Fact]
    public async Task DeletingAParticipantsActivity_DropsHimButLeavesTheRace()
    {
        var race = await CreateMyRaceAsync();
        await CrudAs(Me).Participants.AddRealParticipantAsync(race.RaceId, new TrackId(_friendTrackId), null);

        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM Activity WHERE track_ID = {0}", _friendTrackId);
        _context.ChangeTracker.Clear();

        // The friend's ride is gone, so the friend is gone — but my race (and its course) is untouched.
        Assert.False(await _context.RaceParticipants.AnyAsync(p => p.TrackId == new TrackId(_friendTrackId)));
        Assert.True(await _context.Races.AnyAsync(r => r.RaceId == race.RaceId));
        Assert.Equal(2, await _context.RaceCheckpoints.CountAsync(c => c.RaceId == race.RaceId));
    }
}
