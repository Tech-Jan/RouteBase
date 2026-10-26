using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Races;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// The import RULES — what becomes a participant, what is a DNF, what is a duplicate — tested pure,
/// with no database in sight.
/// </summary>
public class RaceCsvImporterTests
{
    private static readonly RaceId RaceId = new(1);
    private const int StartId = 10;
    private const int RaimartihofId = 11;
    private const int FinishId = 12;

    private static List<RaceCheckpoint> Checkpoints() =>
    [
        new() { CheckpointId = new CheckpointId(StartId), RaceId = RaceId, Seq = 0, CheckpointName = "Start", ChainageMeters = 0 },
        new() { CheckpointId = new CheckpointId(RaimartihofId), RaceId = RaceId, Seq = 1, CheckpointName = "Raimartihof", ChainageMeters = 21000 },
        new() { CheckpointId = new CheckpointId(FinishId), RaceId = RaceId, Seq = 2, CheckpointName = "Finish", ChainageMeters = 38910 },
    ];

    private static RaceCsvMapping Mapping(bool withSplit = false) => new()
    {
        NameColumn = "name",
        FinishColumn = "finish_time",
        BibColumn = "bib",
        CategoryColumn = "category",
        GenderColumn = "gender",
        ClubColumn = "club",
        SplitColumns = withSplit
            ? new Dictionary<int, string> { [RaimartihofId] = "raimartihof" }
            : new Dictionary<int, string>(),
    };

    private static RaceCsvImportResult Build(string csv, RaceCsvMapping? mapping = null) =>
        RaceCsvImporter.Build(RaceCsvParser.Parse(csv), mapping ?? Mapping(), Checkpoints(), RaceId);

    [Fact]
    public void Build_ProjectsRowsIntoSimulatedParticipants()
    {
        var csv = "bib,name,club,category,gender,finish_time\n"
                + "4496,KURZ Felix,SC Hausach,Sh40ML (1.),M,\"1:16:59,4\"\n";

        var result = Build(csv);

        var p = Assert.Single(result.Participants);
        Assert.Equal(RaceParticipantKind.Simulated, p.Participant.Kind);
        Assert.Equal("KURZ Felix", p.Participant.DisplayName);
        Assert.Equal("4496", p.Participant.Bib);
        Assert.Equal("SC Hausach", p.Participant.Club);
        Assert.Equal("M", p.Participant.Gender);
        Assert.Null(p.Participant.TrackId);                    // simulated riders own no geometry

        var split = Assert.Single(p.Splits);
        Assert.Equal(new CheckpointId(FinishId), split.CheckpointId);
        Assert.Equal(4619.4, split.ElapsedSeconds, 3);
    }

    [Fact]
    public void Build_MapsIntermediateSplitColumns()
    {
        var csv = "name,raimartihof,finish_time\nA,\"1:05:00,0\",\"2:29:06\"\n";

        var result = Build(csv, Mapping(withSplit: true));

        var p = Assert.Single(result.Participants);
        Assert.Equal(2, p.Splits.Count);
        Assert.Equal(3900, p.Splits.Single(s => s.CheckpointId == new CheckpointId(RaimartihofId)).ElapsedSeconds, 1);
        Assert.Equal(8946, p.Splits.Single(s => s.CheckpointId == new CheckpointId(FinishId)).ElapsedSeconds, 1);
    }

