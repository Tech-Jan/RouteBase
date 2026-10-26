using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Races;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// The race-aware template. Its job is that adding a race stops meaning writing code: every race so far
/// needed a bespoke Python puller to reshape a published results file into something the importer happened
/// to accept, and a template the user fills in inverts that.
///
/// The strongest property is the round trip — a template, filled in, must import. That is asserted here
/// against the real parser and the real importer rather than against a string.
/// </summary>
public class RaceCsvTemplateTests
{
    private static readonly RaceId RaceId = new(1);

    private static List<RaceCheckpoint> Checkpoints(params (int Id, int Seq, string Name)[] rows) =>
        rows.Select(r => new RaceCheckpoint
        {
            CheckpointId = new CheckpointId(r.Id), RaceId = RaceId, Seq = r.Seq,
            CheckpointName = r.Name, ChainageMeters = r.Seq * 1000,
        }).ToList();

    private static List<RaceCheckpoint> TwoCheckpoints() =>
        Checkpoints((1, 0, "Start"), (2, 1, "Finish"));

    private static List<RaceCheckpoint> BlackForest() =>
        Checkpoints((1, 0, "Start"), (2, 1, "Hinterzarten"), (3, 2, "Raimartihof"), (4, 3, "Finish"));

    [Fact]
    public void Build_HeadersAreTheCanonicalAliases_SoATemplateFileNeedsNoMappingAtAll()
    {
        var table = RaceCsvParser.Parse(RaceCsvTemplate.Build(TwoCheckpoints()));

        foreach (var field in new[]
                 {
                     RaceCsvFields.Name, RaceCsvFields.Bib, RaceCsvFields.Category,
                     RaceCsvFields.Gender, RaceCsvFields.Club, RaceCsvFields.Status, RaceCsvFields.Finish,
                 })
        {
            Assert.NotNull(RaceCsvVocabulary.Guess(table.Header, field));
        }
    }

    [Fact]
    public void Build_OneColumnPerCheckpointBetweenTheGunAndTheLine_InSeqOrder()
    {
        // The start needs none (everyone leaves at 0) and the finish has its own column.
        var table = RaceCsvParser.Parse(RaceCsvTemplate.Build(BlackForest()));

        Assert.Equal(
            ["bib", "name", "category", "gender", "club", "status", "Hinterzarten", "Raimartihof", "finish"],
            table.Header);
    }

    [Fact]
    public void Build_ATwoCheckpointRace_HasNoSplitColumns()
    {
        var table = RaceCsvParser.Parse(RaceCsvTemplate.Build(TwoCheckpoints()));

        Assert.Equal(["bib", "name", "category", "gender", "club", "status", "finish"], table.Header);
    }

    /// <summary>Ski to Sea: eight checkpoints, seven legs, six intermediate split columns.</summary>
    [Fact]
    public void Build_AnEightCheckpointRace_GetsSixSplitColumns()
    {
        var checkpoints = Checkpoints(
            (1, 0, "Start"), (2, 1, "xcski"), (3, 2, "dhski"), (4, 3, "run"),
            (5, 4, "roadbike"), (6, 5, "canoe"), (7, 6, "mtb"), (8, 7, "Finish"));

        var table = RaceCsvParser.Parse(RaceCsvTemplate.Build(checkpoints));

        Assert.Equal(["xcski", "dhski", "run", "roadbike", "canoe", "mtb"], table.Header.Skip(6).SkipLast(1));
    }

    // ── the example rows, which are the part that teaches ──────────────────────────────────────────

