using System.Globalization;
using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Races;
using RouteBaseWeb.Services.Sharing;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Services.Races;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// THE guard for race sharing.
///
/// **The line moved on 2026-07-24, by the owner's decision: entrants are no longer anonymized.** A race's
/// start list and results are the event's own published record, and renaming every finisher to "Rider 7"
/// on a shared replay answered a question nobody asked. So the identity columns now travel, and the tests
/// that used to forbid them are obsolete rather than violated — they pinned a contract that no longer
/// exists, and this file pins the new one.
///
/// What did NOT move, and is the whole reason this file is still called a redaction guard: **a real rider
/// ships his own full GPS**, and that is his, not the sharer's. A real participant reaches the payload only
/// when the grant's owner could publish that ride on its own; every other one is dropped WHOLE — name,
/// splits and geometry together. That is a rule about ownership, so no type can express it, and it is the
/// kind of leak that looks fine in a browser smoke test.
///
/// Two halves, and both still matter:
///   STRUCTURE — the DTO carries the results columns the table renders, so the projection has somewhere
///               to put them and a future "stand-in label" refactor fails here.
///   BEHAVIOUR — a payload built over a race containing a FRIEND's ride carries no trace of that friend,
///               by name or by coordinate, while the owner's own ride is fully present.
/// </summary>
public class ShareRacePayloadRedactionTests : IAsyncLifetime
{
    private const string Owner = "owner-user";
    private const string Friend = "friend-user";

    // Strings and coordinates chosen to be unmistakable in a raw JSON body.
    private const string EntrantName = "Ada Lovelace";
    private const string EntrantBib = "BIB-4711";
    private const string EntrantCategory = "W40-CATEGORY";
    private const string EntrantGender = "SECRET-GENDER";
    private const string EntrantClub = "Bletchley Wheelers";
    private const string FriendName = "Grace Hopper";
    private const double FriendLat = 12.345678;
    private const double OwnerLat = 47.987654;

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _db = null!;
    private int _courseTrackId;
    private int _ownRideTrackId;
    private int _friendRideTrackId;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _db = new RouteBaseDbContext(options);
        await _db.Database.EnsureCreatedAsync();
        // The reader resolves the reference effort's content sha (half the cost-curve cache key); the
        // content tables are patched in rather than EF-modelled.
        await new DbSchemaManager(_db).EnsureTrackContentTablesAsync();

        _db.Users.Add(new AppUser { UserId = Owner, DisplayName = "Owner", Email = "o@t.com" });
        _db.Users.Add(new AppUser { UserId = Friend, DisplayName = FriendName, Email = "f@t.com" });
        await _db.SaveChangesAsync();

        _courseTrackId = await AddActivityAsync(Owner, "Course", LifecycleStates.Released);
        _ownRideTrackId = await AddActivityAsync(Owner, "My ride", LifecycleStates.Released);
        _friendRideTrackId = await AddActivityAsync(Friend, "Friend's ride", LifecycleStates.Released);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    // ---- the structural half ----------------------------------------------------------------------

    [Fact]
    public void ShareRaceParticipantDto_CarriesTheResultsColumns_AndTheOwnersGeometrySlot()
    {
        // The share table shows the same columns the owner's does, so the DTO has to be able to hold them.
        // This fails if someone reinstates a positional stand-in ("Rider N") in place of the real list.
        var properties = typeof(ShareRaceParticipantDto).GetProperties().Select(p => p.Name).ToArray();

        Assert.NotEmpty(properties);   // anti-vacuity: we really did reflect over the DTO
        foreach (var column in new[] { "Name", "Bib", "Category", "Gender", "Club" })
            Assert.Contains(column, properties, StringComparer.Ordinal);

        // ...and the fields that carry the replay itself, or the assertions above would be describing a
        // start list rather than a replay.
        Assert.Contains("Splits", properties, StringComparer.Ordinal);
        Assert.Contains("Track", properties, StringComparer.Ordinal);
    }

    [Fact]
    public void RaceShareDialog_DescribesTheContractThisFilePins()
    {
        // The owner consents through this dialog, so its copy is part of the contract, not decoration —
        // it went stale for a day when the 2026-07-24 decision flipped the DTO and missed the wording.
        // Nothing else fails when the two disagree: the payload stays correct and the owner is misinformed.
        var help = ShareLinkDialogModel.ForRace.Help;

        Assert.NotEmpty(help);   // anti-vacuity: we really did read the dialog copy
        foreach (var anonymityClaim in new[] { "unnamed", "no names", "anonym" })
            Assert.DoesNotContain(anonymityClaim, help, StringComparison.OrdinalIgnoreCase);

        // ...and it still states the half that DID survive, or the copy would under-promise instead.
        Assert.Contains("GPS", help, StringComparison.Ordinal);
    }

