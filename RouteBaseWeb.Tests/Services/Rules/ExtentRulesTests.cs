using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// Direct unit tests for the compiled extent rules (<see cref="CompiledWholeExtent"/>,
/// <see cref="CompiledZoneSpanExtent"/>, <see cref="CompiledGatesExtent"/>). These are pure combinators
/// over hand-built <see cref="TrackEvidence"/> with known answers — no recorded blobs — so every asserted
/// span can be reasoned about from the fixture alone.
///
/// written to kill the surviving/no-coverage mutants in ExtentRules.cs. The gate-span cases are the
/// interesting ones: the greedy start→finish pairing, its min-effort threshold, and the merge/sort of the
/// crossing stream all had zero assertions before.
/// </summary>
public class ExtentRulesTests
{
    private static readonly GeoBoundingBox Anywhere = new(-90, 90, -180, 180);
    private static readonly GeoBoundingBox FarAway  = new(80, 81, 170, 171);

    /// <summary>Evidence carrying only the gate-crossing streams the extent rules read (no zones needed).</summary>
    private static TrackEvidence GateEvidence(int sampleCount, params GateCrossing[][] gateCrossings)
    {
        var samples = new List<TrackSample>(sampleCount);
        for (int i = 0; i < sampleCount; i++) samples.Add(new TrackSample(i, 0, 0, i));
        return new TrackEvidence
        {
            Samples          = samples,
            ZoneVisits       = Array.Empty<IReadOnlyList<ZoneVisit>>(),
            ZoneClipSegments = Array.Empty<IReadOnlyList<int>>(),
            GateCrossings    = gateCrossings.Select(g => (IReadOnlyList<GateCrossing>)g).ToArray(),
        };
    }

    private static TrackEvidence ZoneEvidence(int sampleCount, params ZoneVisit[][] zoneVisits)
    {
        var samples = new List<TrackSample>(sampleCount);
        for (int i = 0; i < sampleCount; i++) samples.Add(new TrackSample(i, 0, 0, i));
        return new TrackEvidence
        {
            Samples          = samples,
            ZoneVisits       = zoneVisits.Select(z => (IReadOnlyList<ZoneVisit>)z).ToArray(),
            ZoneClipSegments = zoneVisits.Select(_ => (IReadOnlyList<int>)Array.Empty<int>()).ToArray(),
            GateCrossings    = Array.Empty<IReadOnlyList<GateCrossing>>(),
        };
    }

    private static GeoBoundingBox[] Boxes(int n, GeoBoundingBox box) => Enumerable.Repeat(box, n).ToArray();

    // ---------------------------------------------------------------- whole extent

    [Fact]
    public void Whole_NonEmptyTrack_IsOneSpanCoveringEveryPoint()
    {
        var spans = new CompiledWholeExtent().BuildSpans(ZoneEvidence(sampleCount: 6));
        Assert.Equal(new[] { new EffortSpan(0, 5) }, spans);
    }

    [Fact]
    public void Whole_EmptyTrack_YieldsNoSpan()
    {
        var spans = new CompiledWholeExtent().BuildSpans(ZoneEvidence(sampleCount: 0));
        Assert.Empty(spans);
    }

    // ---------------------------------------------------------------- zone-span extent

    // The span runs from the earliest EnterIdx to the latest ExitIdx across every visit of the referenced
    // zones. Two visits [2..4] and [6..8] collapse to one span [2..8].
    [Fact]
    public void ZoneSpan_MultipleVisits_SpansFirstEnterToLastExit()
    {
        var rule = new CompiledZoneSpanExtent(new[] { 0 }, Boxes(1, Anywhere));
        var ev   = ZoneEvidence(10, new[] { new ZoneVisit(2, 4), new ZoneVisit(6, 8) });

        Assert.Equal(new[] { new EffortSpan(2, 8) }, rule.BuildSpans(ev));
    }

