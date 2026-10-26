using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// Truth tables of the compiled membership rules over hand-built evidence, including the
/// per-span semantics (the same track can pass on a clipped span and fail whole-track).
/// </summary>
public class MembershipRuleTests
{
    // Three side-by-side zones on lng: A=[0..10], B=[20..30], C=[40..50]; lat always 0..10.
    private static readonly IShape ZoneA = new GeoRect(0, 10, 0, 10);
    private static readonly IShape ZoneB = new GeoRect(0, 10, 20, 30);
    private static readonly IShape ZoneC = new GeoRect(0, 10, 40, 50);
    private static readonly IShape[] Zones = { ZoneA, ZoneB, ZoneC };

    private static TrackEvidence Evidence(params double[] lngs) =>
        TrackEvidenceExtractor.Extract(
            lngs.Select((lng, i) => new TrackSample(i, 5, lng)).ToArray(), Zones);

    private static EffortSpan Whole(TrackEvidence e) => new(0, e.Samples.Count - 1);

    private static GeoBoundingBox[] Boxes(params int[] zoneIdxs) =>
        zoneIdxs.Select(i => Zones[i].Bbox).ToArray();

    // ---------------------------------------------------------------- passesAny

    [Fact]
    public void PassesAny_TrueWhenAnyZoneTouched()
    {
        var rule = new CompiledPassesAny(new[] { 0, 1 }, Boxes(0, 1));
        Assert.True(rule.Evaluate(Evidence(5, 15), Whole(Evidence(5, 15))));      // touches A only
        Assert.True(rule.Evaluate(Evidence(15, 25), new EffortSpan(0, 1)));       // touches B only
        // Both samples sit in the A–B gap and the segment stays there (no fly-through) → misses both.
        Assert.False(rule.Evaluate(Evidence(15, 18), new EffortSpan(0, 1)));
    }

    [Fact]
    public void PassesAny_PreFilter_PrunesOnlyWhenNoBboxOverlap()
    {
        var rule = new CompiledPassesAny(new[] { 0, 1 }, Boxes(0, 1));
        Assert.Null(rule.PreFilter(new GeoBoundingBox(5, 6, 5, 6)));              // overlaps A → unknown
        Assert.False(rule.PreFilter(new GeoBoundingBox(5, 6, 60, 70)));           // misses both → prune
    }

    // ---------------------------------------------------------------- passesAll

    [Fact]
    public void PassesAll_RequiresEveryZone()
    {
        var rule = new CompiledPassesAll(new[] { 0, 1 }, Boxes(0, 1));
        var e = Evidence(5, 25);
        Assert.True(rule.Evaluate(e, Whole(e)));
        Assert.False(rule.Evaluate(Evidence(5, 15), new EffortSpan(0, 1)));       // B missed
    }

    [Fact]
    public void PassesAll_EvaluatesPerSpan()
    {
        // Track touches A then B — but a span clipped to the first sample only touches A.
        var e = Evidence(5, 25);
        var rule = new CompiledPassesAll(new[] { 0, 1 }, Boxes(0, 1));
        Assert.True(rule.Evaluate(e, new EffortSpan(0, 1)));
        Assert.False(rule.Evaluate(e, new EffortSpan(0, 0)));
    }

    [Fact]
    public void PassesAll_PreFilter_PrunesWhenAnyZoneUnreachable()
    {
        var rule = new CompiledPassesAll(new[] { 0, 1 }, Boxes(0, 1));
        Assert.False(rule.PreFilter(new GeoBoundingBox(5, 6, 5, 6)));             // can't reach B
        Assert.Null(rule.PreFilter(new GeoBoundingBox(0, 10, 0, 30)));            // both reachable
    }

    // ---------------------------------------------------------------- staysWithin

    [Fact]
    public void StaysWithin_TrueOnlyWhenSpanFullyCovered()
    {
        var rule = new CompiledStaysWithin(new[] { 0, 1 }, ZoneA.Bbox, wholeExtent: true);
        var inside = Evidence(2, 5, 8);                        // never leaves A
        Assert.True(rule.Evaluate(inside, Whole(inside)));

        var leaves = Evidence(2, 15, 8);                       // sample 1 outside A and B
        Assert.False(rule.Evaluate(leaves, Whole(leaves)));

        var union = Evidence(5, 25, 5);                        // alternates A/B, always in the union
        Assert.True(rule.Evaluate(union, Whole(union)));
    }

    [Fact]
    public void StaysWithin_PassesOnClippedSpan_FailsWholeTrack()
    {
        // The user's "never leaves during the segment": excursion lies outside the clipped span.
        var e = Evidence(60, 2, 5, 8, 60);                     // outside-A-A-A-outside
        var rule = new CompiledStaysWithin(new[] { 0 }, ZoneA.Bbox, wholeExtent: false);
        Assert.False(rule.Evaluate(e, Whole(e)));
        Assert.True(rule.Evaluate(e, new EffortSpan(1, 3)));
    }

    [Fact]
    public void StaysWithin_PreFilter_PrunesOnlyUnderWholeExtent()
    {
        var escapingBbox = new GeoBoundingBox(0, 10, 0, 60);   // clearly exceeds zone A

        var whole = new CompiledStaysWithin(new[] { 0 }, ZoneA.Bbox, wholeExtent: true);
        Assert.False(whole.PreFilter(escapingBbox));
        Assert.Null(whole.PreFilter(new GeoBoundingBox(2, 8, 2, 8)));

        var clipped = new CompiledStaysWithin(new[] { 0 }, ZoneA.Bbox, wholeExtent: false);
        Assert.Null(clipped.PreFilter(escapingBbox));          // clipped span may still stay within
    }

    // ---------------------------------------------------------------- avoids

    [Fact]
    public void Avoids_FalseWhenAnyZoneTouched()
    {
        var rule = new CompiledAvoids(new[] { 2 });
        var e = Evidence(5, 45);
        Assert.False(rule.Evaluate(e, Whole(e)));
        Assert.True(rule.Evaluate(e, new EffortSpan(0, 0)));   // clipped span misses C
    }

    [Fact]
    public void Avoids_PreFilter_NeverPrunes()
    {
        var rule = new CompiledAvoids(new[] { 2 });
        Assert.Null(rule.PreFilter(new GeoBoundingBox(0, 10, 40, 50)));   // overlap ≠ touch
        Assert.Null(rule.PreFilter(new GeoBoundingBox(0, 10, 60, 70)));   // non-overlap = pass, still no prune
    }
}