    [Fact]
    public void Build_RowWithSplitsButNoFinish_IsKeptAsADnf()
    {
        // He was at Raimartihof and then abandoned. The pacing model holds such a rider at his last
        // checkpoint, so he belongs in the field — dropping him would erase a real thing that happened.
        var csv = "name,raimartihof,finish_time\nBailed Bob,\"1:05:00,0\",DNF\n";

        var result = Build(csv, Mapping(withSplit: true));

        var p = Assert.Single(result.Participants);
        var split = Assert.Single(p.Splits);
        Assert.Equal(new CheckpointId(RaimartihofId), split.CheckpointId);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Build_RowWithNoUsableTimeAtAll_IsSkipped()
    {
        var csv = "name,finish_time\nGhost,DNS\n";

        var result = Build(csv);

        Assert.Empty(result.Participants);
        Assert.Contains(result.Skipped, s => s.Contains("Ghost", StringComparison.Ordinal) && s.Contains("no usable time", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_FinishMappedAsBothASplitColumnAndTheFinish_ProducesOneSplit_NotAPkCollision()
    {
        // BE-02: (participant_ID, checkpoint_ID) is the split table's composite PRIMARY KEY. If a caller
        // maps the Finish checkpoint BOTH as a split column and via the Finish column, ElapsedSplits used
        // to emit two rows for the one (participant, finish) pair — which collides on the PK and 500s the
        // whole import. The explicit Finish wins; there is exactly one split per checkpoint.
        var csv = "name,dup,finish_time\nA,\"1:00:00\",\"2:00:00\"\n";
        var mapping = new RaceCsvMapping
        {
            NameColumn = "name",
            FinishColumn = "finish_time",
            SplitColumns = new Dictionary<int, string> { [FinishId] = "dup" },
        };

        var result = Build(csv, mapping);

        var p = Assert.Single(result.Participants);
        Assert.Equal(p.Splits.Count, p.Splits.Select(s => s.CheckpointId).Distinct().Count()); // no dup PK
        var finishSplit = Assert.Single(p.Splits, s => s.CheckpointId == new CheckpointId(FinishId));
        Assert.Equal(7200, finishSplit.ElapsedSeconds, 1);       // the explicit Finish column wins
    }

    [Fact]
    public void Build_NegativeSplitCell_IsDropped_NoNegativeElapsedReachesAStoredSplit()
    {
        // BE-01: a negative cell is no time. The rider keeps his valid finish; the negative intermediate
        // is dropped exactly like a DNF, so no split with a negative elapsed can ever be persisted (and
        // therefore the accumulated splits cannot run backwards).
        var csv = "name,raimartihof,finish_time\nA,\"-1:05:00\",\"2:29:06\"\n";

        var result = Build(csv, Mapping(withSplit: true));

        var p = Assert.Single(result.Participants);
        Assert.All(p.Splits, s => Assert.True(s.ElapsedSeconds >= 0, $"negative split {s.ElapsedSeconds}"));
        var split = Assert.Single(p.Splits);                 // the negative Raimartihof cell dropped
        Assert.Equal(new CheckpointId(FinishId), split.CheckpointId);
        Assert.Equal(8946, split.ElapsedSeconds, 1);
    }

    [Fact]
    public void Build_NamelessRow_IsSkipped()
    {
        var csv = "name,finish_time\n,\"1:16:59,4\"\n";

        var result = Build(csv);

        Assert.Empty(result.Participants);
        Assert.Contains(result.Skipped, s => s.Contains("line 2", StringComparison.Ordinal) && s.Contains("no name", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DuplicateBib_IsSkipped()
    {
        // The same rider twice would race himself.
        var csv = "bib,name,finish_time\n4496,KURZ Felix,\"1:16:59,4\"\n4496,KURZ Felix,\"1:16:59,4\"\n";

        var result = Build(csv);

        Assert.Single(result.Participants);
        Assert.Contains(result.Skipped, s => s.Contains("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_SeedFollowsTheRider_NotTheRowOrder()
    {
        // Re-importing a re-sorted results file must not reshuffle everyone's jitter — the replay would
        // silently change under the user.
        var a = Build("bib,name,finish_time\n1,A,1:00:00\n2,B,2:00:00\n");
        var b = Build("bib,name,finish_time\n2,B,2:00:00\n1,A,1:00:00\n");

        var seedA1 = a.Participants.Single(p => p.Participant.Bib == "1").Participant.Seed;
        var seedA2 = b.Participants.Single(p => p.Participant.Bib == "1").Participant.Seed;
        Assert.Equal(seedA1, seedA2);

        var seedB = b.Participants.Single(p => p.Participant.Bib == "2").Participant.Seed;
        Assert.NotEqual(seedA1, seedB);   // ...but different riders still differ
    }

    [Fact]
    public void Build_MissingRequiredColumn_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Build("name,time\nA,1:00:00\n"));
    }

    [Fact]
    public void Build_SplitColumnForACheckpointNotOnThisRace_IsRejected()
    {
        var mapping = Mapping() with { SplitColumns = new Dictionary<int, string> { [999] = "finish_time" } };

        Assert.Throws<ArgumentException>(() => Build("name,finish_time\nA,1:00:00\n", mapping));
    }

    // ── the published status ───────────────────────────────────────────────────────────────────────

    private static RaceCsvMapping WithStatus() => Mapping() with { StatusColumn = "status" };

    /// <summary>
    /// 113 of 1387 Schauinsland entrants carry a status and an EMPTY finish column. They used to be
    /// skipped as "no usable time" — 8% of the field, silently. A published status IS a result.
    /// </summary>
    [Fact]
    public void Build_KeepsARowThatHasAStatusAndNoTime()
    {
        var csv = "bib,name,status,finish_time\n"
                + "101,GERSTNER Anna,DNS/DNF,\n";

        var result = Build(csv, WithStatus());

        var p = Assert.Single(result.Participants);
        Assert.Empty(result.Skipped);
        Assert.Empty(p.Splits);
        Assert.Equal(RaceFinishStatus.Unknown, p.Participant.FinishStatus);
        // The merged token survives verbatim — normalizing it away would lose the only thing the file said.
        Assert.Equal("DNS/DNF", p.Participant.FinishStatusRaw);
    }

    [Fact]
    public void Build_WithoutAStatusColumn_StillSkipsARowWithNoTime()
    {
        // The rescue above is EARNED by the status column, not free: an unmapped file behaves as before.
        var result = Build("bib,name,status,finish_time\n101,GERSTNER Anna,DNS/DNF,\n");

        Assert.Empty(result.Participants);
        Assert.Single(result.Skipped);
    }

    /// <summary>
    /// The Black Forest inversion: bibs 2423, 2843, 2713 and 2569 are published DNF and carry a `cp_ziel`
    /// finish-mat time. Deriving the result from the splits reported four DNFs as finishers — and the
    /// guesser actively encourages mapping `cp_ziel` to Finish, since *Ziel* is German for finish.
    /// </summary>
    [Fact]
    public void Build_APublishedDnfWithAFinishTime_IsStillADnf()
    {
        var csv = "bib,name,status,finish_time\n"
                + "2569,SCHMIDT Jan,DNF,2:43:18.2\n";

        var result = Build(csv, WithStatus());

        var p = Assert.Single(result.Participants);
        Assert.Equal(RaceFinishStatus.Dnf, p.Participant.FinishStatus);
        // He keeps the split — he really did cross that mat, and the replay rides him to it.
        Assert.Contains(p.Splits, s => s.CheckpointId == new CheckpointId(FinishId));
        // ...but he is not counted as a finisher, which is the number the summary reports.
        Assert.Equal(0, result.FinisherCount);
    }

    [Fact]
    public void FinisherCount_FollowsTheFileWhereItSpeaks_AndTheSplitsWhereItDoesNot()
    {
        var withStatus = Build(
            "bib,name,status,finish_time\n"
            + "1,A,Finisher,1:00:00\n"
            + "2,B,DNF,1:10:00\n"        // a mat time and a published DNF
            + "3,C,DNS,\n"
            + "4,D,Finisher,2:00:00\n",
            WithStatus());

        Assert.Equal(4, withStatus.Participants.Count);
        Assert.Equal(2, withStatus.FinisherCount);

        // No status column: the derived rule, exactly as before.
        var derived = Build("bib,name,finish_time\n1,A,1:00:00\n2,B,\n");
        Assert.Equal(1, derived.FinisherCount);
    }

    [Fact]
    public void Build_ABlankStatusCell_LeavesTheColumnsNull_SoTheDerivedRuleGoverns()
    {
        var result = Build("bib,name,status,finish_time\n1,A,,1:00:00\n", WithStatus());

        var p = Assert.Single(result.Participants);
        Assert.Null(p.Participant.FinishStatus);
        Assert.Null(p.Participant.FinishStatusRaw);
        Assert.Equal(1, result.FinisherCount);
    }

    [Fact]
    public void Build_AnUnmappedStatusColumn_LeavesEveryParticipantsStatusNull()
    {
        // The migration story for the four races imported before this existed: nothing to backfill.
        var result = Build("bib,name,status,finish_time\n1,A,DNF,1:00:00\n");

        Assert.All(result.Participants, p => Assert.Null(p.Participant.FinishStatus));
    }

    // ── staggered starts ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_AnExplicitStartOffsetColumn_IsRead()
    {
        var mapping = Mapping() with { StartOffsetColumn = "start_offset" };

        var result = Build("bib,name,finish_time,start_offset\n1,A,1:00:00,0:12:30\n", mapping);

        Assert.Equal(750, Assert.Single(result.Participants).Participant.StartOffsetSeconds);
    }

    /// <summary>
    /// How the offset actually travels in most files: gun-vs-net is ALWAYS two columns
    /// (Brutto/Netto, CLOCK TIME/CHIP TIME) and an offset column almost never exists. Black Forest
    /// publishes 47 distinct chip-vs-gun offsets this way.
    /// </summary>
    [Fact]
    public void Build_GunMinusNet_DerivesTheOffset_WithNoExtraUserInput()
    {
        var mapping = Mapping() with { GunTimeColumn = "brutto", NetTimeColumn = "netto" };

        var result = Build("bib,name,finish_time,brutto,netto\n1,A,1:16:59,1:19:29,1:16:59\n", mapping);

        Assert.Equal(150, Assert.Single(result.Participants).Participant.StartOffsetSeconds);
    }

    [Fact]
    public void Build_AnExplicitOffsetColumn_BeatsTheGunNetDerivation()
    {
        var mapping = Mapping() with
        {
            StartOffsetColumn = "start_offset", GunTimeColumn = "brutto", NetTimeColumn = "netto",
        };

        var result = Build(
            "bib,name,finish_time,start_offset,brutto,netto\n1,A,1:16:59,0:05:00,1:19:29,1:16:59\n", mapping);

        Assert.Equal(300, Assert.Single(result.Participants).Participant.StartOffsetSeconds);
    }

    [Fact]
    public void Build_NetPastGun_IsNoOffsetAtAll()
    {
        // Net can only exceed gun through a timing error or a mis-mapped pair, and a rider who starts
        // before the gun would be placed on the course before the race.
        var mapping = Mapping() with { GunTimeColumn = "brutto", NetTimeColumn = "netto" };

        var result = Build("bib,name,finish_time,brutto,netto\n1,A,1:16:59,1:16:59,1:19:29\n", mapping);

        Assert.Null(Assert.Single(result.Participants).Participant.StartOffsetSeconds);
    }

    [Fact]
    public void Build_WithNoFinishColumn_UsesNetAsTheFinish()
    {
        // Net is the rider's own clock, which is the basis every other time in this slice is measured on.
        // Saves mapping one column to two pickers on a file that publishes Brutto/Netto and nothing else.
        var mapping = Mapping() with
        {
            FinishColumn = null, GunTimeColumn = "brutto", NetTimeColumn = "netto",
        };

        var result = Build("bib,name,brutto,netto\n1,A,1:19:29,1:16:59\n", mapping);

        var p = Assert.Single(result.Participants);
        Assert.Equal(4619, p.Participant.OfficialFinishSeconds);
        Assert.Equal(150, p.Participant.StartOffsetSeconds);
    }

    [Fact]
    public void Build_AWaveLabel_IsImportedVerbatim_NotParsed()
    {
        // "Startblock 1: 10 Uhr bis 11 Uhr" carries the start time in PROSE. Guessing at German-language
        // time ranges is exactly the kind of code this whole project exists to stop writing — the label is
        // kept as text and the user gives it a number once.
        var mapping = Mapping() with { WaveColumn = "start_block" };

        var result = Build(
            "bib,name,finish_time,start_block\n1,A,1:00:00,Startblock 1: 10 Uhr bis 11 Uhr\n", mapping);

        var p = Assert.Single(result.Participants);
        Assert.Equal("Startblock 1: 10 Uhr bis 11 Uhr", p.Participant.Wave);
        Assert.Null(p.Participant.StartOffsetSeconds);
    }

    // ── discipline + category normalization ───────────────────────────────────────────────────────

    [Fact]
    public void Build_ADisciplineColumn_IsImportedAsAFacet()
    {
        // Schauinsland runs ELEVEN races off one start list, and its rank restarts per discipline.
        var mapping = Mapping() with { DisciplineColumn = "discipline" };

        var result = Build("bib,name,finish_time,discipline\n1,A,1:00:00,Rennrad\n", mapping);

        Assert.Equal("Rennrad", Assert.Single(result.Participants).Participant.Discipline);
    }

    /// <summary>
    /// Datasport embeds the rider's POSITION within his class in the class NAME. Race 2 therefore carried
    /// 424 distinct categories for 425 participants — a Division filter with one option per rider, which
    /// is no filter at all. Nothing is lost: a rank is already derivable from the results.
    /// </summary>
    [Theory]
    [InlineData("Sh40F (12.)", "Sh40F")]
    [InlineData("Sh40ML (1.)", "Sh40ML")]
    [InlineData("Sh40F (12)", "Sh40F")]
    [InlineData("Sh40F  ( 3 . )", "Sh40F")]
    public void Build_StripsATrailingRankFromTheCategory(string raw, string expected)
    {
        var result = Build($"bib,name,category,finish_time\n1,A,{raw},1:00:00\n");

        Assert.Equal(expected, Assert.Single(result.Participants).Participant.Category);
    }

    [Theory]
    // A category is free text and most of what is in brackets is meaningful.
    [InlineData("M40 (Masters)")]
    [InlineData("Sh40F")]
    [InlineData("Herren 40 (Elite)")]
    [InlineData("(12.)")]
    public void Build_LeavesACategoryThatIsNotARank_Alone(string raw)
    {
        var result = Build($"bib,name,category,finish_time\n1,A,\"{raw}\",1:00:00\n");

        Assert.Equal(raw, Assert.Single(result.Participants).Participant.Category);
    }

    [Fact]
    public void NormalizeCategory_CollapsesARealFieldFrom424DistinctValuesToItsRealDivisions()
    {
        // The shape of race 2's data: one class, one rank per rider. Before, that was 5 "divisions".
        string[] raw = ["Sh40F (1.)", "Sh40F (2.)", "Sh40F (3.)", "Sh40ML (1.)", "Sh40ML (2.)"];

        var distinct = raw.Select(RaceCsvImporter.NormalizeCategory).Distinct(StringComparer.Ordinal).ToList();

        Assert.Equal(["Sh40F", "Sh40ML"], distinct);
    }

    [Fact]
    public void Build_WithNoStaggeredColumnsMapped_LeavesEveryEntrantOnTheGun()
    {
        var result = Build("bib,name,finish_time\n1,A,1:00:00\n");

        var p = Assert.Single(result.Participants);
        Assert.Null(p.Participant.StartOffsetSeconds);
        Assert.Null(p.Participant.Wave);
    }
}
