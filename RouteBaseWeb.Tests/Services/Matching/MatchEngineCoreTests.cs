using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Matching;

/// <summary>
/// Pins MatchEngineCore's classify / grouping / effort-distance / gate semantics (TE-09,
/// S4–S7) against the SQL catalog, bug-for-bug. Pure (no DB).
/// </summary>
public class MatchEngineCoreTests
{
    // The matcher is told which activity these points belong to; the decode itself carries no id.
    private const int TrackId = 1;

    // Ordinal ≡ GPS_ID post re-key: place each fixture point AT its id ordinal, NaN-pad the gaps.
    // NaN pads are inert in every code path (Match skips NaN coords, gap arithmetic sees the same
    // gpsId values, EffortDistance skips NaN samples).
    private static DecodedTrack Track(int[] ids, (double lat, double lng)[] pts, double[]? dist = null)
    {
        int n = ids[^1] + 1;                                  // ids ascending in every fixture
        double[] lat = Filled(n), lng = Filled(n), time = Filled(n);
        double[]? d = dist is null ? null : Filled(n);
        for (int k = 0; k < ids.Length; k++)
        {
            lat[ids[k]] = pts[k].lat;
            lng[ids[k]] = pts[k].lng;
            if (dist != null) d![ids[k]] = dist[k];
        }
        return DecodedTrackExtensions.FromDoubleChannels(time, lat, lng, null, d, null, null, null, null, null);
    }

    private static double[] Filled(int n)
    {
        var a = new double[n];
        Array.Fill(a, double.NaN);
        return a;
    }

    private static SegmentGeometry Seg(int[] segIds, (double lat, double lng)[] pts)
    {
        var lat = new double[pts.Length];
        var lng = new double[pts.Length];
        for (int i = 0; i < pts.Length; i++) { lat[i] = pts[i].lat; lng[i] = pts[i].lng; }
        return SegmentGeometry.Create(new SegmentId(1), segIds, lat, lng);
    }

    // A short (n<100) segment of 'count' points, lat 47.000 + j*0.001, lng 11.000.
    private static (int[] ids, (double, double)[] pts) LinearSeg(int count, double startLat = 47.000)
    {
        var ids = new int[count];
        var pts = new (double, double)[count];
        for (int j = 0; j < count; j++) { ids[j] = j + 1; pts[j] = (startLat + j * 0.001, 11.000); }
        return (ids, pts);
    }

    // -------------------------------------------------------------------------
    // Classification (S4.1 priority, S4.2 strict middle)
    // -------------------------------------------------------------------------

    [Fact]
    public void Classify_TightBeatsShort_InZoneWithinBothTolerances()
    {
        var (segIds, segPts) = LinearSeg(8); // start zone = ids 1..2
        var seg = Seg(segIds, segPts);

        // Track point exactly on start-pin (id1) → qualifies for BOTH tight and short.
        var track = Track(new[] { 10 }, new[] { (47.000, 11.000) });
        var crossing = Assert.Single(MatchEngineCore.Match(seg, track, new TrackId(TrackId)));

        // start_end_tight (checked first) fired → it contributes a zone hit; short_standard would not.
        Assert.Equal(1, crossing.StartZoneHits);
        Assert.Equal(1, crossing.MatchedSegPoints);
    }

    [Fact]
    public void Classify_MiddleBoundaryPoint_ExcludedFromStrictMiddle()
    {
        // Medium segment: n=150 → StartZoneEnd = 31. Boundary seg_GPS_ID 31 (ordinal 30).
        var (segIds, segPts) = LinearSeg(150);
        var seg = Seg(segIds, segPts);
        Assert.Equal(31, seg.StartZoneEnd); // guard the fixture

        // Two track points, each offset +0.0002 (fails tight <0.00015, passes medium <0.0003):
        //   47.0302 over the boundary point id31 → tight zone-test true but tolerance fails,
        //           middle branch needs seg_GPS_ID > 31 (strict) → excluded, matches nothing.
        //   47.0312 over the interior point id32 → middle branch matches.
        var track = Track(new[] { 10, 11 }, new[] { (47.0302, 11.000), (47.0312, 11.000) });
        var crossing = Assert.Single(MatchEngineCore.Match(seg, track, new TrackId(TrackId)));

        Assert.Equal(1, crossing.MatchedSegPoints); // only the interior id32, boundary excluded
    }

    // -------------------------------------------------------------------------
    // Crossing grouping (S5.1 strict '>', S5.2 duplicate-GPS_ID)
    // -------------------------------------------------------------------------

    [Fact]
    public void Grouping_GapAboveThreshold_SplitsWindow()
    {
        var (segIds, segPts) = LinearSeg(8); // GapThreshold = 300
        var seg = Seg(segIds, segPts);

        // Two hits on the start pin, GPS_ID gap 301 (> 300) → two windows.
        var track = Track(new[] { 10, 311 }, new[] { (47.000, 11.000), (47.000, 11.000) });
        Assert.Equal(2, MatchEngineCore.Match(seg, track, new TrackId(TrackId)).Count);
    }

    [Fact]
    public void Grouping_GapEqualToThreshold_DoesNotSplit()
    {
        var (segIds, segPts) = LinearSeg(8);
        var seg = Seg(segIds, segPts);

        // Gap exactly 300 (not > 300) → one window.
        var track = Track(new[] { 10, 310 }, new[] { (47.000, 11.000), (47.000, 11.000) });
        Assert.Single(MatchEngineCore.Match(seg, track, new TrackId(TrackId)));
    }