    [Fact]
    public void Build_TheExampleRows_RoundTripThroughTheRealImporter()
    {
        // THE property. A template the importer cannot read would be worse than none: it would look like
        // the app's own file format and still fail.
        var checkpoints = BlackForest();
        var table = RaceCsvParser.Parse(RaceCsvTemplate.Build(checkpoints));

        var result = RaceCsvImporter.Build(
            table,
            new RaceCsvMapping
            {
                NameColumn = "name", FinishColumn = "finish", BibColumn = "bib",
                CategoryColumn = "category", GenderColumn = "gender", ClubColumn = "club",
                StatusColumn = "status",
                SplitColumns = new Dictionary<int, string> { [2] = "Hinterzarten", [3] = "Raimartihof" },
            },
            checkpoints, RaceId);

        // All three rows survive — including the one with no time at all, which is the shape that used to
        // be dropped silently.
        Assert.Equal(3, result.Participants.Count);
        Assert.Empty(result.Skipped);
        Assert.Equal(1, result.FinisherCount);
    }

    [Fact]
    public void Build_DemonstratesAFinisher_ADnf_AndANonStarter()
    {
        var checkpoints = BlackForest();
        var table = RaceCsvParser.Parse(RaceCsvTemplate.Build(checkpoints));

        var statusIdx = table.Header.ToList().IndexOf("status");
        var statuses = table.Rows.Select(r => r[statusIdx]).ToList();

        Assert.Equal(["Finisher", "DNF", "DNS"], statuses);
        Assert.All(statuses, s => Assert.NotNull(RaceFinishStatusVocabulary.Normalize(s)));
    }

    [Fact]
    public void Build_TheFinishersSplitsIncreaseAlongTheCourse_ShowingTheyAreElapsedFromTheStart()
    {
        // The single most common way an import produces 424 riders with no times is a misunderstood time
        // format; the second is cumulative-vs-per-section. The example row settles both by example.
        var table = RaceCsvParser.Parse(RaceCsvTemplate.Build(BlackForest()));
        var finisher = table.Rows[0];

        var times = new[] { finisher[6], finisher[7], finisher[8] }
            .Select(c => RaceCsvParser.ParseElapsedSeconds(c)!.Value)
            .ToList();

        Assert.True(times[0] < times[1], $"{times[0]} !< {times[1]}");
        Assert.True(times[1] < times[2], $"{times[1]} !< {times[2]}");
    }

    [Fact]
    public void Build_TheDnfRow_HasSplitsAndNoFinish()
    {
        var table = RaceCsvParser.Parse(RaceCsvTemplate.Build(BlackForest()));
        var dnf = table.Rows[1];

        Assert.NotEmpty(dnf[6]);                       // he reached the first checkpoint
        Assert.Equal(string.Empty, dnf[8]);            // ...and never the line
    }

    // ── hostile checkpoint names ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ACheckpointNameWithADelimiter_CannotShiftTheColumns()
    {
        // Checkpoint names are free text. A comma in a header would move every column after it out from
        // under its data, which is a corruption the user would have no way to see.
        var checkpoints = Checkpoints((1, 0, "Start"), (2, 1, "Feed, zone"), (3, 2, "Finish"));

        var table = RaceCsvParser.Parse(RaceCsvTemplate.Build(checkpoints));

        Assert.Equal(8, table.Header.Count);
        Assert.All(table.Rows, r => Assert.Equal(8, r.Count));
        Assert.Equal("Feed zone", table.Header[6]);
    }

    [Fact]
    public void Build_TwoCheckpointsWithTheSameName_GetDistinctColumns()
    {
        // A duplicate header resolves to the FIRST column, so two "Feed zone" checkpoints would silently
        // share one column — and the second would import the first's times.
        var checkpoints = Checkpoints((1, 0, "Start"), (2, 1, "Feed zone"), (3, 2, "Feed zone"), (4, 3, "Finish"));

        var header = RaceCsvParser.Parse(RaceCsvTemplate.Build(checkpoints)).Header;

        Assert.Equal(header.Count, header.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["Feed zone", "Feed zone 2"], header.Skip(6).SkipLast(1));
    }

    [Fact]
    public void Build_AnUnnamedCheckpoint_StillGetsAnAddressableColumn()
    {
        var checkpoints = Checkpoints((1, 0, "Start"), (2, 1, ""), (3, 2, "Finish"));

        Assert.Equal("cp1", RaceCsvParser.Parse(RaceCsvTemplate.Build(checkpoints)).Header[6]);
    }
}
