using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// The single-pass GPS → evidence extraction: zone visit intervals, including the
/// track-starts-inside and still-inside-at-the-end edges.
/// </summary>
public class TrackEvidenceExtractorTests
{
    // Zone covering lat 10..20, lng 10..20. Points use lat as the moving coordinate, lng fixed 15.
    private static readonly IShape Zone = new GeoRect(10, 20, 10, 20);

    private static TrackSample[] Track(params double[] lats) =>
        lats.Select((lat, i) => new TrackSample(GpsId: 100 + i, lat, 15)).ToArray();

    private static IReadOnlyList<ZoneVisit> Visits(params double[] lats) =>
        TrackEvidenceExtractor.Extract(Track(lats), new[] { Zone }).ZoneVisits[0];

    [Fact]
    public void PassThrough_YieldsOneVisit()
    {
        var visits = Visits(0, 5, 12, 18, 25, 30);   // enters at idx 2, leaves after idx 3
        Assert.Equal(new[] { new ZoneVisit(2, 3) }, visits);
    }

    [Fact]
    public void StartsInside_VisitOpensAtIndexZero()
    {
        var visits = Visits(15, 18, 25);
        Assert.Equal(new[] { new ZoneVisit(0, 1) }, visits);
    }

    [Fact]
    public void EndsInside_VisitClosesAtLastSample()
    {
        var visits = Visits(0, 15, 18);
        Assert.Equal(new[] { new ZoneVisit(1, 2) }, visits);
    }

    [Fact]
    public void MultipleVisits_AreSeparateIntervals()
    {
        var visits = Visits(15, 25, 15, 25, 15);     // in-out-in-out-in
        Assert.Equal(new[] { new ZoneVisit(0, 0), new ZoneVisit(2, 2), new ZoneVisit(4, 4) }, visits);
    }

    [Fact]
    public void NeverInside_NoVisits()
    {
        Assert.Empty(Visits(0, 5, 25, 30));
    }

    [Fact]
    public void EmptyTrack_NoVisits()
    {
        Assert.Empty(Visits());
    }

    [Fact]
    public void MultipleZones_TrackedIndependently()
    {
        var zoneA = new GeoRect(10, 20, 10, 20);
        var zoneB = new GeoRect(0, 5, 10, 20);
        var e = TrackEvidenceExtractor.Extract(Track(2, 15, 2), new IShape[] { zoneA, zoneB });

        Assert.Equal(new[] { new ZoneVisit(1, 1) }, e.ZoneVisits[0]);
        Assert.Equal(new[] { new ZoneVisit(0, 0), new ZoneVisit(2, 2) }, e.ZoneVisits[1]);
    }

    [Fact]
    public void ZoneTouches_RespectsSpanBounds()
    {
        var e = TrackEvidenceExtractor.Extract(Track(15, 25, 25, 25), new[] { Zone });

        Assert.True(e.ZoneTouches(0, new EffortSpan(0, 3)));
        Assert.False(e.ZoneTouches(0, new EffortSpan(1, 3)));   // visit [0,0] outside span
    }
}
