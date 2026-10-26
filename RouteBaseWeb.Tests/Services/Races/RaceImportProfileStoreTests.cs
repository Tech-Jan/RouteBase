using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Races;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// Saved column mappings, on the existing ProfileSettings KV store.
///
/// The property that justifies the whole feature: a profile is per USER and race-INDEPENDENT, so the
/// mapping worked out on this year's Datasport file applies to next year's race in one click. Which means
/// the interesting tests are the ones that replay a profile onto a DIFFERENT race.
/// </summary>
public class RaceImportProfileStoreTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Someone = "someone-else";

    private SqliteConnection _conn = null!;
    private RouteBaseDbContext _context = null!;
    private RaceImportProfileStore _store = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _context = new RouteBaseDbContext(
            new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options);
        await _context.Database.EnsureCreatedAsync();

        var settings = new ProfileSettingsService(
            _context, NullLogger<ProfileSettingsService>.Instance,
            new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
        _store = new RaceImportProfileStore(settings);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    /// <summary>This year's race: checkpoint ids 1–4.</summary>
    private static List<RaceCheckpoint> ThisYear() =>
    [
        new() { CheckpointId = new CheckpointId(1), Seq = 0, CheckpointName = "Start", ChainageMeters = 0 },
        new() { CheckpointId = new CheckpointId(2), Seq = 1, CheckpointName = "Hinterzarten", ChainageMeters = 12000 },
        new() { CheckpointId = new CheckpointId(3), Seq = 2, CheckpointName = "Raimartihof", ChainageMeters = 25000 },
        new() { CheckpointId = new CheckpointId(4), Seq = 3, CheckpointName = "Finish", ChainageMeters = 38910 },
    ];

    /// <summary>Next year: same course, same names, COMPLETELY different checkpoint ids.</summary>
    private static List<RaceCheckpoint> NextYear() =>
    [
        new() { CheckpointId = new CheckpointId(91), Seq = 0, CheckpointName = "Start", ChainageMeters = 0 },
        new() { CheckpointId = new CheckpointId(92), Seq = 1, CheckpointName = "Hinterzarten", ChainageMeters = 12000 },
        new() { CheckpointId = new CheckpointId(93), Seq = 2, CheckpointName = "Raimartihof", ChainageMeters = 25000 },
        new() { CheckpointId = new CheckpointId(94), Seq = 3, CheckpointName = "Finish", ChainageMeters = 38910 },
    ];

    private static RaceCsvMapping Datasport() => new()
    {
        NameColumn = "name",
        FinishColumn = "finish",
        BibColumn = "bib",
        CategoryColumn = "category",
        StatusColumn = "status",
        SplitColumns = new Dictionary<int, string> { [2] = "cp_hinterzarten", [3] = "cp_raimartihof" },
    };

    [Fact]
    public async Task Save_ThenList_RoundTripsEveryColumn()
    {
        await _store.SaveAsync(Me, "Datasport", Datasport(), ThisYear());

        var profile = Assert.Single(await _store.ListAsync(Me, ThisYear()));

        Assert.Equal("Datasport", profile.Name);
        Assert.Equal("name", profile.NameColumn);
        Assert.Equal("finish", profile.FinishColumn);
        Assert.Equal("bib", profile.BibColumn);
        Assert.Equal("category", profile.CategoryColumn);
        Assert.Equal("status", profile.StatusColumn);
        Assert.Null(profile.GenderColumn);
        Assert.Equal(
            new Dictionary<int, string> { [2] = "cp_hinterzarten", [3] = "cp_raimartihof" },
            profile.SplitColumns);
    }

    /// <summary>
    /// THE test. Split columns are stored by checkpoint NAME and SEQ, never by checkpoint_ID — an id
    /// belongs to one race and would pin the profile to it forever, which is the opposite of the point.
    /// </summary>
    [Fact]
    public async Task AProfile_ReplaysOntoADifferentRace_WithDifferentCheckpointIds()
    {
        await _store.SaveAsync(Me, "Datasport", Datasport(), ThisYear());

        var profile = Assert.Single(await _store.ListAsync(Me, NextYear()));

        Assert.Equal(
            new Dictionary<int, string> { [92] = "cp_hinterzarten", [93] = "cp_raimartihof" },
            profile.SplitColumns);
    }

    [Fact]
    public async Task ARenamedCheckpoint_StillResolvesBySeq()
    {
        await _store.SaveAsync(Me, "Datasport", Datasport(), ThisYear());

        var renamed = NextYear();
        renamed[1].CheckpointName = "Hinterzarten (neu)";

        var profile = Assert.Single(await _store.ListAsync(Me, renamed));

        Assert.Equal("cp_hinterzarten", profile.SplitColumns[92]);
    }

    [Fact]
    public async Task ACheckpointTheNewRaceDoesNotHave_IsSimplyAbsent_NeverMappedOntoAnother()
    {
        // Mapping one column onto a checkpoint the profile did not mean is a composite-PK collision at
        // import — it 500s the whole thing — so an unmatched split must drop, not slide.
        await _store.SaveAsync(Me, "Datasport", Datasport(), ThisYear());

        List<RaceCheckpoint> shorter =
        [
            new() { CheckpointId = new CheckpointId(91), Seq = 0, CheckpointName = "Start", ChainageMeters = 0 },
            new() { CheckpointId = new CheckpointId(94), Seq = 1, CheckpointName = "Finish", ChainageMeters = 38910 },
        ];

        var profile = Assert.Single(await _store.ListAsync(Me, shorter));

        // Seq 1 is now the Finish, and only ONE of the two saved splits can claim it.
        Assert.Single(profile.SplitColumns);
        Assert.Equal(["cp_hinterzarten"], profile.SplitColumns.Values);
    }

    [Fact]
    public async Task TwoCheckpointsSharingAName_AreClaimedOnce_Each()
    {
        var duplicated = ThisYear();
        duplicated[1].CheckpointName = "Feed zone";
        duplicated[2].CheckpointName = "Feed zone";

        await _store.SaveAsync(Me, "Dup", Datasport(), duplicated);

        var profile = Assert.Single(await _store.ListAsync(Me, duplicated));

        // Both mapped, and to DIFFERENT checkpoints — a name match must not claim the same row twice.
        Assert.Equal(2, profile.SplitColumns.Count);
        Assert.Equal([2, 3], profile.SplitColumns.Keys.Order());
    }

    // ── ownership and lifecycle ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Profiles_ArePerUser()
    {
        await _store.SaveAsync(Me, "Datasport", Datasport(), ThisYear());

        Assert.Empty(await _store.ListAsync(Someone, ThisYear()));
        Assert.Single(await _store.ListAsync(Me, ThisYear()));
    }

    [Fact]
    public async Task Save_UnderAnExistingName_Replaces_RatherThanDuplicating()
    {
        await _store.SaveAsync(Me, "Datasport", Datasport(), ThisYear());
        await _store.SaveAsync(Me, "Datasport", Datasport() with { BibColumn = "startnr" }, ThisYear());

        var profile = Assert.Single(await _store.ListAsync(Me, ThisYear()));
        Assert.Equal("startnr", profile.BibColumn);
    }

    [Fact]
    public async Task Delete_RemovesIt_AndSaysWhetherThereWasAnything()
    {
        await _store.SaveAsync(Me, "Datasport", Datasport(), ThisYear());

        Assert.True(await _store.DeleteAsync(Me, "Datasport"));
        Assert.Empty(await _store.ListAsync(Me, ThisYear()));
        Assert.False(await _store.DeleteAsync(Me, "Datasport"));
    }

    [Fact]
    public async Task Delete_CannotReachAnotherUsersProfile()
    {
        await _store.SaveAsync(Me, "Datasport", Datasport(), ThisYear());

        Assert.False(await _store.DeleteAsync(Someone, "Datasport"));
        Assert.Single(await _store.ListAsync(Me, ThisYear()));
    }

    [Fact]
    public async Task List_SkipsARowItCannotRead_RatherThanFailingTheWholePanel()
    {
        await _store.SaveAsync(Me, "Good", Datasport(), ThisYear());
        _context.ProfileSettings.Add(new ProfileSettings
        {
            Category = SettingCategories.RaceImport,
            SettingKey = RaceImportProfileStore.SettingKeyFor("Corrupt"),
            SettingValue = "{ not json",
            UserId = Me,
        });
        await _context.SaveChangesAsync();

        var profiles = await _store.ListAsync(Me, ThisYear());

        Assert.Single(profiles);
        Assert.Equal("Good", profiles[0].Name);
    }

    [Fact]
    public async Task List_IgnoresOtherKeysInTheSameCategory()
    {
        // ProfileSettings is a shared store; a future feature filing something under RaceImport must not
        // turn up in the mapping picker.
        _context.ProfileSettings.Add(new ProfileSettings
        {
            Category = SettingCategories.RaceImport, SettingKey = "SomethingElse",
            SettingValue = "42", UserId = Me,
        });
        await _context.SaveChangesAsync();

        Assert.Empty(await _store.ListAsync(Me, ThisYear()));
    }

    /// <summary>
    /// A ~10-checkpoint mapping serialises to 400–700 characters, which is why SettingValue went from 500
    /// to 2000. Ski to Sea's eight checkpoints with long column names is the realistic worst case.
    /// </summary>
    [Fact]
    public async Task ALargeMapping_FitsInTheStoredValue()
    {
        var checkpoints = Enumerable.Range(0, 10)
            .Select(i => new RaceCheckpoint
            {
                CheckpointId = new CheckpointId(i + 1), Seq = i,
                CheckpointName = $"Checkpoint with a fairly long name {i}", ChainageMeters = i * 1000,
            }).ToList();
        var mapping = Datasport() with
        {
            SplitColumns = checkpoints.Skip(1)
                .ToDictionary(c => c.CheckpointId.Value, c => $"cp_column_with_a_long_name_{c.Seq}"),
        };

        await _store.SaveAsync(Me, "Ski to Sea", mapping, checkpoints);

        var stored = await _context.ProfileSettings.SingleAsync(s => s.Category == SettingCategories.RaceImport);
        Assert.InRange(stored.SettingValue.Length, 501, 2000);
        Assert.Equal(9, (await _store.ListAsync(Me, checkpoints))[0].SplitColumns.Count);
    }

    // ── the name is user input ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Datasport", "Datasport")]
    [InlineData("  Datasport  ", "Datasport")]
    public void NormalizeName_TrimsAndAccepts(string input, string expected)
    {
        Assert.Equal(expected, RaceImportProfileStore.NormalizeName(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // A colon is the key separator: `Mapping:a:b` would parse back as the name `a:b`, which round-trips
    // by luck rather than by design — and a name that collides with the prefix would be worse.
    [InlineData("has:colon")]
    public void NormalizeName_RejectsWhatCannotBeAKey(string? input)
    {
        Assert.Null(RaceImportProfileStore.NormalizeName(input));
    }

    [Fact]
    public void NormalizeName_RejectsSomethingTooLongToBeARetypedName()
    {
        Assert.Null(RaceImportProfileStore.NormalizeName(new string('x', 61)));
        Assert.NotNull(RaceImportProfileStore.NormalizeName(new string('x', 60)));
    }
}
