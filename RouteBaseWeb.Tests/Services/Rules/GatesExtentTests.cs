using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// The gates extent + gate membership rules end-to-end: greedy start→finish pairing, lap
/// courses (same gate as both), the min-effort guard, and startsAtGate / endsAtGate.
/// Vertical gates on lng; tracks run along lat 5, so lng is the crossing coordinate.
/// </summary>
public class GatesExtentTests
{
    private static ShapeDef Gate(int no, double lng) => new()
    {
        No = no,
        Kind = "gate",
        Geom = new LineGeom { Lat1 = 0, Lng1 = lng, Lat2 = 10, Lng2 = lng },
    };

    private static RuleDocument Doc(
        ExtentRule extent, List<ShapeDef> gates,
        double debounce = 0, double minEffort = 0, params MembershipRule[] membership) => new()
    {
        Shapes = gates,
        Extent = extent,
        Membership = membership.ToList(),
        GateDebounceSeconds = debounce,
        MinEffortSeconds = minEffort,
    };

    private static IReadOnlyList<EffortResult> Run(RuleDocument doc, params (double lng, double t)[] pts)
    {
        var ev = SegmentRuleEvaluator.Compile(doc);
        var samples = pts.Select((p, i) => new TrackSample(i, 5, p.lng, p.t)).ToArray();
        var evidence = TrackEvidenceExtractor.Extract(samples, ev.Zones, ev.Gates, ev.GateDebounceSeconds);
        return ev.Evaluate(evidence);
    }

    // ---------------------------------------------------------------- lap course (start == finish)

    private static RuleDocument LapDoc(double minEffort = 0) =>
        Doc(new GatesExtent { StartGates = { 1 }, FinishGates = { 1 } }, new() { Gate(1, 10) }, minEffort: minEffort);

    [Fact]
    public void ZeroCrossings_NoEfforts() =>
        Assert.Empty(Run(LapDoc(), (5, 0), (6, 100), (7, 200)));

    [Fact]
    public void OneCrossing_CannotClose_NoEfforts() =>
        Assert.Empty(Run(LapDoc(), (5, 0), (15, 100)));

    [Fact]
    public void TwoCrossings_OneLap()
    {
        var efforts = Run(LapDoc(), (5, 0), (15, 100), (5, 200));
        var e = Assert.Single(efforts);
        Assert.Equal(new EffortSpan(1, 1), e.Span);   // between the two crossings
    }

    [Fact]
    public void ThreeCrossings_TwoLaps_WithDanglingTrailingStart()
    {
        var efforts = Run(LapDoc(), (5, 0), (15, 100), (5, 200), (15, 300));
        Assert.Equal(new[] { new EffortSpan(1, 1), new EffortSpan(2, 2) }, efforts.Select(e => e.Span).ToArray());
    }

    [Fact]
    public void MinEffort_DropsTooShortSpans()
    {
        // Two crossings 10 s apart. minEffort 20 → dropped; 0 → kept.
        Assert.Empty(Run(LapDoc(minEffort: 20), (5, 0), (15, 10), (5, 20)));
        Assert.Single(Run(LapDoc(minEffort: 0), (5, 0), (15, 10), (5, 20)));
    }

    // ---------------------------------------------------------------- distinct start / finish gates

    [Fact]
    public void DistinctGates_PairStartToFinish()
    {
        var doc = Doc(new GatesExtent { StartGates = { 1 }, FinishGates = { 2 } }, new() { Gate(1, 10), Gate(2, 20) });
        var e = Assert.Single(Run(doc, (5, 0), (15, 100), (25, 200)));
        Assert.Equal(new EffortSpan(1, 1), e.Span);
    }

    // ---------------------------------------------------------------- Stage-1 prefilter

    [Fact]
    public void CouldMatch_RequiresBothAStartAndFinishGateReachable()
    {
        var doc = Doc(new GatesExtent { StartGates = { 1 }, FinishGates = { 2 } }, new() { Gate(1, 10), Gate(2, 20) });
        var ev = SegmentRuleEvaluator.Compile(doc);

        Assert.True(ev.CouldMatch(new GeoBoundingBox(0, 10, 0, 25)));    // reaches both gates
        Assert.False(ev.CouldMatch(new GeoBoundingBox(0, 10, 0, 12)));   // finish gate (lng 20) unreachable
    }

    // ---------------------------------------------------------------- startsAtGate / endsAtGate

    [Fact]
    public void StartsAtGate_RequiresCrossingOnTheFirstSegment()
    {
        var doc = Doc(new WholeExtent(), new() { Gate(1, 10) }, membership: new StartsAtGateRule { Zones = { 1 } });
        Assert.Single(Run(doc, (5, 0), (15, 100)));           // crosses on segment 0 → starts at the gate
        Assert.Empty(Run(doc, (5, 0), (6, 50), (15, 100)));   // crosses on segment 1 → not at the start
    }

    [Fact]
    public void EndsAtGate_RequiresCrossingOnTheLastSegment()
    {
        var doc = Doc(new WholeExtent(), new() { Gate(1, 10) }, membership: new EndsAtGateRule { Zones = { 1 } });
        Assert.Single(Run(doc, (5, 0), (15, 100)));           // last segment (0) crosses
        Assert.Empty(Run(doc, (5, 0), (15, 50), (16, 100)));  // crossing is on segment 0, last segment is 1
    }

    [Fact]
    public void StartsAtGate_ComposesWithGatesExtent()
    {
        var doc = Doc(new GatesExtent { StartGates = { 1 }, FinishGates = { 2 } }, new() { Gate(1, 10), Gate(2, 20) },
            membership: new StartsAtGateRule { Zones = { 1 } });
        Assert.Single(Run(doc, (5, 0), (15, 100), (25, 200)));
    }
}
