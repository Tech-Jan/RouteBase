using System.Text.Json;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Races;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// Assembling the replay payload — the split-joining half, which the field size scales.
///
/// The course/curve/projection collaborators are faked: their own suites cover the geometry, and what
/// this fixture is for is the part that used to be O(P·S) with a participant-id IN list wide enough to
/// break SQLite outright.
/// </summary>
public class RaceReplayServiceTests : IAsyncLifetime
{
    private const string Me = "me";

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;
    private int _courseTrackId;

    private sealed class NoCurveService : IRaceCostCurveService
    {
        public Task<RaceCostCurve?> FitFromEffortAsync(
            TrackId effortTrackId, string? effortSha, RaceCourseGrid grid, TrackId courseTrackId,
            CancellationToken ct = default) =>
            Task.FromResult<RaceCostCurve?>(null);
    }

    /// <summary>No real riders in this fixture — the simulated field is what the split join scales with.</summary>
    private sealed class NoRealRiderService : IRaceRealParticipantService
    {
        public Task<RaceRealParticipantTrack?> ProjectAsync(
            TrackId trackId, RaceCourseGrid course, IReadOnlyList<RaceCheckpoint> checkpoints,
            ParticipantId participantId, int maxPoints = 0, CancellationToken ct = default) =>
            Task.FromResult<RaceRealParticipantTrack?>(null);
    }

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        // The replay now resolves the reference effort's content sha (half the cost-curve cache key), and
        // the content tables are patched in rather than modelled by EF — so EnsureCreated alone leaves no
        // Activity_Version for it to read.
        await new DbSchemaManager(_context).EnsureTrackContentTablesAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = "Me" });
        await _context.SaveChangesAsync();

        var course = new Activity
        {
            TrackName = "Course", UserId = Me, Visibility = "friends",
            Lifecycle = LifecycleStates.Released, TotalDistance = 1000, ActivityType = "Ride",
        };
        _context.Activities.Add(course);
        await _context.SaveChangesAsync();
        _courseTrackId = course.TrackId.Value;
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private RaceReplayService ReplayAs(string userId)
    {
        var visibility = new VisibilityService(
            new TestCurrentUserService(userId),
            new FriendshipService(_context, null!, null!, NullLogger<FriendshipService>.Instance, TimeProvider.System),
            _context);
        return new RaceReplayService(
            _context, new RaceAccess(_context, visibility), FakeRaceCourseService.OfLength(1000),
            new NoRealRiderService(), new NoCurveService(),
            // The effort's content sha is resolved here now (it is half the curve's cache key). This
            // fixture fakes the curve service outright, so the store is only ever asked for a sha it
            // will not find — a real one over the same context answers null and that is correct.
            new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System));
    }

    /// <summary>A race with <paramref name="checkpointCount"/> checkpoints (Start … Finish).</summary>
    private async Task<Race> SeedRaceAsync(int checkpointCount = 2)
    {
        var race = new Race
        {
            RaceName = "Black Forest ULTRA", CourseTrackId = new TrackId(_courseTrackId),
            UserId = Me, Visibility = "friends",
        };
        _context.Races.Add(race);
        await _context.SaveChangesAsync();

        for (var seq = 0; seq < checkpointCount; seq++)
        {
            _context.RaceCheckpoints.Add(new RaceCheckpoint
            {
                RaceId = race.RaceId,
                Seq = seq,
                CheckpointName = $"CP{seq}",
                ChainageMeters = seq * 1000.0 / Math.Max(1, checkpointCount - 1),
            });
        }
        await _context.SaveChangesAsync();
        return race;
    }

    private async Task<RaceParticipant> AddSimulatedAsync(Race race, string name)
    {
        var participant = new RaceParticipant
        {
            RaceId = race.RaceId, Kind = RaceParticipantKind.Simulated, DisplayName = name,
        };
        _context.RaceParticipants.Add(participant);
        await _context.SaveChangesAsync();
        return participant;
    }

    /// <summary>
    /// AUTHZ-P1: the course id stored on a race is a pointer, not a grant — the same rule the reference
    /// effort already followed. A race the caller OWNS (so `VisibleRaces` always passes) may still name a
    /// course belonging to someone else: DuplicateRaceAsync used to mint exactly that shape, and the
    /// replay then shipped that activity's full geometry to a user with no rights to it. The mint path is
    /// closed too, but this is the gate that also covers rows created before the fix.
    /// </summary>
    [Fact]
    public async Task GetReplayAsync_RefusesARaceWhoseCourseTheCallerCannotSee()
    {
        var stranger = new AppUser { UserId = "stranger", DisplayName = "Stranger" };
        _context.Users.Add(stranger);
        var privateRide = new Activity
        {
            TrackName = "Stranger's private ride", UserId = "stranger", Visibility = "private",
            Lifecycle = LifecycleStates.Released, TotalDistance = 1000, ActivityType = "Ride",
        };
        _context.Activities.Add(privateRide);
        await _context.SaveChangesAsync();

        // The shape the old DuplicateRaceAsync produced: owned by me, course owned by the stranger.
        var race = await SeedRaceAsync(checkpointCount: 2);
        race.CourseTrackId = privateRide.TrackId;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        Assert.Null(await ReplayAs(Me).GetReplayAsync(race.RaceId));
    }

    /// <summary>The same read still works when the course is the caller's own — the gate is not a blanket refusal.</summary>
    [Fact]
    public async Task GetReplayAsync_AllowsARaceOverTheCallersOwnCourse()
    {
        var race = await SeedRaceAsync(checkpointCount: 2);
        await AddSimulatedAsync(race, "Rider");
        _context.ChangeTracker.Clear();

        Assert.NotNull(await ReplayAs(Me).GetReplayAsync(race.RaceId));
    }

    [Fact]
    public async Task GetReplayAsync_OrdersSplitsByCheckpointSeq()
    {
        var race = await SeedRaceAsync(checkpointCount: 4);
        var rider = await AddSimulatedAsync(race, "Rider");
        var checkpoints = await _context.RaceCheckpoints
            .Where(c => c.RaceId == race.RaceId).OrderBy(c => c.Seq).ToListAsync();

        // Inserted in a deliberately scrambled order — the payload must still come out in seq order.
        foreach (var seq in new[] { 2, 0, 3, 1 })
        {
            _context.RaceSplitTimes.Add(new RaceSplitTime
            {
                ParticipantId = rider.ParticipantId,
                CheckpointId = checkpoints[seq].CheckpointId,
                ElapsedSeconds = seq * 600,
            });
        }
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var payload = await ReplayAs(Me).GetReplayAsync(race.RaceId);

        var splits = payload!.Participants.Single().Splits;
        Assert.Equal(new[] { 0.0, 600.0, 1200.0, 1800.0 }, splits.Select(s => s.ElapsedSeconds));
        Assert.Equal(
            checkpoints.Select(c => c.CheckpointId.Value),
            splits.Select(s => s.CheckpointId));
    }

    [Fact]
    public async Task GetReplayAsync_DropsSplitsForDeletedCheckpoints()
    {
        var race = await SeedRaceAsync(checkpointCount: 3);
        var rider = await AddSimulatedAsync(race, "Rider");
        var checkpoints = await _context.RaceCheckpoints
            .Where(c => c.RaceId == race.RaceId).OrderBy(c => c.Seq).ToListAsync();

        foreach (var checkpoint in checkpoints)
        {
            _context.RaceSplitTimes.Add(new RaceSplitTime
            {
                ParticipantId = rider.ParticipantId,
                CheckpointId = checkpoint.CheckpointId,
                ElapsedSeconds = checkpoint.Seq * 600,
            });
        }
        await _context.SaveChangesAsync();

        // Orphan the middle split by deleting its checkpoint out from under it (raw SQL, so EF's cascade
        // does not tidy the split row away for us — which is exactly the state the payload must survive).
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM Race_Checkpoint WHERE checkpoint_ID = {0}", checkpoints[1].CheckpointId.Value);
        _context.ChangeTracker.Clear();

        var payload = await ReplayAs(Me).GetReplayAsync(race.RaceId);

        var splits = payload!.Participants.Single().Splits;
        Assert.Equal(new[] { 0.0, 1200.0 }, splits.Select(s => s.ElapsedSeconds));
    }

    [Fact]
    public async Task GetReplayAsync_GivesEachParticipantOnlyItsOwnSplits()
    {
        var race = await SeedRaceAsync(checkpointCount: 2);
        var checkpoints = await _context.RaceCheckpoints
            .Where(c => c.RaceId == race.RaceId).OrderBy(c => c.Seq).ToListAsync();

        for (var rider = 0; rider < 3; rider++)
        {
            var participant = await AddSimulatedAsync(race, $"Rider {rider}");
            foreach (var checkpoint in checkpoints)
            {
                _context.RaceSplitTimes.Add(new RaceSplitTime
                {
                    ParticipantId = participant.ParticipantId,
                    CheckpointId = checkpoint.CheckpointId,
                    // Unique per (rider, checkpoint): a cross-assignment cannot hide behind equal values.
                    ElapsedSeconds = rider * 10_000 + checkpoint.Seq,
                });
            }
        }
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var payload = await ReplayAs(Me).GetReplayAsync(race.RaceId);

        var participants = payload!.Participants.OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList();
        Assert.Equal(3, participants.Count);
        for (var rider = 0; rider < 3; rider++)
        {
            Assert.Equal(
                new[] { rider * 10_000.0, rider * 10_000.0 + 1 },
                participants[rider].Splits.Select(s => s.ElapsedSeconds));
        }
    }

    [Fact]
    public async Task GetReplayAsync_Returns1200Participants()
    {
        // The old query built `WHERE participant_id IN (…)` from EVERY participant id. SQLite's default
        // parameter ceiling is 999 — a 1,000-finisher marathon CSV is an ordinary input for this feature,
        // and it did not return a payload, it THREW. Filtering by race instead is one parameter.
        var race = await SeedRaceAsync(checkpointCount: 2);
        var checkpoints = await _context.RaceCheckpoints
            .Where(c => c.RaceId == race.RaceId).OrderBy(c => c.Seq).ToListAsync();

        var field = new List<RaceParticipant>(1200);
        for (var i = 0; i < 1200; i++)
        {
            field.Add(new RaceParticipant
            {
                RaceId = race.RaceId, Kind = RaceParticipantKind.Simulated, DisplayName = $"Finisher {i}",
            });
        }
        _context.RaceParticipants.AddRange(field);
        await _context.SaveChangesAsync();

        foreach (var participant in field)
        {
            _context.RaceSplitTimes.Add(new RaceSplitTime
            {
                ParticipantId = participant.ParticipantId,
                CheckpointId = checkpoints[1].CheckpointId,
                ElapsedSeconds = 7200 + participant.ParticipantId.Value,
            });
        }
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var payload = await ReplayAs(Me).GetReplayAsync(race.RaceId);

        Assert.NotNull(payload);
        Assert.Equal(1200, payload!.Participants.Count);
        Assert.All(payload.Participants, p => Assert.Single(p.Splits));
    }

    /// <summary>
    /// A race BRICKED by an earlier import must come back to life.
    ///
    /// The poison here is ±∞, not NaN, and the difference is the whole mechanism. Microsoft.Data.Sqlite
    /// REFUSES a NaN outright ("Cannot store 'NaN' values"), so a NaN can never reach the table — it kills
    /// the import instead. **Infinity it stores happily**, and reads back intact. So an "Infinity" or an
    /// overflowing "1e400" in a results file imported SUCCESSFULLY, sat in the table, and then killed every
    /// replay request from that moment on: System.Text.Json will not write an ∞ either, and it throws
    /// mid-body — HTTP 200 with a truncated body, which the browser reports as a bare "Failed to fetch".
    ///
    /// RaceCsvParser can no longer produce one, but rows written before that fix are still there. So the
    /// replay service is the last gate: a non-finite split is dropped on the way out and the race serves —
    /// with no migration, and without the good riders paying for the bad row.
    /// </summary>
    [Fact]
    public async Task GetReplayAsync_DropsAStoredNonFiniteSplit_SoTheResponseStillSerializes()
    {
        var race = await SeedRaceAsync(checkpointCount: 3);
        var checkpoints = await _context.RaceCheckpoints
            .Where(c => c.RaceId == race.RaceId).OrderBy(c => c.Seq).ToListAsync();

        var good = await AddSimulatedAsync(race, "GOOD Rider");
        var ghost = await AddSimulatedAsync(race, "GHOST Rider");

        // Exactly what a pre-fix import of an "Infinity" / "1e400" cell left behind.
        ghost.OfficialFinishSeconds = double.PositiveInfinity;
        _context.RaceSplitTimes.AddRange(
            new RaceSplitTime
            {
                ParticipantId = good.ParticipantId,
                CheckpointId = checkpoints[^1].CheckpointId,
                ElapsedSeconds = 3600,
            },
            new RaceSplitTime
            {
                ParticipantId = ghost.ParticipantId,
                CheckpointId = checkpoints[^1].CheckpointId,
                ElapsedSeconds = double.PositiveInfinity,
            });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var payload = await ReplayAs(Me).GetReplayAsync(race.RaceId);

        Assert.NotNull(payload);

        // The whole point: this is the call that used to blow up mid-stream.
        var thrown = Record.Exception(() => JsonSerializer.Serialize(payload));
        Assert.Null(thrown);

        // The bad row is gone; the good rider is untouched.
        Assert.All(
            payload!.Participants.SelectMany(p => p.Splits),
            s => Assert.True(double.IsFinite(s.ElapsedSeconds)));
        Assert.All(
            payload.Participants,
            p => Assert.True(p.OfficialFinishSeconds is null || double.IsFinite(p.OfficialFinishSeconds.Value)));
        Assert.Equal(3600, Assert.Single(payload.Participants.Single(p => p.Name == "GOOD Rider").Splits).ElapsedSeconds);
    }
}
