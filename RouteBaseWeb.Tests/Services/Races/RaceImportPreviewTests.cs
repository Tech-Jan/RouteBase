using System.Text;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Races;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// The preview — what the import WOULD do, before anything is written.
///
/// It goes through <see cref="RaceCsvImporter.Build"/> rather than re-deriving anything, and these tests
/// pin the consequences of that: the numbers shown are the numbers that will be stored, and the guesses
/// come from the one alias table rather than from a second copy in the browser.
/// </summary>
public class RaceImportPreviewTests
{
    private static readonly RaceId RaceId = new(1);
    private const int StartId = 10;
    private const int MidId = 11;
    private const int FinishId = 12;

    private static List<RaceCheckpoint> Checkpoints() =>
    [
        new() { CheckpointId = new CheckpointId(StartId), RaceId = RaceId, Seq = 0, CheckpointName = "Start", ChainageMeters = 0, LegName = "xcski" },
        new() { CheckpointId = new CheckpointId(MidId), RaceId = RaceId, Seq = 1, CheckpointName = "Raimartihof", ChainageMeters = 21000, LegName = "dhski" },
        new() { CheckpointId = new CheckpointId(FinishId), RaceId = RaceId, Seq = 2, CheckpointName = "Finish", ChainageMeters = 38910 },
    ];

    private static RaceImportPreviewDto Preview(string csv, RaceCsvMapping? mapping = null) =>
        RaceImportPreview.Build(
            RaceCsvDecoder.Decode(Encoding.UTF8.GetBytes(csv)), mapping, Checkpoints(), RaceId);

    private const string BlackForest =
        "rank,bib,name,status,finish,cp_raimartihof\n"
        + "1,4496,KURZ Felix,Finisher,2:43:18.2,1:32:18.2\n"
        + "2,4511,SCHLADEBACH Louis,Finisher,2:50:25.2,1:35:11.0\n";

    // ── what the server detected ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ReportsWhatItDetected_SoAMisreadFileLooksMisread()
    {
        // "1 column found" with no explanation is what made a semicolon export look like a broken file.
        var preview = Preview("bib;name;finish\n1;A;1:00:00\n2;B;2:00:00\n");

        Assert.Equal(";", preview.Delimiter);
        Assert.Equal(RaceCsvDecoder.Utf8, preview.Encoding);
        Assert.Equal(["bib", "name", "finish"], preview.Header);
        Assert.Equal(2, preview.RowCount);
    }

    [Fact]
    public void Build_GuessesEveryCanonicalFieldFromTheAliasTable()
    {
        var preview = Preview(BlackForest);
        string? GuessFor(string field) => preview.Fields.Single(f => f.Field == field).Guess;

        Assert.Equal("name", GuessFor(RaceCsvFields.Name));
        Assert.Equal("bib", GuessFor(RaceCsvFields.Bib));
        Assert.Equal("status", GuessFor(RaceCsvFields.Status));
        Assert.Equal("finish", GuessFor(RaceCsvFields.Finish));
        Assert.Equal("rank", GuessFor(RaceCsvFields.Rank));
        // Nothing in this file is a club, and a confidently wrong guess is worse than none.
        Assert.Null(GuessFor(RaceCsvFields.Club));
    }

    [Fact]
    public void Build_WithNoMappingAtAll_StillShowsRows()
    {
        // The FIRST preview arrives with nothing mapped — the user has only just picked the file. Filling
        // the blanks from the guesses is what makes that call show real rows instead of an error, which is
        // what turns the preview into the thing you check rather than the thing you dismiss.
        var preview = Preview(BlackForest);

        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal("KURZ Felix", preview.Rows[0].Name);
        Assert.Equal(9798.2, preview.Rows[0].PublishedFinishSeconds!.Value, 3);
    }

