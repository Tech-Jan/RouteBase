using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Tests for per-point segment progress (chainage / linear referencing):
/// <see cref="SegmentChainage.BuildSegmentReference"/> and
/// <see cref="SegmentChainage.AssignSegmentProgress"/>.
/// </summary>
public class SegmentProgressTests
{
    private const double Lat = 47.0;

    // A straight west→east segment along constant latitude. lngStep 0.001° ≈ 76 m here.
    private static List<SegmentGpsPoint> StraightSegment(int n, double lngStep = 0.001) =>
        Enumerable.Range(0, n)
            .Select(i => new SegmentGpsPoint { Lat = Lat, Lng = 11.0 + i * lngStep })
            .ToList();

    // Builds parallel GpsSlice + EffortDataPoint lists with cumulative haversine distance,
    // mirroring what BuildPerformancePoints produces.
    private static (List<GpsSlice> slices, List<EffortDataPoint> built) Effort(
        params (double lat, double lng)[] coords)
    {
        var slices = new List<GpsSlice>(coords.Length);
        var built = new List<EffortDataPoint>(coords.Length);
        double cum = 0;
        for (int i = 0; i < coords.Length; i++)
        {
            if (i > 0)
                cum += GeoUtils.HaversineDistance(
                    coords[i - 1].lat, coords[i - 1].lng, coords[i].lat, coords[i].lng);
            slices.Add(new GpsSlice
            {
                GpsId = i, GpsLat = coords[i].lat, GpsLong = coords[i].lng, Distance = cum
            });
            built.Add(new EffortDataPoint
            {
                Distance = Math.Round(cum, 1), Lat = coords[i].lat, Lng = coords[i].lng
            });
        }
        return (slices, built);
    }

    private static double[] Progress(List<EffortDataPoint> built) =>
        built.Select(p => p.SegmentProgress ?? double.NaN).ToArray();

    private static void AssertMonotonic(double[] p)
    {
        for (int i = 1; i < p.Length; i++)
            Assert.True(p[i] >= p[i - 1] - 1e-9,
                $"progress not monotonic at {i}: {p[i - 1]} -> {p[i]}");
    }

    private static void AssertInRange(double[] p)
    {
        foreach (var v in p)
        {
            Assert.False(double.IsNaN(v), "progress was null/NaN");
            Assert.InRange(v, 0.0, 1.0);
        }
    }

    // ----------------------------------------------------------------
    // BuildSegmentReference
    // ----------------------------------------------------------------

    [Fact]
    public void BuildReference_FewerThanTwoPoints_ReturnsNull()
    {
        Assert.Null(SegmentChainage.BuildSegmentReference(new List<SegmentGpsPoint>()));
        Assert.Null(SegmentChainage.BuildSegmentReference(StraightSegment(1)));
    }

    [Fact]
    public void BuildReference_ZeroLength_ReturnsNull()
    {
        var pts = new List<SegmentGpsPoint>
        {
            new() { Lat = Lat, Lng = 11.0 },
            new() { Lat = Lat, Lng = 11.0 },
        };
        Assert.Null(SegmentChainage.BuildSegmentReference(pts));
    }

    [Fact]
    public void BuildReference_TotalLength_MatchesHaversine()
    {
        var pts = StraightSegment(11);
        var segRef = SegmentChainage.BuildSegmentReference(pts)!;
        double expected = GeoUtils.HaversineDistance(Lat, 11.0, Lat, 11.0 + 10 * 0.001);
        Assert.NotNull(segRef);
        Assert.Equal(expected, segRef.TotalLen, 1.0); // within 1 m
    }

    // ----------------------------------------------------------------
    // Straight segment: progress ≈ distance fraction
    // ----------------------------------------------------------------

    [Fact]
    public void StraightSegment_ProgressIsLinearFromZeroToOne()
    {
        var segRef = SegmentChainage.BuildSegmentReference(StraightSegment(11))!;
        // Effort exactly follows the segment vertices.
        var coords = Enumerable.Range(0, 11).Select(i => (Lat, 11.0 + i * 0.001)).ToArray();
        var (slices, built) = Effort(coords);

        SegmentChainage.AssignSegmentProgress(slices, built, segRef);
        var p = Progress(built);

        AssertInRange(p);
        AssertMonotonic(p);
        Assert.Equal(0.0, p[0], 3);
        Assert.Equal(1.0, p[^1], 2);
        // Midpoint of a straight effort should be ~50%.
        Assert.Equal(0.5, p[5], 2);
    }

