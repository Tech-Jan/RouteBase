using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// End-to-end evaluator behaviour: extent × membership combinations, the compiled Stage-1
/// prefilter contract, and inside-point counting — everything the matcher builds on.
/// </summary>
public class SegmentRuleEvaluatorTests
{
    // Zones on a lng axis (lat fixed 0..10): A=1:[0..10], B=2:[20..30], C=3:[40..50].
    private static ShapeDef Zone(int no, double west, double east) => new()
    {
        No = no,
        Geom = new RectGeom { South = 0, West = west, North = 10, East = east },
    };

    private static RuleDocument Doc(ExtentRule extent, params MembershipRule[] rules) => new()
    {
        Shapes = new() { Zone(1, 0, 10), Zone(2, 20, 30), Zone(3, 40, 50) },
        Extent = extent,
        Membership = rules.ToList(),
    };

    private static TrackEvidence Evidence(SegmentRuleEvaluator ev, params double[] lngs) =>
        TrackEvidenceExtractor.Extract(
            lngs.Select((lng, i) => new TrackSample(100 + i, 5, lng)).ToArray(), ev.Zones);

    private static IReadOnlyList<EffortResult> Run(RuleDocument doc, params double[] lngs)
    {
        var ev = SegmentRuleEvaluator.Compile(doc);
        return ev.Evaluate(Evidence(ev, lngs));
    }

    // ---------------------------------------------------------------- track filters

    [Fact]
    public void Compile_ExposesTrackFilters_EmptyWhenAbsent()
    {
        var ev = SegmentRuleEvaluator.Compile(Doc(new WholeExtent(), new PassesAnyRule { Zones = { 1 } }));
        Assert.Empty(ev.TrackFilters);
    }