    [Fact]
    public void Build_CapsTheRowsItBuilds_ButReportsTheWholeFile()
    {
        var rows = string.Concat(Enumerable.Range(1, 50).Select(i => $"{i},{i},R{i},Finisher,1:00:0{i % 10},0:30:00\n"));

        var preview = Preview("rank,bib,name,status,finish,cp_raimartihof\n" + rows);

        Assert.Equal(50, preview.RowCount);
        Assert.Equal(RaceImportPreview.RowLimit, preview.Rows.Count);
    }

    // ── the split pickers ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Ski to Sea import came out shifted by a column: the pickers guessed on each CHECKPOINT's name,
    /// but a per-section column holds the duration of the section ENDING there — one row back. Every rider
    /// got the next section's time and the published TOTAL was fed in as the last section, doubling 494
    /// riders' finishes. The rule now lives next to the checkpoints, which is where it can be tested.
    /// </summary>
    [Fact]
    public void Build_PerLeg_LabelsAndGuessesEachPickerOnItsSECTION_NotItsCheckpoint()
    {
        var csv = "bib,team,finish_time,xcski,dhski\n1,A,2:00:00,1:00:00,1:00:00\n";

        var preview = Preview(csv, new RaceCsvMapping { NameColumn = "team", PerLegTimes = true });

        // Under per-section times the Finish ends a section like any other checkpoint, so it takes a picker.
        Assert.Equal([MidId, FinishId], preview.SplitColumns.Select(s => s.CheckpointId));
        Assert.Equal(["xcski", "dhski"], preview.SplitColumns.Select(s => s.Label));
        Assert.Equal(["xcski", "dhski"], preview.SplitColumns.Select(s => s.Guess));
        // ...and never the published total, which is the mistake that doubled every finish time.
        Assert.DoesNotContain("finish_time", preview.SplitColumns.Select(s => s.Guess));
    }

    [Fact]
    public void Build_Elapsed_OffersNoPickerForTheFinish_TheFinishColumnOwnsIt()
    {
        // A second column mapped to the finish checkpoint is a composite-PK collision that 500s the import.
        var preview = Preview(BlackForest);

        Assert.Equal([MidId], preview.SplitColumns.Select(s => s.CheckpointId));
        Assert.Equal("Raimartihof", preview.SplitColumns[0].Label);
        Assert.Equal("cp_raimartihof", preview.SplitColumns[0].Guess);
    }

    [Fact]
    public void Build_PerLeg_AnUnnamedSection_IsLeftUnguessed_NotGuessedOffByOne()
    {
        var checkpoints = Checkpoints();
        foreach (var c in checkpoints) c.LegName = null;

        var preview = RaceImportPreview.Build(
            RaceCsvDecoder.Decode(Encoding.UTF8.GetBytes("bib,team,finish_time,xcski\n1,A,2:00:00,1:00:00\n")),
            new RaceCsvMapping { NameColumn = "team", PerLegTimes = true },
            checkpoints, RaceId);

        Assert.All(preview.SplitColumns, s => Assert.Null(s.Guess));
        Assert.Contains("Section 1", preview.SplitColumns[0].Label, StringComparison.Ordinal);
    }

    // ── the warnings that pay for the whole feature ────────────────────────────────────────────────