    // ----------------------------------------------------------------
    // Detour: off-segment points interpolate between anchors (compressed)
    // ----------------------------------------------------------------

    [Fact]
    public void Detour_OffSegmentPointsInterpolateBetweenAnchors()
    {
        var segRef = SegmentChainage.BuildSegmentReference(StraightSegment(11))!;

        // On segment for indices 0..3, detour ~150 m north for 4..6 (advancing east),
        // rejoin at 7, continue on segment 7..10.
        var (slices, built) = Effort(
            (Lat, 11.000), (Lat, 11.001), (Lat, 11.002), (Lat, 11.003),     // on
            (Lat + 0.0014, 11.0035), (Lat + 0.0018, 11.004), (Lat + 0.0014, 11.0045), // detour
            (Lat, 11.005), (Lat, 11.006), (Lat, 11.008), (Lat, 11.010));    // rejoin + on

        SegmentChainage.AssignSegmentProgress(slices, built, segRef);
        var p = Progress(built);

        AssertInRange(p);
        AssertMonotonic(p);

        // Anchors stayed on the segment → their progress tracks chainage fraction.
        Assert.Equal(0.0, p[0], 2);
        Assert.Equal(1.0, p[^1], 2);

        // Detour points (4,5,6) lie strictly between the bounding anchors' progress.
        double before = p[3];
        double after = p[7];
        for (int i = 4; i <= 6; i++)
        {
            Assert.True(p[i] >= before - 1e-9 && p[i] <= after + 1e-9,
                $"detour point {i} ({p[i]}) not between anchors {before}..{after}");
        }
    }

    // ----------------------------------------------------------------
    // Partial effort: enters mid-segment, exits early → sub-range, not 0..1
    // ----------------------------------------------------------------

    [Fact]
    public void PartialEffort_SpansSubRange()
    {
        var segRef = SegmentChainage.BuildSegmentReference(StraightSegment(11))!;
        // Only covers the middle: lng 11.003 .. 11.007 of a 11.000..11.010 segment.
        var coords = new[] { 3, 4, 5, 6, 7 }.Select(i => (Lat, 11.0 + i * 0.001)).ToArray();
        var (slices, built) = Effort(coords);

        SegmentChainage.AssignSegmentProgress(slices, built, segRef);
        var p = Progress(built);

        AssertInRange(p);
        AssertMonotonic(p);
        // Starts well above 0 and ends well below 1 (true chainage, not stretched 0..1).
        Assert.True(p[0] > 0.25, $"expected start ~0.3, got {p[0]}");
        Assert.True(p[^1] < 0.75, $"expected end ~0.7, got {p[^1]}");
        Assert.True(p[0] < 0.4 && p[^1] > 0.6);
    }

    // ----------------------------------------------------------------
    // Loop / self-crossing: progress never jumps backward at the crossing
    // ----------------------------------------------------------------

    [Fact]
    public void LoopSegment_NoBackwardJumpAtCrossing()
    {
        // Figure-eight-ish loop that returns near its own start coordinate.
        var loop = new List<SegmentGpsPoint>
        {
            new() { Lat = Lat,          Lng = 11.000 },
            new() { Lat = Lat + 0.002,  Lng = 11.001 },
            new() { Lat = Lat + 0.002,  Lng = 11.004 },
            new() { Lat = Lat,          Lng = 11.005 },
            new() { Lat = Lat - 0.0005, Lng = 11.002 }, // passes back near the start area
            new() { Lat = Lat + 0.001,  Lng = 11.000 },
        };
        var segRef = SegmentChainage.BuildSegmentReference(loop)!;

        // Effort follows the loop closely.
        var (slices, built) = Effort(
            (Lat, 11.000), (Lat + 0.002, 11.001), (Lat + 0.002, 11.004),
            (Lat, 11.005), (Lat - 0.0005, 11.002), (Lat + 0.001, 11.000));

        SegmentChainage.AssignSegmentProgress(slices, built, segRef);
        var p = Progress(built);

        AssertInRange(p);
        AssertMonotonic(p); // key property: no backward collapse at the self-crossing
        Assert.Equal(0.0, p[0], 2);
    }

    // ----------------------------------------------------------------
    // Shortcut across a bend: the greedy projection used to snap onto a later
    // polyline section and "overtake" (jump to ~100% then flat-line). DTW must
    // keep progress smooth and NOT reach the finish until the effort actually does.
    // ----------------------------------------------------------------

