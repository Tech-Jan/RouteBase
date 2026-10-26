using System.Text.Json;
using RouteBaseWeb.Services.Races;
using Xunit;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// The wire is the last line of defence, and it is a hard one: <see cref="System.Text.Json"/> REFUSES to
/// write a non-finite double, and it discovers this only when it reaches the offending number — i.e. after
/// the response body has already begun streaming. The client then sees HTTP 200 with a truncated body
/// (ERR_INCOMPLETE_CHUNKED_ENCODING) and `fetch` rejects with a bare "Failed to fetch": no status code to
/// read, no error page, nothing in the UI to point at.
///
/// This has now shipped three times in this slice — the DEM elevation holes, the cost-curve samples, and
/// (below) a results CSV carrying a non-finite time. So the rule is a test, not a comment: **every double
/// the race payload can carry must be provably finite before it reaches a DTO.**
/// </summary>
public class RaceWireContractTests
{
    private static readonly RaceId RaceId = new(1);
    private const int StartId = 10;
    private const int FinishId = 12;

    private static List<Models.RaceCheckpoint> Checkpoints() =>
    [
        new() { CheckpointId = new CheckpointId(StartId), RaceId = RaceId, Seq = 0, CheckpointName = "Start", ChainageMeters = 0 },
        new() { CheckpointId = new CheckpointId(FinishId), RaceId = RaceId, Seq = 1, CheckpointName = "Finish", ChainageMeters = 38910 },
    ];

    private static RaceCsvImportResult Import(string csv) =>
        RaceCsvImporter.Build(
            RaceCsvParser.Parse(csv),
            new RaceCsvMapping { NameColumn = "name", FinishColumn = "finish_time" },
            Checkpoints(),
            RaceId);

    /// <summary>
    /// The whole chain, end to end: one cell of a results file kills the entire replay response.
    ///
    /// "nan" is what a pandas-derived export writes for a missing split, and the column mapping is the
    /// user's to get wrong. The import reports SUCCESS and commits the row; every replay load from then on
    /// dies mid-stream. The race is bricked until somebody deletes the participant — and nothing in the UI
    /// says which one, or why.
    /// </summary>
    [Fact]
    public void ImportedNonFiniteTime_MustNotReachTheWire()
    {
        var csv = "name,finish_time\n"
                + "GOOD Rider,1:00:00\n"
                + "GHOST Rider,nan\n";       // ← one cell

        var imported = Import(csv);

        var splits = imported.Participants
            .SelectMany(p => p.Splits)
            .Select(s => new RaceSplitDto { CheckpointId = s.CheckpointId.Value, ElapsedSeconds = s.ElapsedSeconds })
            .ToList();

        // RED before the fix: ArgumentException — ".NET number values such as positive and negative
        // infinity cannot be written as valid JSON." Thrown mid-body, which is the whole problem.
        var thrown = Record.Exception(() => JsonSerializer.Serialize(splits));
        Assert.Null(thrown);
    }

    /// <summary>The published total travels on the payload too, and it comes from the same parser.</summary>
    [Fact]
    public void ImportedNonFiniteFinish_MustNotReachTheWire()
    {
        var imported = Import("name,finish_time\nGHOST Rider,nan\n");

        foreach (var p in imported.Participants)
        {
            var official = p.Participant.OfficialFinishSeconds;
            Assert.True(
                official is null || double.IsFinite(official.Value),
                $"OfficialFinishSeconds = {official} — System.Text.Json cannot write that, and it will "
                    + "take the whole replay response down with it.");
        }
    }

    /// <summary>
    /// Nothing a participant carries may be non-finite, whatever the file said. Stated over the split
    /// rows themselves so it holds for every import mode, not just the one the test above exercises.
    /// </summary>
    [Theory]
    [InlineData("nan")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e400")]
    public void ImportedSplits_AreAlwaysFinite(string cell)
    {
        var imported = Import($"name,finish_time\nGHOST Rider,{cell}\n");

        foreach (var split in imported.Participants.SelectMany(p => p.Splits))
        {
            Assert.True(
                double.IsFinite(split.ElapsedSeconds),
                $"cell '{cell}' became ElapsedSeconds = {split.ElapsedSeconds}");
        }
    }
}