    // Mut 60 (conditional-false → always emit a span): with no visits, entry stays int.MaxValue and the rule
    // must return NO span. The mutant would emit EffortSpan(int.MaxValue, int.MinValue).
    [Fact]
    public void ZoneSpan_NoVisits_YieldsNoSpan()
    {
        var rule = new CompiledZoneSpanExtent(new[] { 0 }, Boxes(1, Anywhere));
        var ev   = ZoneEvidence(10, Array.Empty<ZoneVisit>());

        Assert.Empty(rule.BuildSpans(ev));
    }

    // ---------------------------------------------------------------- gate extent: pre-filter

    // Mut 94 (return false → true): an unreachable START gate must prune (false, not "unknown"/null).
    [Fact]
    public void GatesPreFilter_StartGateUnreachable_ReturnsFalse()
    {
        var rule = new CompiledGatesExtent(
            new[] { 0 }, Boxes(1, FarAway),   // start box far from the track
            new[] { 1 }, Boxes(1, Anywhere), minEffortSeconds: 0);

        Assert.Equal((bool?)false, rule.PreFilter(new GeoBoundingBox(0, 1, 0, 1)));
    }

    [Fact]
    public void GatesPreFilter_FinishGateUnreachable_ReturnsFalse()
    {
        var rule = new CompiledGatesExtent(
            new[] { 0 }, Boxes(1, Anywhere),
            new[] { 1 }, Boxes(1, FarAway), minEffortSeconds: 0);

        Assert.Equal((bool?)false, rule.PreFilter(new GeoBoundingBox(0, 1, 0, 1)));
    }

    [Fact]
    public void GatesPreFilter_BothReachable_ReturnsNull()
    {
        var rule = new CompiledGatesExtent(
            new[] { 0 }, Boxes(1, Anywhere),
            new[] { 1 }, Boxes(1, Anywhere), minEffortSeconds: 0);

        Assert.Null(rule.PreFilter(new GeoBoundingBox(0, 1, 0, 1)));
    }

    // ---------------------------------------------------------------- gate extent: build spans

    // Separate start(gate 0)/finish(gate 1) gates. A start crossing at seg 2 pairs with a finish at seg 8;
    // the between-the-lines span is [3, 8].
    [Fact]
    public void Gates_StartThenFinish_SpansBetweenTheLines()
    {
        var rule = SeparateGates(minEffortSeconds: 0);
        var ev   = GateEvidence(12,
            new[] { new GateCrossing(2, 0) },     // gate 0 = start
            new[] { new GateCrossing(8, 100) });  // gate 1 = finish

        Assert.Equal(new[] { new EffortSpan(3, 8) }, rule.BuildSpans(ev));
    }

    // Mut 111 (min-effort `>=` → `>`): a span whose duration EQUALS the threshold must survive.
    [Fact]
    public void Gates_DurationExactlyMinEffort_KeepsTheSpan()
    {
        var rule = SeparateGates(minEffortSeconds: 100);   // finish.Time - start.Time == 100 exactly
        var ev   = GateEvidence(12,
            new[] { new GateCrossing(2, 0) },
            new[] { new GateCrossing(8, 100) });

        Assert.Equal(new[] { new EffortSpan(3, 8) }, rule.BuildSpans(ev));
    }

    [Fact]
    public void Gates_DurationBelowMinEffort_DropsTheSpan()
    {
        var rule = SeparateGates(minEffortSeconds: 101);   // 100 < 101 → blip
        var ev   = GateEvidence(12,
            new[] { new GateCrossing(2, 0) },
            new[] { new GateCrossing(8, 100) });

        Assert.Empty(rule.BuildSpans(ev));
    }

    // Mut 111 (segIdx `>` → `>=`): a finish crossing on the SAME segment as the still-open start must not
    // close (and, under the mutant, must not silently reset `open` and swallow the later real finish). Real
    // keeps [6, 8]; the mutant yields nothing.
    [Fact]
    public void Gates_FinishOnStartSegment_IgnoredThenLaterFinishCloses()
    {
        var rule = SeparateGates(minEffortSeconds: 0);
        var ev   = GateEvidence(12,
            new[] { new GateCrossing(5, 0) },                        // start at seg 5
            new[] { new GateCrossing(5, 50), new GateCrossing(8, 100) }); // finish at seg 5 (same) then seg 8

        Assert.Equal(new[] { new EffortSpan(6, 8) }, rule.BuildSpans(ev));
    }

