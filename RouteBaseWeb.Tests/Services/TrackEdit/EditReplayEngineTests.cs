using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>Engine error paths + engine-only invariants (A1 §5.2 #9-25). Success geometry lives in the
/// shared parity fixtures; these pin the codes, op-index reporting, source immutability and determinism.</summary>
public class EditReplayEngineTests
{
    [Fact]
    public void LoadActivity_MissingSource_ReturnsSourceMissing() =>
        AssertError(Replay(NoSources, new LoadActivityOp(999)), 0, TrackEditErrors.SourceMissing);

    [Fact]
    public void OpOnUnknownSlot_ReturnsSlotNotFound() =>
        AssertError(
            Replay(Sources((101, Track(101, 4))), new LoadActivityOp(101), new CropToOp(5, 0, 1)),
            1, TrackEditErrors.SlotNotFound);

    [Fact]
    public void OpOnConsumedSlot_ReturnsSlotConsumed() =>
        AssertError(
            Replay(
                Sources((201, Track(201, 3)), (202, Track(202, 2))),
                new LoadActivityOp(201), new LoadActivityOp(202),
                new AppendOp(0, 1, TimeMode.Keep, null),
                new CropToOp(1, 0, 1)),
            3, TrackEditErrors.SlotConsumed);

    [Fact]
    public void Append_Self_ReturnsSelfAppend() =>
        AssertError(
            Replay(Sources((101, Track(101, 4))), new LoadActivityOp(101), new AppendOp(0, 0, TimeMode.Keep, null)),
            1, TrackEditErrors.SelfAppend);

    [Fact]
    public void CropTo_Inverted_ReturnsRangeInverted() =>
        AssertError(
            Replay(Sources((101, Track(101, 4))), new LoadActivityOp(101), new CropToOp(0, 2, 1)),
            1, TrackEditErrors.RangeInverted);

    [Fact]
    public void DeleteRange_WholeTrack_ReturnsResultTooSmall() =>
        AssertError(
            Replay(Sources((101, Track(101, 3))), new LoadActivityOp(101), new DeleteRangeOp(0, 0, 2, TimeMode.Keep, null)),
            1, TrackEditErrors.ResultTooSmall);

    [Fact]
    public void SplitAt_EdgeIndices_ReturnSplitIndexOutOfBounds()
    {
        var sources = Sources((101, Track(101, 4)));
        AssertError(Replay(sources, new LoadActivityOp(101), new SplitAtOp(0, 0)), 1, TrackEditErrors.SplitIndexOutOfBounds);
        AssertError(Replay(sources, new LoadActivityOp(101), new SplitAtOp(0, 3)), 1, TrackEditErrors.SplitIndexOutOfBounds);
    }

    [Fact]
    public void CloseWithoutGapSeconds_ReturnsGapSecondsMissing() =>
        AssertError(
            Replay(Sources((101, Track(101, 4))), new LoadActivityOp(101), new DeleteRangeOp(0, 1, 2, TimeMode.Close, null)),
            1, TrackEditErrors.GapSecondsMissing);

    [Fact]
    public void GapSecondsOverCap_ReturnsGapSecondsOutOfRange() =>
        AssertError(
            Replay(Sources((101, Track(101, 4))),
                new LoadActivityOp(101), new DeleteRangeOp(0, 1, 2, TimeMode.Close, TrackEditLimits.MaxGapSeconds + 1)),
            1, TrackEditErrors.GapSecondsOutOfRange);

    [Fact]
    public void CloseWithNaNAnchor_ReturnsTimeAnchorMissing()
    {
        // anchor for from==1 is index 0; make its timestamp missing.
        var track = Track(101, 4, time: new[] { double.NaN, 1010, 1020, 1030 });
        AssertError(
            Replay(Sources((101, track)), new LoadActivityOp(101), new DeleteRangeOp(0, 1, 2, TimeMode.Close, 5)),
            1, TrackEditErrors.TimeAnchorMissing);
    }

    [Fact]
    public void Error_CarriesFailingOpIndex() =>
        AssertError(
            Replay(Sources((101, Track(101, 4))),
                new LoadActivityOp(101), new CropToOp(0, 0, 1), new CropToOp(0, 0, 9)),
            2, TrackEditErrors.RangeOutOfBounds);

    [Fact]
    public void Replay_PreservesClientUnseenChannels()
    {
        // gradeSmooth/cadence/power/pressure the client details API never sends must still slice correctly.
        int n = 4;
        var grade = new double[] { 1, 2, 3, 4 };
        var cadence = new double[] { 80, 81, 82, 83 };
        var power = new double[] { 200, 201, 202, 203 };
        var pressure = new double[] { 1013.00, 1013.10, 1013.20, 1013.30 };
        var track = DecodedTrackExtensions.FromDoubleChannels(
            Seq(n, 1000, 10), Seq(n, 47, 0.001), Seq(n, 8, 0.001),
            null, null, null, null, grade, cadence, power, pressure);

        var outcome = Replay(Sources((101, track)), new LoadActivityOp(101), new CropToOp(0, 1, 2));

        var result = Single(outcome).Track;
        Assert.Equal(3.0, result.GradeSmooth[1], 3);   // index 2 of source
        Assert.Equal(82.0, result.Cadence[1]);
        Assert.Equal(202.0, result.Power[1]);
        Assert.Equal(1013.20, result.Pressure[1], 3);
    }

    [Fact]
    public void Replay_DoesNotMutateSourceTracks()
    {
        var track = Track(101, 5);
        int[] latBefore = (int[])track.Lat.RawQ!.Clone();
        int[] timeBefore = (int[])track.TimeEpochS.RawQ!.Clone();

        Replay(Sources((101, track)),
            new LoadActivityOp(101),
            new SplitAtOp(0, 2),
            new DeleteRangeOp(0, 0, 0, TimeMode.Close, 1));

        Assert.Equal(latBefore, track.Lat.RawQ);
        Assert.Equal(timeBefore, track.TimeEpochS.RawQ);
    }

    [Fact]
    public void Replay_SamePrefixTwice_IsDeterministic()
    {
        var ops = new EditOp[] { new LoadActivityOp(101), new SplitAtOp(0, 2), new CropToOp(1, 0, 1) };
        var a = EditReplayEngine.Replay(ops, Sources((101, Track(101, 5))));
        var b = EditReplayEngine.Replay(ops, Sources((101, Track(101, 5))));

        Assert.Null(a.Error);
        Assert.Null(b.Error);
        Assert.Equal(a.Slots.Count, b.Slots.Count);
        for (int i = 0; i < a.Slots.Count; i++)
        {
            Assert.Equal(a.Slots[i].SlotId, b.Slots[i].SlotId);
            Assert.Equal(a.Slots[i].Track.Count, b.Slots[i].Track.Count);
            for (int j = 0; j < a.Slots[i].Track.Count; j++)
                Assert.Equal(a.Slots[i].Track.Lat[j], b.Slots[i].Track.Lat[j]);
        }
    }

    [Fact]
    public void Replay_EmptyScript_ReturnsZeroSlots()
    {
        var outcome = EditReplayEngine.Replay(Array.Empty<EditOp>(), NoSources);
        Assert.Null(outcome.Error);
        Assert.Empty(outcome.Slots);
    }

    [Fact]
    public void TimeShiftOverflow_ReturnsValueOutOfRange()
    {
        // keep-append rebases source into target's QBias; a far-apart epoch pushes the folded int32 over.
        var target = Track(201, 2, time: new double[] { 0, 10 });
        var source = Track(202, 2, time: new double[] { 2_147_483_640, 2_147_483_650 });

        var outcome = Replay(
            Sources((201, target), (202, source)),
            new LoadActivityOp(201), new LoadActivityOp(202), new AppendOp(0, 1, TimeMode.Keep, null));

        AssertError(outcome, 2, TrackEditErrors.ValueOutOfRange);
    }

    [Fact]
    public void AppendAcrossQBias_RebasesExactly()
    {
        var target = Track(201, 2, time: new double[] { 1_000_000_000, 1_000_000_010 });
        var source = Track(202, 2, time: new double[] { 1_500_000_000, 1_500_000_010 });

        var outcome = Replay(
            Sources((201, target), (202, source)),
            new LoadActivityOp(201), new LoadActivityOp(202), new AppendOp(0, 1, TimeMode.Keep, null));

        var track = Single(outcome).Track;
        Assert.Equal(new[] { 1_000_000_000.0, 1_000_000_010.0, 1_500_000_000.0, 1_500_000_010.0 },
            new[] { track.TimeEpochS[0], track.TimeEpochS[1], track.TimeEpochS[2], track.TimeEpochS[3] });
    }

    // ---- helpers ----

    private static readonly IReadOnlyDictionary<int, DecodedTrack> NoSources = new Dictionary<int, DecodedTrack>();

    private static ReplayOutcome Replay(IReadOnlyDictionary<int, DecodedTrack> sources, params EditOp[] ops) =>
        EditReplayEngine.Replay(ops, sources);

    private static Dictionary<int, DecodedTrack> Sources(params (int id, DecodedTrack track)[] tracks) =>
        tracks.ToDictionary(t => t.id, t => t.track);

    private static DecodedTrack Track(int id, int n, double[]? time = null, double[]? distance = null)
    {
        time ??= Seq(n, 1000, 10);
        return DecodedTrackExtensions.FromDoubleChannels(
            time, Seq(n, 47, 0.001), Seq(n, 8, 0.001),
            null, distance, null, null, null, null, null, null);
    }

    private static double[] Seq(int n, double start, double step)
    {
        var a = new double[n];
        for (int i = 0; i < n; i++) a[i] = start + i * step;
        return a;
    }

    private static ReplaySlot Single(ReplayOutcome outcome)
    {
        Assert.Null(outcome.Error);
        return Assert.Single(outcome.Slots);
    }

    private static void AssertError(ReplayOutcome outcome, int opIndex, string code)
    {
        Assert.NotNull(outcome.Error);
        Assert.Equal(opIndex, outcome.Error!.OpIndex);
        Assert.Equal(code, outcome.Error.Code);
    }
}
