using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// The gate/clip half of the single-pass extractor: crossing capture, direction filtering,
/// same-gate debounce, and the Liang–Barsky fly-through that augments zone touches.
/// </summary>
public class GateExtractionTests
{
    // Vertical gate at lng 10, lat 0..10. Tracks run along lat 5.
    private static readonly GeoLine Gate = new(0, 10, 10, 10);

    private static TrackSample[] Track((double lng, double t)[] pts) =>
        pts.Select((p, i) => new TrackSample(i, 5, p.lng, p.t)).ToArray();

    private static IReadOnlyList<GateCrossing> Cross(GeoLine gate, double debounce, params (double lng, double t)[] pts) =>
        TrackEvidenceExtractor.Extract(Track(pts), Array.Empty<IShape>(), new[] { gate }, debounce)
            .GateCrossings[0];

    [Fact]
    public void SingleCrossing_RecordedWithSegIndexAndInterpolatedTime()
    {
        var c = Assert.Single(Cross(Gate, 0, (5, 0), (15, 100)));
        Assert.Equal(0, c.SegIdx);
        Assert.Equal(50, c.TimeSeconds, 3);   // crossing at t=0.5 between times 0 and 100
    }

    [Fact]
    public void DirectionConstraint_Forward_AllowsWestwardCrossing_BlocksEastward()
    {
        // Independent ground truth — NOT read back from TryCross's own sign (which would make an inverted
        // convention self-consistent and invisible). The gate is the directed segment (lat0,lng10)→(lat10,lng10),
        // i.e. pointing NORTH. Its normal is the gate rotated +90° (per GeoLine's contract), which points WEST
        // (−longitude). "Forward" = a crossing whose velocity has a positive component along that normal, so the
        // Forward-allowed track is the one moving east→west (decreasing longitude).
        var fwd = new GeoLine(0, 10, 10, 10, CrossDirection.Forward);

        Assert.Single(Cross(fwd, 0, (15, 0), (5, 100)));   // east → west: with the west-pointing normal → allowed
        Assert.Empty(Cross(fwd, 0, (5, 0), (15, 100)));    // west → east: against the normal → blocked
    }

    [Fact]
    public void DirectionConstraint_Backward_IsTheExactMirror()
    {
        // Backward inverts the gate's constraint: the east→west crossing Forward allowed is now blocked, and
        // the west→east crossing is admitted. Pinning both directions catches a flipped sign convention.
        var bwd = new GeoLine(0, 10, 10, 10, CrossDirection.Backward);

        Assert.Empty(Cross(bwd, 0, (15, 0), (5, 100)));    // east → west: now blocked
        Assert.Single(Cross(bwd, 0, (5, 0), (15, 100)));   // west → east: now allowed
    }

    [Fact]
    public void Debounce_CollapsesSameGateCrossingsInsideTheWindow()
    {
        // Crossings at t=5, 15, 25. With a 15 s window the middle one is dropped (15-5 < 15),
        // the last kept (25-5 ≥ 15).
        var crossings = Cross(Gate, 15, (5, 0), (15, 10), (5, 20), (15, 30));
        Assert.Equal(new[] { 0, 2 }, crossings.Select(c => c.SegIdx).ToArray());
    }

    [Fact]
    public void Debounce_Zero_KeepsEveryCrossing()
    {
        var crossings = Cross(Gate, 0, (5, 0), (15, 10), (5, 20));
        Assert.Equal(2, crossings.Count);
    }

    [Fact]
    public void Debounce_IsSkipped_WhenTheTrackCarriesNoTime()
    {
        // A track with no time axis reports 0 for every sample (ShapeSegmentMatcher maps NaN → 0).
        // Debouncing against a flat time axis would read every crossing as simultaneous and keep only
        // the first — a lap gate would then produce one crossing and therefore zero efforts.
        var crossings = Cross(Gate, 15, (5, 0), (15, 0), (5, 0), (15, 0));
        Assert.Equal(new[] { 0, 1, 2 }, crossings.Select(c => c.SegIdx).ToArray());
    }

    [Fact]
    public void ZoneFlyThrough_CountsAsTouch_WithoutAPointVisit()
    {
        // Thin zone the track straddles in one segment (both samples outside it).
        var slab = new GeoRect(0, 10, 10, 12);
        var evidence = TrackEvidenceExtractor.Extract(
            new[] { new TrackSample(0, 5, 8), new TrackSample(1, 5, 15) }, new IShape[] { slab });

        Assert.Empty(evidence.ZoneVisits[0]);                       // never sampled inside
        Assert.Equal(new[] { 0 }, evidence.ZoneClipSegments[0]);    // but the edge clips it
        Assert.True(evidence.ZoneTouches(0, new EffortSpan(0, 1))); // → touched over the span
        Assert.False(evidence.ZoneTouches(0, new EffortSpan(1, 1)));// segment 0 not inside [1,1]
    }
}
