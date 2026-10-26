using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// Direct unit tests for the compiled membership rules. Two behaviours had no assertions before the mutation-testing pass:
/// the three-valued gate pre-filters (overlap → unknown / no-overlap → prune) and the interval-sweep
/// <c>SpanCoverage.Covers</c> that backs "stays within", exercised here through
/// <see cref="CompiledStaysWithin"/>.
///
/// The pre-filter contract is load-bearing: it must return <c>false</c> ONLY when a track can never satisfy
/// the rule (over-approximation is safe, a wrong prune drops a real match). A boolean/negation mutation
/// there silently loses efforts.
/// </summary>
public class MembershipRulesTests
{
    private static readonly GeoBoundingBox Near = new(46.9, 47.1, 10.9, 11.1);
    private static readonly GeoBoundingBox Far  = new(80.0, 81.0, 170.0, 171.0);
    private static readonly GeoBoundingBox Track = new(47.0, 47.05, 11.0, 11.05);

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

    // ---------------------------------------------------------------- StartsAtGate pre-filter (134, 135)

    [Fact]
    public void StartsAtGate_PreFilter_GateReachable_IsUnknown()
    {
        var rule = new CompiledStartsAtGate(new[] { 0 }, new[] { Near });
        Assert.Null(rule.PreFilter(Track));           // overlap → null (mut 134 would return false)
    }

    [Fact]
    public void StartsAtGate_PreFilter_NoGateReachable_Prunes()
    {
        var rule = new CompiledStartsAtGate(new[] { 0 }, new[] { Far });
        Assert.Equal((bool?)false, rule.PreFilter(Track));   // no overlap → false (mut 135 would return true)
    }

    // ---------------------------------------------------------------- EndsAtGate pre-filter (162, 163)

    [Fact]
    public void EndsAtGate_PreFilter_GateReachable_IsUnknown()
    {
        var rule = new CompiledEndsAtGate(new[] { 0 }, new[] { Near });
        Assert.Null(rule.PreFilter(Track));
    }

    [Fact]
    public void EndsAtGate_PreFilter_NoGateReachable_Prunes()
    {
        var rule = new CompiledEndsAtGate(new[] { 0 }, new[] { Far });
        Assert.Equal((bool?)false, rule.PreFilter(Track));
    }

    // ---------------------------------------------------------------- StaysWithin / SpanCoverage.Covers

    private static CompiledStaysWithin StaysWithinZone0() =>
        new(new[] { 0 }, Near, wholeExtent: false);

    [Fact]
    public void StaysWithin_ContiguousVisitCoveringWholeSpan_Passes()
    {
        var rule = StaysWithinZone0();
        var ev   = ZoneEvidence(10, new[] { new ZoneVisit(0, 5) });
        Assert.True(rule.Evaluate(ev, new EffortSpan(0, 5)));
    }

    [Fact]
    public void StaysWithin_AdjacentVisitsWithNoGap_Passes()
    {
        // [0..2] then [3..5] — the second visit starts at covered+1, so there is no gap.
        var rule = StaysWithinZone0();
        var ev   = ZoneEvidence(10, new[] { new ZoneVisit(0, 2), new ZoneVisit(3, 5) });
        Assert.True(rule.Evaluate(ev, new EffortSpan(0, 5)));
    }

    [Fact]
    public void StaysWithin_GapBetweenVisits_Fails()
    {
        // [0..1] then [3..5] leaves sample 2 uncovered → the effort leaves the zone union.
        var rule = StaysWithinZone0();
        var ev   = ZoneEvidence(10, new[] { new ZoneVisit(0, 1), new ZoneVisit(3, 5) });
        Assert.False(rule.Evaluate(ev, new EffortSpan(0, 5)));
    }

    // Mut 224 (final `covered >= span.ExitIdx` → `covered < span.ExitIdx`): coverage that stops SHORT of the
    // span end, with no trailing gap to trip the early return, must FAIL. The mutant flips the tail verdict
    // to true. Visit [0..3] covers a [0..5] span only up to 3.
    [Fact]
    public void StaysWithin_CoverageStopsShortOfEnd_Fails()
    {
        var rule = StaysWithinZone0();
        var ev   = ZoneEvidence(10, new[] { new ZoneVisit(0, 3) });
        Assert.False(rule.Evaluate(ev, new EffortSpan(0, 5)));
    }

    [Fact]
    public void StaysWithin_LeadingGapBeforeSpan_Fails()
    {
        // First covered sample is span.EntryIdx+1, so sample 1 (the entry) is already outside.
        var rule = StaysWithinZone0();
        var ev   = ZoneEvidence(10, new[] { new ZoneVisit(2, 5) });
        Assert.False(rule.Evaluate(ev, new EffortSpan(1, 5)));
    }

    // ---------------------------------------------------------------- Evaluate sanity (touch / avoid / endpoint)

    [Fact]
    public void PassesAll_MissingOneZone_Fails()
    {
        var rule = new CompiledPassesAll(new[] { 0, 1 }, new[] { Near, Near });
        // Zone 1 is never visited inside the span → fails; zone 0 alone would pass PassesAny.
        var ev   = ZoneEvidence(10, new[] { new ZoneVisit(1, 3) }, Array.Empty<ZoneVisit>());
        Assert.False(rule.Evaluate(ev, new EffortSpan(0, 5)));
        Assert.True(new CompiledPassesAny(new[] { 0, 1 }, new[] { Near, Near })
            .Evaluate(ev, new EffortSpan(0, 5)));
    }

    [Fact]
    public void Avoids_ZoneTouchedInsideSpan_Fails()
    {
        var rule = new CompiledAvoids(new[] { 0 });
        var ev   = ZoneEvidence(10, new[] { new ZoneVisit(2, 3) });
        Assert.False(rule.Evaluate(ev, new EffortSpan(0, 5)));
        Assert.True(rule.Evaluate(ev, new EffortSpan(6, 9)));   // zone not touched in this span → avoided
    }

    [Fact]
    public void EndpointInZone_Start_OnlyEntrySampleQualifies()
    {
        var rule = new CompiledEndpointInZone(new[] { 0 }, new[] { Near }, Endpoint.Start);
        var ev   = ZoneEvidence(10, new[] { new ZoneVisit(0, 1) });   // entry sample 0 inside
        Assert.True(rule.Evaluate(ev, new EffortSpan(0, 5)));
        Assert.False(rule.Evaluate(ev, new EffortSpan(3, 5)));        // entry sample 3 not inside
    }
}