    /// <summary>
    /// THE guard. Black Forest publishes CUMULATIVE cp_* columns; chained as per-section they sum to
    /// 10:27:26 against a published 2:43:18 — for all 864 riders, with no error anywhere. Two numbers
    /// side by side settle it, and this warning names it outright.
    /// </summary>
    [Fact]
    public void Build_CumulativeFileReadAsPerSection_IsCalledOut()
    {
        var csv = "bib,name,finish,cp_a,cp_b\n"
                + "1,KURZ Felix,2:43:18.2,1:32:18.2,2:43:18.2\n";

        var preview = Preview(csv, new RaceCsvMapping
        {
            NameColumn = "name",
            FinishColumn = "finish",
            PerLegTimes = true,
            SplitColumns = new Dictionary<int, string> { [MidId] = "cp_a", [FinishId] = "cp_b" },
        });

        var computed = preview.Rows[0].ComputedFinishSeconds!.Value;
        Assert.True(computed > preview.Rows[0].PublishedFinishSeconds * 1.5, $"computed {computed}");
        Assert.Contains(preview.Warnings, w => w.Contains("CUMULATIVE", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_PerSectionFileReadAsElapsed_IsCalledOutTheOtherWay()
    {
        // Section durations do not increase along the course; elapsed times always do.
        var csv = "bib,name,finish,leg_a,leg_b\n1,A,2:00:00,1:30:00,0:30:00\n";

        var preview = Preview(csv, new RaceCsvMapping
        {
            NameColumn = "name",
            FinishColumn = "finish",
            PerLegTimes = false,
            SplitColumns = new Dictionary<int, string> { [MidId] = "leg_a", [StartId] = "leg_b" },
        });

        Assert.Contains(preview.Warnings, w => w.Contains("SECTION durations", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AgreeingClocks_RaiseNoDisagreementWarning()
    {
        var preview = Preview(BlackForest);

        Assert.DoesNotContain(preview.Warnings, w => w.Contains("CUMULATIVE", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Warnings, w => w.Contains("SECTION durations", StringComparison.Ordinal));
    }

    /// <summary>
    /// Boston 2014 publishes decimal MINUTES (`85.25`), and ParseElapsedSeconds reads a bare number as
    /// SECONDS. That is a 60× error which looks like a plausible number rather than a broken one, so
    /// nothing downstream can catch it — only the user can.
    /// </summary>
    [Fact]
    public void Build_BareNumbersInTheFinishColumn_WarnAboutDecimalMinutes()
    {
        var preview = Preview("bib,name,official\n1,A,85.25\n2,B,92.5\n",
            new RaceCsvMapping { NameColumn = "name", FinishColumn = "official" });

        Assert.Contains(preview.Warnings, w => w.Contains("decimal MINUTES", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_FormattedTimes_RaiseNoDecimalMinutesWarning()
    {
        Assert.DoesNotContain(Preview(BlackForest).Warnings,
            w => w.Contains("decimal MINUTES", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AnUnreadableStatusToken_IsNamedRatherThanSwallowed()
    {
        var csv = "bib,name,status,finish\n1,GERSTNER Anna,DNS/DNF,\n";

        var preview = Preview(csv);

        Assert.Equal(RaceFinishStatus.Unknown, preview.Rows[0].Status);
        Assert.Equal("DNS/DNF", preview.Rows[0].StatusRaw);
        Assert.Contains(preview.Warnings, w => w.Contains("DNS/DNF", StringComparison.Ordinal));
    }

    // ── failure modes that must still SHOW the header ──────────────────────────────────────────────

    [Fact]
    public void Build_AnUnmappableFile_ReturnsTheHeaderAndAWarning_NotAThrow()
    {
        // The header and the guesses are exactly what the user needs in order to fix the mapping.
        // Throwing would hide both and leave them with a status line and nothing to act on.
        var preview = Preview("a,b\n1,2\n", new RaceCsvMapping { NameColumn = "nope", FinishColumn = "b" });

        Assert.Equal(["a", "b"], preview.Header);
        Assert.Empty(preview.Rows);
        Assert.NotEmpty(preview.Warnings);
    }

    [Fact]
    public void Build_ReportsSkippedRowsWithTheirLineNumbers()
    {
        var preview = Preview("bib,name,status,finish\n1,,Finisher,1:00:00\n2,B,,\n");

        Assert.Contains(preview.Skipped, s => s.Contains("line 2", StringComparison.Ordinal));
        Assert.Contains(preview.Skipped, s => s.Contains("line 3", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RowsCarryTheLineTheyCameFrom()
    {
        var preview = Preview(BlackForest);

        // 1-based with the header included, so it matches what the user sees in a text editor.
        Assert.Equal([2, 3], preview.Rows.Select(r => r.Line));
    }
}