    [Fact]
    public void Compile_ExposesTrackFilters_WhenPresent()
    {
        var doc = Doc(new WholeExtent(), new PassesAnyRule { Zones = { 1 } }) with
        {
            TrackFilters = new List<TrackFilter> { new ActivityTypeFilter { Types = { "Run" } } },
        };
        var ev = SegmentRuleEvaluator.Compile(doc);

        var f = Assert.IsType<ActivityTypeFilter>(Assert.Single(ev.TrackFilters));
        Assert.Equal(new[] { "Run" }, f.Types, StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------- extents

    [Fact]
    public void WholeExtent_SpansEntireTrack()
    {
        var doc = Doc(new WholeExtent(), new PassesAnyRule { Zones = { 1 } });
        var efforts = Run(doc, 15, 5, 15, 15);

        var e = Assert.Single(efforts);
        Assert.Equal(new EffortSpan(0, 3), e.Span);
        Assert.Equal(1, e.InsidePoints);
    }

    [Fact]
    public void ZoneSpanExtent_ClipsToFirstAndLastInsideSample()
    {
        var doc = Doc(new ZoneSpanExtent { Zones = { 1, 2 } }, new PassesAnyRule { Zones = { 1 } });
        var efforts = Run(doc, 60, 5, 15, 25, 60);   // inside at idx 1 (A) and 3 (B)

        var e = Assert.Single(efforts);
        Assert.Equal(new EffortSpan(1, 3), e.Span);
        Assert.Equal(2, e.InsidePoints);             // idx 2 is between the zones
    }

    [Fact]
    public void ZoneSpanExtent_NoInsideSample_NoEffort()
    {
        var doc = Doc(new ZoneSpanExtent { Zones = { 1 } }, new PassesAnyRule { Zones = { 1 } });
        Assert.Empty(Run(doc, 60, 70));
    }

    [Fact]
    public void EmptyTrack_NoEffort()
    {
        Assert.Empty(Run(Doc(new WholeExtent()), /* no samples */ Array.Empty<double>()));
    }

    // ---------------------------------------------------------------- extent × membership

    [Fact]
    public void StaysWithin_OnZoneSpan_IgnoresExcursionOutsideSpan()
    {
        // Whole track leaves zone A (samples at lng 60), but the clipped span never does.
        var doc = Doc(new ZoneSpanExtent { Zones = { 1 } }, new StaysWithinRule { Zones = { 1 } });
        Assert.Single(Run(doc, 60, 2, 5, 8, 60));

        // Same rule under whole extent fails.
        var whole = Doc(new WholeExtent(), new StaysWithinRule { Zones = { 1 } });
        Assert.Empty(Run(whole, 60, 2, 5, 8, 60));
    }

    [Fact]
    public void Avoids_KillsEffortTouchingTheZone()
    {
        var doc = Doc(new WholeExtent(),
            new PassesAnyRule { Zones = { 1 } }, new AvoidsRule { Zones = { 3 } });
        Assert.Single(Run(doc, 5, 25));
        Assert.Empty(Run(doc, 5, 45));
    }

    [Fact]
    public void MembershipRules_AreAnded()
    {
        var doc = Doc(new WholeExtent(),
            new PassesAllRule { Zones = { 1, 2 } }, new AvoidsRule { Zones = { 3 } });
        Assert.Single(Run(doc, 5, 25));              // A and B, not C
        Assert.Empty(Run(doc, 5, 15));               // B missed
        Assert.Empty(Run(doc, 5, 25, 45));           // C touched
    }

    // ---------------------------------------------------------------- endpointInZone

    [Fact]
    public void EndpointInZone_Start_MatchesOnlyWhenFirstSampleInside()
    {
        var doc = Doc(new WholeExtent(), new EndpointInZoneRule { Endpoint = Endpoint.Start, Zones = { 1 } });
        Assert.Single(Run(doc, 5, 25, 60));   // first sample in A
        Assert.Empty(Run(doc, 60, 25, 5));     // ends in A but starts outside
    }

    [Fact]
    public void EndpointInZone_Finish_MatchesOnlyWhenLastSampleInside()
    {
        var doc = Doc(new WholeExtent(), new EndpointInZoneRule { Endpoint = Endpoint.Finish, Zones = { 1 } });
        Assert.Single(Run(doc, 60, 25, 5));   // last sample in A
        Assert.Empty(Run(doc, 5, 25, 60));     // starts in A but ends outside
    }

    [Fact]
    public void EndpointInZone_Either_MatchesOnEitherEnd()
    {
        var doc = Doc(new WholeExtent(), new EndpointInZoneRule { Endpoint = Endpoint.Either, Zones = { 1 } });
        Assert.Single(Run(doc, 5, 60, 60));   // starts in A
        Assert.Single(Run(doc, 60, 60, 5));   // ends in A
        Assert.Empty(Run(doc, 60, 5, 60));     // only the middle sample is in A
    }

    [Fact]
    public void EndpointInZone_OrsAcrossZones()
    {
        var doc = Doc(new WholeExtent(), new EndpointInZoneRule { Endpoint = Endpoint.Start, Zones = { 1, 2 } });
        Assert.Single(Run(doc, 25, 60));      // first sample in B (the second listed zone)
        Assert.Empty(Run(doc, 60, 25));
    }

    // ---------------------------------------------------------------- Stage-1 prefilter contract

    private static bool CouldMatch(RuleDocument doc, double west, double east) =>
        SegmentRuleEvaluator.Compile(doc).CouldMatch(new GeoBoundingBox(0, 10, west, east));

    [Fact]
    public void CouldMatch_PassesAny_PrunesOnBboxMiss()
    {
        var doc = Doc(new WholeExtent(), new PassesAnyRule { Zones = { 1, 2 } });
        Assert.True(CouldMatch(doc, 5, 6));
        Assert.False(CouldMatch(doc, 60, 70));
    }

    [Fact]
    public void CouldMatch_EndpointInZone_PrunesOnBboxMiss()
    {
        var doc = Doc(new WholeExtent(), new EndpointInZoneRule { Endpoint = Endpoint.Start, Zones = { 1, 2 } });
        Assert.True(CouldMatch(doc, 5, 6));
        Assert.False(CouldMatch(doc, 60, 70));
    }

    [Fact]
    public void CouldMatch_Avoids_NeverPrunes()
    {
        var doc = Doc(new WholeExtent(), new AvoidsRule { Zones = { 1 } });
        Assert.True(CouldMatch(doc, 0, 10));     // overlapping the avoided zone → still stage-2
        Assert.True(CouldMatch(doc, 60, 70));
    }

    [Fact]
    public void CouldMatch_StaysWithin_PrunesOnlyUnderWholeExtent()
    {
        var whole = Doc(new WholeExtent(), new StaysWithinRule { Zones = { 1 } });
        Assert.False(CouldMatch(whole, 0, 60));  // track bbox exceeds zone A → impossible
        Assert.True(CouldMatch(whole, 2, 8));

        var clipped = Doc(new ZoneSpanExtent { Zones = { 1 } }, new StaysWithinRule { Zones = { 1 } });
        Assert.True(CouldMatch(clipped, 0, 60)); // clipped span may still stay within
    }

    [Fact]
    public void CouldMatch_ZoneSpanExtent_PrunesWhenNoZoneReachable()
    {
        var doc = Doc(new ZoneSpanExtent { Zones = { 1, 2 } });
        Assert.False(CouldMatch(doc, 60, 70));
        Assert.True(CouldMatch(doc, 25, 26));
    }

    // ---------------------------------------------------------------- TryCompile

    [Fact]
    public void TryCompile_RejectsInvalidJson_AndReportsErrors()
    {
        Assert.Null(SegmentRuleEvaluator.TryCompile(null, out var e1));
        Assert.NotEmpty(e1);

        Assert.Null(SegmentRuleEvaluator.TryCompile("{\"version\":99}", out var e2));
        Assert.NotEmpty(e2);

        var ok = SegmentRuleEvaluator.TryCompile(
            Doc(new WholeExtent(), new PassesAnyRule { Zones = { 1 } }).Serialize(), out var e3);
        Assert.NotNull(ok);
        Assert.Empty(e3);
        Assert.Equal(3, ok!.ShapeCount);
    }
}