    [Fact]
    public void Grouping_DuplicateGpsIdEvents_StayInOneWindow()
    {
        // Two segment points 0.0001 apart share a geo-cell; one track point matches both →
        // two events for one GPS_ID → one window, two distinct seg points.
        var segIds = new[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var segPts = new (double, double)[]
        {
            (47.0000, 11.000), (47.0001, 11.000), (47.002, 11.000), (47.003, 11.000),
            (47.004, 11.000),  (47.005, 11.000),  (47.006, 11.000), (47.007, 11.000),
        };
        var seg = Seg(segIds, segPts);

        var track = Track(new[] { 10 }, new[] { (47.00005, 11.000) });
        var crossing = Assert.Single(MatchEngineCore.Match(seg, track, new TrackId(TrackId)));
        Assert.Equal(2, crossing.MatchedSegPoints);
    }

    // -------------------------------------------------------------------------
    // Effort distance (S6.1) and match_perc (S6.2)
    // -------------------------------------------------------------------------

    [Fact]
    public void EffortDist_MaxMinusMin_NotLastMinusFirst()
    {
        var (segIds, segPts) = LinearSeg(8);
        var seg = Seg(segIds, segPts);

        // Out-and-back cumulative distance over the window: 0 → 100 → 50. MAX−MIN = 100, not 50.
        var track = Track(
            new[] { 1, 2, 3 },
            new[] { (47.000, 11.000), (47.000, 11.000), (47.000, 11.000) },
            dist: new[] { 0.0, 100.0, 50.0 });

        var crossing = Assert.Single(MatchEngineCore.Match(seg, track, new TrackId(TrackId)));
        Assert.Equal(100.0, crossing.EffortDistM);
    }

    [Fact]
    public void EffortDist_NoDistanceChannel_ReturnsMinus1()
    {
        var (segIds, segPts) = LinearSeg(8);
        var seg = Seg(segIds, segPts);

        var track = Track(new[] { 1 }, new[] { (47.000, 11.000) }); // no distance channel
        var crossing = Assert.Single(MatchEngineCore.Match(seg, track, new TrackId(TrackId)));
        Assert.Equal(-1, crossing.EffortDistM);
    }

    [Fact]
    public void MatchPerc_Truncates_87_5_To_87()
    {
        var (segIds, segPts) = LinearSeg(8);
        var seg = Seg(segIds, segPts);

        // 7 of 8 seg points matched (skip lat 47.003) → 7*100/8 = 87.5 → 87.
        var track = Track(
            new[] { 1, 2, 3, 4, 5, 6, 7 },
            new[]
            {
                (47.000, 11.000), (47.001, 11.000), (47.002, 11.000), (47.004, 11.000),
                (47.005, 11.000), (47.006, 11.000), (47.007, 11.000),
            });

        var crossing = Assert.Single(MatchEngineCore.Match(seg, track, new TrackId(TrackId)));
        Assert.Equal(7, crossing.MatchedSegPoints);
        Assert.Equal(87, crossing.MatchPerc);
    }

    // -------------------------------------------------------------------------
    // INSERT gate (S7.1)
    // -------------------------------------------------------------------------

    private static QualifiedCrossing Qc(
        int total, int startHits = 1, int endHits = 1, int perc = 50,
        int entry = 0, int exit = 100, double effort = -1) =>
        new(1, 1, entry, exit, MatchedSegPoints: 1, total, perc, effort, startHits, endHits);

    [Fact]
    public void Gate_SpanUsesGpsIdUnits_NonContiguousIds()
    {
        // Span is exit − entry in GPS_ID units, not the point count. ids 10..800 → span 790.
        Assert.True(MatchEngineCore.PassesGate(Qc(total: 8, entry: 10, exit: 800), null, 40));
        // Same shape but a tiny GPS_ID span (< min 5) fails the span gate.
        Assert.False(MatchEngineCore.PassesGate(Qc(total: 8, entry: 10, exit: 14), null, 40));
    }

    [Fact]
    public void Gate_ThresholdInclusive()
    {
        Assert.True(MatchEngineCore.PassesGate(Qc(total: 8, perc: 40), null, 40));  // >= inclusive
        Assert.False(MatchEngineCore.PassesGate(Qc(total: 8, perc: 40), null, 41));
    }

    [Fact]
    public void Gate_NullSegmentDistance_Passes()
    {
        // Small effort distance passes when segment distance is unknown (null)…
        Assert.True(MatchEngineCore.PassesGate(Qc(total: 8, effort: 5), null, 40));
        // …but is rejected against a known segment distance (5 < 60% of 1000).
        Assert.False(MatchEngineCore.PassesGate(Qc(total: 8, effort: 5), 1000, 40));
    }

    [Fact]
    public void Gate_ZoneHits_Under100Needs1_Over100Needs2()
    {
        Assert.True(MatchEngineCore.PassesGate(Qc(total: 99, startHits: 1, endHits: 1), null, 40));
        Assert.False(MatchEngineCore.PassesGate(Qc(total: 100, startHits: 1, endHits: 1), null, 40));
        Assert.True(MatchEngineCore.PassesGate(Qc(total: 100, startHits: 2, endHits: 2), null, 40));
    }
}