    // Mut 118 (`!open && e.IsStart` → `!open || e.IsStart`): a second start crossing while a span is already
    // open must NOT move the start point. Real pairs the FIRST start (seg 2) with the finish → [3, 8]; the
    // mutant re-anchors to the second start (seg 6) → [7, 8].
    [Fact]
    public void Gates_SecondStartWhileOpen_DoesNotMoveTheStart()
    {
        var rule = SeparateGates(minEffortSeconds: 0);
        var ev   = GateEvidence(12,
            new[] { new GateCrossing(2, 0), new GateCrossing(6, 50) }, // two start crossings
            new[] { new GateCrossing(8, 100) });                       // one finish

        Assert.Equal(new[] { new EffortSpan(3, 8) }, rule.BuildSpans(ev));
    }

    // Mut 143 (finish-only event IsStart flag `false` → `true`): a finish-gate crossing must not be able to
    // OPEN a span. With one start (seg 2) and two finish-only crossings (seg 8, seg 12), real closes exactly
    // one span [3, 8]; the mutant re-opens on the seg-8 finish and closes a second span [9, 12].
    [Fact]
    public void Gates_FinishOnlyCrossing_NeverOpensASpan()
    {
        var rule = SeparateGates(minEffortSeconds: 0);
        var ev   = GateEvidence(16,
            new[] { new GateCrossing(2, 0) },
            new[] { new GateCrossing(8, 100), new GateCrossing(12, 150) });

        Assert.Equal(new[] { new EffortSpan(3, 8) }, rule.BuildSpans(ev));
    }

    // Mut 145 (Sort statement removed): the merged event stream is start-crossings-then-finish-crossings, so
    // without the by-segment sort a finish at seg 4 is processed AFTER a start at seg 6 and greedy pairing
    // breaks. Real yields two spans [3,4] and [7,8]; the unsorted stream yields only [3,4].
    [Fact]
    public void Gates_InterleavedCrossings_AreSortedBeforeGreedyPairing()
    {
        var rule = SeparateGates(minEffortSeconds: 0);
        var ev   = GateEvidence(12,
            new[] { new GateCrossing(2, 0),  new GateCrossing(6, 60) },  // starts
            new[] { new GateCrossing(4, 40), new GateCrossing(8, 80) }); // finishes

        Assert.Equal(new[] { new EffortSpan(3, 4), new EffortSpan(7, 8) }, rule.BuildSpans(ev));
    }

    // A lap gate (one gate listed as BOTH start and finish) crossed three times = two laps.
    [Fact]
    public void Gates_LapGate_ThreeCrossings_ProduceTwoLaps()
    {
        var rule = new CompiledGatesExtent(
            new[] { 0 }, Boxes(1, Anywhere),
            new[] { 0 }, Boxes(1, Anywhere), minEffortSeconds: 0);
        var ev = GateEvidence(12,
            new[] { new GateCrossing(2, 0), new GateCrossing(5, 50), new GateCrossing(8, 100) });

        Assert.Equal(new[] { new EffortSpan(3, 5), new EffortSpan(6, 8) }, rule.BuildSpans(ev));
    }

    [Fact]
    public void Gates_NoCrossings_YieldNoSpans()
    {
        var rule = SeparateGates(minEffortSeconds: 0);
        Assert.Empty(rule.BuildSpans(GateEvidence(12, Array.Empty<GateCrossing>(), Array.Empty<GateCrossing>())));
    }

    private static CompiledGatesExtent SeparateGates(double minEffortSeconds) => new(
        new[] { 0 }, Boxes(1, Anywhere),
        new[] { 1 }, Boxes(1, Anywhere), minEffortSeconds);
}