    // ---- the behavioural half ---------------------------------------------------------------------

    [Fact]
    public async Task SharePayload_CarriesTheStartList_ButNothingOfANonOwnersRider()
    {
        var race = await SeedRaceAsync();

        var payload = await ReaderWithProjection().GetReplayAsync(GrantFor(race), default);
        Assert.NotNull(payload);

        // Serialized, not inspected field by field: the wire is what a visitor receives, and a leak that
        // only exists after serialization (a nested DTO, a computed property) is still a leak.
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        // The imported results, as published by the event. A visitor reads the race, not a row of numbers.
        foreach (var published in new[] { EntrantName, EntrantBib, EntrantCategory, EntrantGender, EntrantClub })
            Assert.Contains(published, json, StringComparison.Ordinal);

        // The FRIEND is a different matter: his ride is his. He is dropped whole, so neither his name nor
        // his coordinates appear — and the two are asserted separately because geometry does not travel as
        // a name, which is how the other half of a leak like this gets missed.
        Assert.DoesNotContain(FriendName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(FriendLat.ToString("R", CultureInfo.InvariantCulture), json, StringComparison.Ordinal);

        // Anti-vacuity: the payload really is the race, and the owner's OWN ride really is in it — this
        // test must fail if the filter ever becomes "return nothing".
        Assert.Contains(OwnerLat.ToString("R", CultureInfo.InvariantCulture), json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharePayload_DropsAnotherOwnersRealRiderWholesale_AndKeepsTheOwnersOwn()
    {
        var race = await SeedRaceAsync();

        var payload = await ReaderWithProjection().GetReplayAsync(GrantFor(race), default);

        // A friend's rider is not shipped without his track — he is not shipped AT ALL. A half-present
        // rider is a leak waiting for someone to "restore" the missing half.
        var real = payload!.Participants.Where(p => p.Kind == RaceParticipantKind.Real).ToList();
        var mine = Assert.Single(real);
        Assert.NotNull(mine.Track);
        Assert.Equal(OwnerLat, mine.Track!.Lat[0]);
        Assert.Equal("Owner", mine.Name);

        // The simulated field travels whole — the filter is about whose GPS a rider ships, and a simulated
        // entrant ships none.
        Assert.Equal(2, payload.Participants.Count(p => p.Kind == RaceParticipantKind.Simulated));
        Assert.Equal([EntrantName, EntrantName + " II"],
            payload.Participants.Where(p => p.Kind == RaceParticipantKind.Simulated)
                .Select(p => p.Name).ToArray());
        Assert.Equal([EntrantBib, EntrantBib + "-2"],
            payload.Participants.Where(p => p.Kind == RaceParticipantKind.Simulated)
                .Select(p => p.Bib).ToArray());
    }

    [Fact]
    public async Task SharePayload_KeepsTheRaceableFacts()
    {
        // The counterweight to every assertion above: a redacted payload that lost the replay would pass a
        // leak test and ship a blank page.
        var race = await SeedRaceAsync();

        var payload = await ReaderWithProjection().GetReplayAsync(GrantFor(race), default);

        Assert.Equal(race.RaceId.Value, payload!.RaceId);
        Assert.Equal("Black Forest ULTRA", payload.RaceName);
        Assert.Equal(2, payload.Checkpoints.Count);
        Assert.NotEmpty(payload.Course.Lat);
        Assert.All(payload.Participants, p => Assert.NotEmpty(p.Splits));
    }

    // ---- the read-time gates ----------------------------------------------------------------------

    [Fact]
    public async Task Read_IsNull_WhenTheCourseActivityIsNotTheGrantOwnersOwn()
    {
        // The duplicate case, and the reason the course is gated at READ time: DuplicateRaceAsync copies
        // CourseTrackId from a race the caller could merely SEE, so a race someone OWNS can be built over
        // someone else's ride. Publishing it would republish that ride.
        var race = await SeedRaceAsync();
        // Re-loaded, because SeedRaceAsync clears the change tracker — mutating the detached instance
        // would save nothing and the assertion below would pass for the wrong reason.
        var tracked = await _db.Races.SingleAsync(r => r.RaceId == race.RaceId);
        tracked.CourseTrackId = new TrackId(_friendRideTrackId);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Null(await ReaderWithProjection().GetReplayAsync(GrantFor(race), default));
    }

    [Fact]
    public async Task Read_IsNull_WhenTheCourseIsTrashed()
    {
        // Lifecycle is restated in the reader precisely because it does not go through VisibleActivities.
        // Trashing the course must cut a live link off, exactly as it does for an activity share.
        var race = await SeedRaceAsync();
        var course = await _db.Activities.SingleAsync(a => a.TrackId == new TrackId(_courseTrackId));
        course.Lifecycle = LifecycleStates.Trashed;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Null(await ReaderWithProjection().GetReplayAsync(GrantFor(race), default));
    }

    [Fact]
    public async Task Read_IsNull_ForAGrantOwnedBySomeoneElse_OrOfAnotherKind()
    {
        var race = await SeedRaceAsync();
        var reader = ReaderWithProjection();

        // Ownership is re-asserted at read time: mint checked it once, and nothing else does.
        var stranger = new ShareGrant(1, ShareKind.Race, race.RaceId.Value, Friend, DateTime.UtcNow.AddDays(1));
        Assert.Null(await reader.GetReplayAsync(stranger, default));

        // And a grant of the wrong kind resolves nothing here even with the right numeric id.
        var wrongKind = new ShareGrant(1, ShareKind.Activity, race.RaceId.Value, Owner, DateTime.UtcNow.AddDays(1));
        Assert.Null(await reader.GetReplayAsync(wrongKind, default));
    }

    [Fact]
    public async Task Header_AndReplay_AgreeOnWhatIsServable()
    {
        // The page gates on GetHeaderAsync and the endpoint on GetReplayAsync. If those two predicates ever
        // drift, a share link either renders a shell whose content 404s or 404s a page whose content would
        // have loaded — so the agreement is the property, on both sides of every gate.
        var race = await SeedRaceAsync();
        var reader = ReaderWithProjection();
        var grant = GrantFor(race);

        Assert.NotNull(await reader.GetHeaderAsync(grant, default));
        Assert.NotNull(await reader.GetReplayAsync(grant, default));
        Assert.Equal("Black Forest ULTRA", (await reader.GetHeaderAsync(grant, default))!.RaceName);

        var course = await _db.Activities.SingleAsync(a => a.TrackId == new TrackId(_courseTrackId));
        course.Lifecycle = LifecycleStates.Trashed;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Null(await reader.GetHeaderAsync(grant, default));
        Assert.Null(await reader.GetReplayAsync(grant, default));
    }

    [Fact]
    public async Task Read_DropsARealRiderWhoseRideWasTrashed_WithoutFailingTheWholeReplay()
    {
        // Same rule as the course, applied per rider: an owner's trashed ride must not surface on a public
        // link — but the rest of the field still replays.
        var race = await SeedRaceAsync();
        var ride = await _db.Activities.SingleAsync(a => a.TrackId == new TrackId(_ownRideTrackId));
        ride.Lifecycle = LifecycleStates.Trashed;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var payload = await ReaderWithProjection().GetReplayAsync(GrantFor(race), default);

        Assert.NotNull(payload);
        Assert.DoesNotContain(payload!.Participants, p => p.Kind == RaceParticipantKind.Real);
        Assert.Equal(2, payload.Participants.Count);
    }

    // ---- fixture ----------------------------------------------------------------------------------

    private static ShareGrant GrantFor(Race race) =>
        new(TokenId: 1, ShareKind.Race, race.RaceId.Value, Owner, DateTime.UtcNow.AddDays(1));

    private ShareRaceReader ReaderWithProjection() => new(
        _db,
        FakeRaceCourseService.OfLength(1000),
        new FakeRealParticipantService(_friendRideTrackId),
        new NoCurveService(),
        new TrackContentStore(_db, new TrackObjectStore(_db, TimeProvider.System), TimeProvider.System));

    private async Task<int> AddActivityAsync(string userId, string name, string lifecycle)
    {
        var activity = new Activity
        {
            TrackName = name, UserId = userId, Visibility = "friends",
            Lifecycle = lifecycle, TotalDistance = 1000, ActivityType = "Ride",
        };
        _db.Activities.Add(activity);
        await _db.SaveChangesAsync();
        return activity.TrackId.Value;
    }

    /// <summary>
    /// A race whose field has every identity column filled on two imported finishers, plus the owner's own
    /// ride and a FRIEND's ride (which the owner legitimately added — racing a friend's ride is the
    /// feature, and dropping that friend from the SHARE is the rule under test).
    /// </summary>
    private async Task<Race> SeedRaceAsync()
    {
        var race = new Race
        {
            RaceName = "Black Forest ULTRA", CourseTrackId = new TrackId(_courseTrackId),
            UserId = Owner, Visibility = "friends",
        };
        _db.Races.Add(race);
        await _db.SaveChangesAsync();

        for (var seq = 0; seq < 2; seq++)
        {
            _db.RaceCheckpoints.Add(new RaceCheckpoint
            {
                RaceId = race.RaceId, Seq = seq,
                CheckpointName = seq == 0 ? "Start" : "Finish",
                ChainageMeters = seq * 1000.0,
            });
        }
        await _db.SaveChangesAsync();

        var checkpoints = await _db.RaceCheckpoints
            .Where(c => c.RaceId == race.RaceId).OrderBy(c => c.Seq).ToListAsync();

        var field = new List<RaceParticipant>
        {
            new()
            {
                RaceId = race.RaceId, Kind = RaceParticipantKind.Simulated,
                DisplayName = EntrantName, Bib = EntrantBib, Category = EntrantCategory,
                Gender = EntrantGender, Club = EntrantClub, OfficialFinishSeconds = 3600,
            },
            new()
            {
                RaceId = race.RaceId, Kind = RaceParticipantKind.Simulated,
                DisplayName = EntrantName + " II", Bib = EntrantBib + "-2", Category = EntrantCategory,
                Gender = EntrantGender, Club = EntrantClub, OfficialFinishSeconds = 4200,
            },
            new()
            {
                RaceId = race.RaceId, Kind = RaceParticipantKind.Real,
                DisplayName = "Owner", TrackId = new TrackId(_ownRideTrackId),
            },
            new()
            {
                RaceId = race.RaceId, Kind = RaceParticipantKind.Real,
                DisplayName = FriendName, TrackId = new TrackId(_friendRideTrackId),
            },
        };
        _db.RaceParticipants.AddRange(field);
        await _db.SaveChangesAsync();

        foreach (var participant in field)
        foreach (var checkpoint in checkpoints)
        {
            _db.RaceSplitTimes.Add(new RaceSplitTime
            {
                ParticipantId = participant.ParticipantId,
                CheckpointId = checkpoint.CheckpointId,
                ElapsedSeconds = checkpoint.Seq * 1800.0,
            });
        }
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return race;
    }

    private sealed class NoCurveService : IRaceCostCurveService
    {
        public Task<RaceCostCurve?> FitFromEffortAsync(
            TrackId effortTrackId, string? effortSha, RaceCourseGrid grid, TrackId courseTrackId,
            CancellationToken ct = default) =>
            Task.FromResult<RaceCostCurve?>(null);
    }

    /// <summary>
    /// Projects ANY track it is handed, stamping a latitude that says whose ride it was. Deliberately
    /// permissive: the reader must never hand it a foreign track in the FIRST place, so a friend's
    /// coordinate reaching the payload proves the filter failed rather than the projection.
    /// </summary>
    private sealed class FakeRealParticipantService(int friendTrackId) : IRaceRealParticipantService
    {
        public Task<RaceRealParticipantTrack?> ProjectAsync(
            TrackId trackId, RaceCourseGrid course, IReadOnlyList<RaceCheckpoint> checkpoints,
            ParticipantId participantId, int maxPoints = 0, CancellationToken ct = default)
        {
            var lat = trackId == new TrackId(friendTrackId) ? FriendLat : OwnerLat;
            return Task.FromResult<RaceRealParticipantTrack?>(new RaceRealParticipantTrack
            {
                Lat = [lat, lat],
                Lng = [8.1, 8.2],
                ElapsedSeconds = [0, 1800],
                ChainageMeters = [0, 1000],
                Splits = checkpoints.Select(c => new RaceSplitTime
                {
                    ParticipantId = participantId,
                    CheckpointId = c.CheckpointId,
                    ElapsedSeconds = c.Seq * 1800.0,
                }).ToList(),
            });
        }
    }
}
