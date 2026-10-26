using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Races;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// Importing a results CSV into a race's field. The rule worth pinning: a re-import replaces the
/// SIMULATED field but must never evict the real riders.
/// </summary>
public class RaceImportServiceTests : IAsyncLifetime
{
    private const string Me = "me";

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;
    // The course GRID, which is what every chainage is measured against — see FakeRaceCourseService.
    private readonly FakeRaceCourseService Courses = FakeRaceCourseService.OfLength(38910);
    private int _courseTrackId;
    private int _friendTrackId;
    private Race _race = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = "Me" });
        await _context.SaveChangesAsync();

        _courseTrackId = await SeedActivityAsync("Course");
        _friendTrackId = await SeedActivityAsync("A real rider");

        var access = new RaceAccess(_context, new TestVisibilityService(Me));
        _race = (await new RaceCrud(_context, access, Courses, TimeProvider.System).CreateRaceAsync(new TrackId(_courseTrackId), "Black Forest", null, "friends"))!;
        await new RaceParticipantCrud(_context, access).AddRealParticipantAsync(_race.RaceId, new TrackId(_friendTrackId), "Real Rider");
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private async Task<int> SeedActivityAsync(string name)
    {
        var a = new Activity
        {
            TrackName = name, UserId = Me, Visibility = "friends",
            Lifecycle = LifecycleStates.Released, TotalDistance = 38910, ActivityType = "Ride",
        };
        _context.Activities.Add(a);
        await _context.SaveChangesAsync();
        return a.TrackId.Value;
    }

    private RaceImportService Service() => new(
        _context, new RaceAccess(_context, new TestVisibilityService(Me)), NullLogger<RaceImportService>.Instance);

    private static RaceCsvMapping Mapping() => new()
    {
        NameColumn = "name", FinishColumn = "finish_time", BibColumn = "bib",
    };

    /// <summary>The import takes BYTES — the encoding is the server's call, not the browser's.</summary>
    private static byte[] Bytes(string csv) => Encoding.UTF8.GetBytes(csv);

    private const string Csv =
        "bib,name,finish_time\n"
        + "4496,KURZ Felix,\"1:16:59,4\"\n"
        + "4511,SCHLADEBACH Louis,\"1:20:25,2\"\n"
        + "4011,PECHACEK Luca,\"1:21:26,9\"\n";

    [Fact]
    public async Task Import_CreatesSimulatedParticipantsWithFinishSplits()
    {
        var result = await Service().ImportAsync(_race.RaceId, Bytes(Csv), Mapping());

        Assert.NotNull(result);
        Assert.Equal(3, result!.Participants.Count);
        Assert.Empty(result.Skipped);

        var simulated = await _context.RaceParticipants
            .Where(p => p.RaceId == _race.RaceId && p.Kind == RaceParticipantKind.Simulated)
            .ToListAsync();
        Assert.Equal(3, simulated.Count);

        // Every one of them carries a finish time, and it survived the round-trip through the DB.
        var finishId = _context.RaceCheckpoints
            .Where(c => c.RaceId == _race.RaceId).OrderByDescending(c => c.Seq).First().CheckpointId;
        var felix = simulated.Single(p => p.DisplayName == "KURZ Felix");
        var split = await _context.RaceSplitTimes.SingleAsync(
            s => s.ParticipantId == felix.ParticipantId && s.CheckpointId == finishId);
        Assert.Equal(4619.4, split.ElapsedSeconds, 3);
    }

    [Fact]
    public async Task Reimport_ReplacesTheSimulatedField_ButKeepsTheRealRiders()
    {
        // A corrected results file must not evict the humans from the race.
        await Service().ImportAsync(_race.RaceId, Bytes(Csv), Mapping());

        var corrected = "bib,name,finish_time\n4496,KURZ Felix,\"1:15:00,0\"\n";
        await Service().ImportAsync(_race.RaceId, Bytes(corrected), Mapping());

        var participants = await _context.RaceParticipants
            .Where(p => p.RaceId == _race.RaceId).ToListAsync();

        Assert.Single(participants, p => p.Kind == RaceParticipantKind.Real);
        Assert.Equal(new TrackId(_friendTrackId), participants.Single(p => p.Kind == RaceParticipantKind.Real).TrackId);

        var simulated = participants.Where(p => p.Kind == RaceParticipantKind.Simulated).ToList();
        Assert.Single(simulated);
        Assert.Equal("KURZ Felix", simulated[0].DisplayName);
    }

    [Fact]
    public async Task Reimport_LeavesNoOrphanSplitTimes()
    {
        await Service().ImportAsync(_race.RaceId, Bytes(Csv), Mapping());
        await Service().ImportAsync(_race.RaceId, Bytes("bib,name,finish_time\n4496,KURZ Felix,\"1:15:00,0\"\n"), Mapping());

        var liveIds = await _context.RaceParticipants.Select(p => p.ParticipantId).ToListAsync();
        var splitOwners = await _context.RaceSplitTimes.Select(s => s.ParticipantId).Distinct().ToListAsync();

        Assert.All(splitOwners, id => Assert.Contains(id, liveIds));
    }

    /// <summary>
    /// The destructive one. A re-import deletes the ENTIRE simulated field before writing the new one, so
    /// a file that yields nobody — one wrong column mapping, a delimiter we could not read — used to wipe
    /// the field and report success. The existing riders must survive their replacement failing.
    /// </summary>
    [Fact]
    public async Task ImportAsync_WithNoUsableRows_LeavesFieldIntact()
    {
        await Service().ImportAsync(_race.RaceId, Bytes(Csv), Mapping());
        var before = await _context.RaceSplitTimes.CountAsync();

        // Every row has a name and no time at all: usable rows, zero.
        var useless = "bib,name,finish_time\n4496,KURZ Felix,\n4511,SCHLADEBACH Louis,\n";

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Service().ImportAsync(_race.RaceId, Bytes(useless), Mapping()));
        Assert.Contains("left unchanged", ex.Message, StringComparison.Ordinal);

        var simulated = await _context.RaceParticipants
            .Where(p => p.RaceId == _race.RaceId && p.Kind == RaceParticipantKind.Simulated)
            .ToListAsync();
        Assert.Equal(3, simulated.Count);
        Assert.Equal(before, await _context.RaceSplitTimes.CountAsync());
    }

    [Fact]
    public async Task Import_IntoARaceIDoNotOwn_IsRefused()
    {
        var stranger = new RaceImportService(
            _context, new RaceAccess(_context, new TestVisibilityService("stranger")), NullLogger<RaceImportService>.Instance);

        var result = await stranger.ImportAsync(_race.RaceId, Bytes(Csv), Mapping());

        Assert.Null(result);
        Assert.Empty(await _context.RaceParticipants
            .Where(p => p.Kind == RaceParticipantKind.Simulated).ToListAsync());
    }
}