    // An L-shaped segment: east along the first leg, then north along the second.
    // A diagonal shortcut cutting the corner passes near the *inner* elbow, which the
    // old greedy code mistook for the far (later-chainage) leg.
    private static List<SegmentGpsPoint> LSegment()
    {
        var pts = new List<SegmentGpsPoint>();
        for (int i = 0; i <= 5; i++) pts.Add(new() { Lat = Lat, Lng = 11.000 + i * 0.001 });        // east leg
        for (int i = 1; i <= 5; i++) pts.Add(new() { Lat = Lat + i * 0.001, Lng = 11.005 });          // north leg
        return pts;
    }

    [Fact]
    public void ShortcutAcrossBend_DoesNotOvertakeToFinish()
    {
        var segRef = SegmentChainage.BuildSegmentReference(LSegment())!;

        // On the east leg to ~30%, cut the corner diagonally toward the north leg,
        // rejoin partway up it, finish at the north end. The diagonal points sit well
        // off both legs (off-segment) and must be interpolated, not snapped to the finish.
        var (slices, built) = Effort(
            (Lat, 11.000), (Lat, 11.001), (Lat, 11.002), (Lat, 11.003),        // east leg (on)
            (Lat + 0.0006, 11.0037), (Lat + 0.0016, 11.0043), (Lat + 0.0026, 11.0048), // diagonal shortcut (off)
            (Lat + 0.003, 11.005), (Lat + 0.004, 11.005), (Lat + 0.005, 11.005));      // north leg (on) to finish

        SegmentChainage.AssignSegmentProgress(slices, built, segRef);
        var p = Progress(built);

        AssertInRange(p);
        AssertMonotonic(p);
        Assert.Equal(1.0, p[^1], 2);

        // The bug signature: progress hitting ~1.0 early then flat-lining. The corner-cut
        // points (4,5,6) must stay clearly below the finish, i.e. no early overtake.
        for (int i = 4; i <= 6; i++)
            Assert.True(p[i] < 0.9, $"shortcut point {i} overtook to {p[i]} (should stay < 0.9)");
        // And the rejoin should still be climbing, not already parked at 100%.
        Assert.True(p[7] < p[^1], $"rejoin {p[7]} already at finish {p[^1]}");
    }

    [Fact]
    public void ShortcutToFinish_ProgressRisesContinuously()
    {
        var segRef = SegmentChainage.BuildSegmentReference(LSegment())!;

        // On the east leg to ~27%, then a straight shortcut across open ground to the north
        // end (the finish). Progress must rise continuously to 1.0 across the shortcut,
        // never freezing at ~27% with the end marker jumping to the finish.
        var (slices, built) = Effort(
            (Lat, 11.000), (Lat, 11.001), (Lat, 11.002),                       // east leg (on) ~27%
            (Lat + 0.0015, 11.0035), (Lat + 0.003, 11.0045),                   // shortcut (off)
            (Lat + 0.005, 11.005));                                            // finish (on)

        SegmentChainage.AssignSegmentProgress(slices, built, segRef);
        var p = Progress(built);

        AssertInRange(p);
        AssertMonotonic(p);
        Assert.Equal(1.0, p[^1], 2);

        // No frozen vertical line: the shortcut span must make real forward progress, not
        // stall (a stall would leave p[3]≈p[4]≈p[2] then a lone jump at the end).
        Assert.True(p[3] > p[2] + 0.05, $"progress stalled at the shortcut: p2={p[2]} p3={p[3]}");
        Assert.True(p[4] > p[3], $"progress stalled mid-shortcut: p3={p[3]} p4={p[4]}");
    }

    // ----------------------------------------------------------------
    // No-coordinate points inherit previous progress (no crash, monotonic)
    // ----------------------------------------------------------------

    [Fact]
    public void MissingCoordinates_DoNotBreakMonotonicity()
    {
        var segRef = SegmentChainage.BuildSegmentReference(StraightSegment(11))!;
        var (slices, built) = Effort(
            (Lat, 11.000), (Lat, 11.002), (Lat, 11.004), (Lat, 11.006), (Lat, 11.010));

        // Null out a middle point's coordinates.
        slices[2] = new GpsSlice { GpsId = 2, GpsLat = null, GpsLong = null, Distance = slices[2].Distance };

        SegmentChainage.AssignSegmentProgress(slices, built, segRef);
        var p = Progress(built);

        AssertInRange(p);
        AssertMonotonic(p);
    }
}
