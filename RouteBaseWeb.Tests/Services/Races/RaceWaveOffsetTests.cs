using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Races;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// Assigning one start offset per BLOCK — the no-code answer for a file that publishes only a label.
///
/// Most do: Schauinsland's blocks are called "Startblock 1: 10 Uhr bis 11 Uhr", which carries the start
/// time in prose and nothing a parser can use. The offsets a file DOES publish (an explicit column, or
/// gun − net) are read at import and never come through here.
/// </summary>
public class RaceWaveOffsetTests : IAsyncLifetime
{
    private const string Me = "me";

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;
    private Race _race = null!;
    // The course GRID, which is what every chainage is measured against — see FakeRaceCourseService.
    private readonly FakeRaceCourseService _courses = FakeRaceCourseService.OfLength(38910);

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = "Me" });
        await _context.SaveChangesAsync();

        var activity = new Activity
        {
            TrackName = "Course", UserId = Me, Visibility = "friends",
            Lifecycle = LifecycleStates.Released, TotalDistance = 38910, ActivityType = "Ride",
        };
        _context.Activities.Add(activity);
        await _context.SaveChangesAsync();

        var access = new RaceAccess(_context, new TestVisibilityService(Me));
        _race = (await new RaceCrud(_context, access, _courses, TimeProvider.System)
            .CreateRaceAsync(activity.TrackId, "Schauinsland", null, "friends"))!;
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private RaceParticipantCrud Crud(string userId = Me) =>
        new(_context, new RaceAccess(_context, new TestVisibilityService(userId)));

    private async Task SeedAsync(params (string Name, string? Wave)[] riders)
    {
        foreach (var (name, wave) in riders)
        {
            _context.RaceParticipants.Add(new RaceParticipant
            {
                RaceId = _race.RaceId, Kind = RaceParticipantKind.Simulated,
                DisplayName = name, Wave = wave,
            });
        }
        await _context.SaveChangesAsync();
    }

    private async Task<RaceParticipant> RiderAsync(string name) =>
        await _context.RaceParticipants.SingleAsync(p => p.DisplayName == name);

    [Fact]
    public async Task SetWaveOffsets_MovesEveryEntrantInTheBlock()
    {
        await SeedAsync(("A", "Block 1"), ("B", "Block 1"), ("C", "Block 2"));

        var (result, updated) = await Crud().SetWaveOffsetsAsync(
            _race.RaceId, new Dictionary<string, double?> { ["Block 1"] = 3600, ["Block 2"] = 7200 });

        Assert.Equal(RaceWriteResult.Ok, result);
        Assert.Equal(3, updated);
        Assert.Equal(3600, (await RiderAsync("A")).StartOffsetSeconds);
        Assert.Equal(3600, (await RiderAsync("B")).StartOffsetSeconds);
        Assert.Equal(7200, (await RiderAsync("C")).StartOffsetSeconds);
    }

    [Fact]
    public async Task SetWaveOffsets_ABlockNotInTheRequest_IsLeftAlone()
    {
        await SeedAsync(("A", "Block 1"), ("C", "Block 2"));
        await Crud().SetWaveOffsetsAsync(_race.RaceId, new Dictionary<string, double?> { ["Block 2"] = 7200 });

        var (_, updated) = await Crud().SetWaveOffsetsAsync(
            _race.RaceId, new Dictionary<string, double?> { ["Block 1"] = 3600 });

        Assert.Equal(1, updated);
        Assert.Equal(7200, (await RiderAsync("C")).StartOffsetSeconds);   // untouched
    }

    [Fact]
    public async Task SetWaveOffsets_ANullValue_ClearsTheBlockBackToTheGun()
    {
        await SeedAsync(("A", "Block 1"));
        await Crud().SetWaveOffsetsAsync(_race.RaceId, new Dictionary<string, double?> { ["Block 1"] = 3600 });

        await Crud().SetWaveOffsetsAsync(_race.RaceId, new Dictionary<string, double?> { ["Block 1"] = null });

        Assert.Null((await RiderAsync("A")).StartOffsetSeconds);
    }

    [Fact]
    public async Task SetWaveOffsets_MatchesTheLabelOrdinalIgnoreCase()
    {
        // The label is text the FILE wrote and the user then saw rendered. CurrentCulture equality would
        // change which riders a block contains under a Turkish or Thai server culture.
        await SeedAsync(("A", "Startblock 1"));

        var (_, updated) = await Crud().SetWaveOffsetsAsync(
            _race.RaceId, new Dictionary<string, double?> { ["STARTBLOCK 1"] = 3600 });

        Assert.Equal(1, updated);
        Assert.Equal(3600, (await RiderAsync("A")).StartOffsetSeconds);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-60)]
    public async Task SetWaveOffsets_AnImpossibleOffset_IsRefusedPerRider_NotForTheWholeRequest(double bad)
    {
        // One bad cell must not discard the blocks that were fine — and a NaN reaching the browser's clock
        // arithmetic would take the whole field with it.
        await SeedAsync(("A", "Block 1"), ("C", "Block 2"));

        var (_, updated) = await Crud().SetWaveOffsetsAsync(
            _race.RaceId, new Dictionary<string, double?> { ["Block 1"] = bad, ["Block 2"] = 7200 });

        Assert.Equal(1, updated);
        Assert.Null((await RiderAsync("A")).StartOffsetSeconds);
        Assert.Equal(7200, (await RiderAsync("C")).StartOffsetSeconds);
    }

    [Fact]
    public async Task SetWaveOffsets_ARiderWithNoWave_IsNeverTouched()
    {
        await SeedAsync(("A", "Block 1"), ("Solo", null));

        var (_, updated) = await Crud().SetWaveOffsetsAsync(
            _race.RaceId, new Dictionary<string, double?> { ["Block 1"] = 3600 });

        Assert.Equal(1, updated);
        Assert.Null((await RiderAsync("Solo")).StartOffsetSeconds);
    }

    [Fact]
    public async Task SetWaveOffsets_OnARaceIDoNotOwn_IsRefused()
    {
        await SeedAsync(("A", "Block 1"));

        var (result, updated) = await Crud("stranger").SetWaveOffsetsAsync(
            _race.RaceId, new Dictionary<string, double?> { ["Block 1"] = 3600 });

        Assert.Equal(RaceWriteResult.RaceNotFound, result);
        Assert.Equal(0, updated);
        Assert.Null((await RiderAsync("A")).StartOffsetSeconds);
    }
}
