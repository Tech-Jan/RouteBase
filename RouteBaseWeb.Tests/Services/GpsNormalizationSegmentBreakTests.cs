using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Discontinuity handling in <see cref="GpsNormalizationService"/>: a recording gap or a merge seam is a
/// real elevation STEP, not a spike. Regression fixture for the latch bug — a step used to be clamped to
/// the spike EMA, and because the clamped value was fed back (ema = a·ema + (1−a)·ema = ema) the reference
/// froze, flattening every later point to that constant for the rest of the ride. Seen live on a merged
/// ride: 2129 points pinned at 1085 m while the real profile descended 966 → 434 m.
/// </summary>
public class GpsNormalizationSegmentBreakTests
{
    private readonly GpsNormalizationService _sut = new();

    private const double Lat = 47.0;
    private const double MetresPerDegreeLng = 75930.0;   // at 47° N
    private const int LegPoints = 60;
    private const double StepMeters = 10.0;              // point spacing along the leg

    /// <summary>A straight eastward leg: <paramref name="n"/> points, 10 m and 1 s apart, altitude ramping
    /// linearly from <paramref name="altFrom"/> to <paramref name="altTo"/>.</summary>
    private static List<GpsData> Leg(double altFrom, double altTo, double lngStart, int secStart, int n = LegPoints)
        => Enumerable.Range(0, n).Select(i => new GpsData
        {
            GpsLat = Lat,
            GpsLong = lngStart + i * StepMeters / MetresPerDegreeLng,
            Altitude = altFrom + (altTo - altFrom) * i / (n - 1),
            DateTimeUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc).AddSeconds(secStart + i),
        }).ToList();

    private static double LngAfter(List<GpsData> leg, double gapMetres)
        => leg[^1].GpsLong!.Value + gapMetres / MetresPerDegreeLng;

    [Fact]
    public void SmoothAltitudes_StepAcrossRecordingGap_DoesNotFlattenTheRest()
    {
        // Climb to 1080 m, stop recording, resume 1 km away and 2 h later at 1280 m, then descend to 1200 m.
        var pts = Leg(1000, 1080, lngStart: 0, secStart: 0);
        var after = Leg(1280, 1200, LngAfter(pts, 1000), secStart: 7200);
        pts.AddRange(after);

        _sut.SmoothAltitudes(pts);

        var tail = pts.Skip(LegPoints).Select(p => p.Altitude!.Value).ToList();
        // The 200 m step is real terrain — the tail must sit at its own altitude, not be dragged to ~1080.
        Assert.InRange(tail[0], 1270, 1290);
        Assert.InRange(tail[^1], 1190, 1210);
        // And it must still be a descent, not a frozen constant.
        Assert.True(tail[0] - tail[^1] > 70, $"tail flat-lined: {tail[0]:F1} → {tail[^1]:F1}");
    }

    [Fact]
    public void SmoothAltitudes_DescentAwayFromTheFrozenValue_StillTracked()
    {
        // The shape that made it visible: after the seam the ride only ever descends, so a frozen spike
        // reference could never re-acquire it and the profile stayed pinned to the end of the activity.
        var pts = Leg(1000, 1080, lngStart: 0, secStart: 0);
        pts.AddRange(Leg(960, 440, LngAfter(pts, 1600), secStart: 7200));

        _sut.SmoothAltitudes(pts);

        var tail = pts.Skip(LegPoints).Select(p => p.Altitude!.Value).ToList();
        Assert.InRange(tail[^1], 430, 455);                     // reached the valley, not stuck at 1080
        Assert.True(tail.Distinct().Count() > LegPoints / 2);   // a real profile, not a constant
    }

    [Fact]
    public void SmoothAltitudes_SustainedStepWithinOneSegment_IsAcceptedNotClamped()
    {
        // No gap here (continuous track), but a >30 m level change its successors confirm — a DEM cliff or
        // a tunnel exit. It is terrain, so the reference re-anchors instead of clamping point after point.
        // Legs are long enough (800 m) that the 70 m gain-path curvature smoother settles clear of the
        // step. The band is a "did it reach the upper level at all" guard, not a smoother pin — the
        // retune 105 → 70 moved the settled value 1099.37 → 1100.03, i.e. toward nominal, not out of band.
        var pts = Leg(1000, 1000, lngStart: 0, secStart: 0, n: 80);
        pts.AddRange(Leg(1100, 1100, LngAfter(pts, StepMeters), secStart: 80, n: 80));

        _sut.SmoothAltitudes(pts);

        Assert.InRange(pts[^1].Altitude!.Value, 1090, 1110);   // settled on the upper level, not clamped to 1000
    }

    [Fact]
    public void ComputeStats_ElevationStepAcrossGap_IsNotCountedAsAscent()
    {
        // You did not climb the seam — you were somewhere else. A merge seam banked +195 m of phantom ascent.
        var pts = Leg(1000, 1000, lngStart: 0, secStart: 0);
        pts.AddRange(Leg(1200, 1200, LngAfter(pts, 1000), secStart: 7200));

        var stats = _sut.ComputeStats(pts);

        Assert.True(stats.TotalAscentMeters < 5.0,
            $"the {pts[LegPoints].Altitude - pts[LegPoints - 1].Altitude:F0} m step across the gap was banked as ascent " +
            $"({stats.TotalAscentMeters:F0} m)");
    }

    // A flat, speed-bearing leg for the moving-time assertions: n points 10 m and 1 s apart from secStart,
    // moving at `speed` m/s (> the 0.5 m/s moving threshold).
    private static List<GpsData> MovingLeg(int secStart, int n, double lngStart = 0, double speed = 3.0)
        => Enumerable.Range(0, n).Select(i => new GpsData
        {
            GpsLat = Lat,
            GpsLong = lngStart + i * StepMeters / MetresPerDegreeLng,
            Altitude = 1000.0,
            Speed = speed,
            DateTimeUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc).AddSeconds(secStart + i),
        }).ToList();

    [Fact]
    public void ComputeStats_MovingTimeAcrossBreak_IsNotBanked()
    {
        // SVC-01: the full inter-point gap across a break (pause / merge seam / multi-day stitch) must not be
        // banked as moving time, exactly as the ascent step across it is dropped. Two moving legs 1 h apart —
        // the second point-after-the-gap still reads "moving", so the pre-fix code banked the whole 3600 s.
        var leg1 = MovingLeg(secStart: 0, n: 10);
        var pts = new List<GpsData>(leg1);
        pts.AddRange(MovingLeg(secStart: 3600, n: 10, lngStart: LngAfter(leg1, 20)));

        var stats = _sut.ComputeStats(pts);

        // Only the two legs' own 9 s each is legitimate movement (~18 s); never the ~3618 s that banking the
        // 1-hour idle gap produced.
        Assert.True(stats.TimeMovingSeconds < 60,
            $"the 3600 s gap across the break was banked as moving time ({stats.TimeMovingSeconds:F0} s)");
        Assert.True(stats.TimeMovingSeconds > 10,
            $"the legs' own moving time was dropped ({stats.TimeMovingSeconds:F0} s)");
    }

    [Fact]
    public void ComputeStats_MovingTimeAcrossSubBreakGap_IsStillCounted()
    {
        // The inverse guard: a gap that is NOT a break (≤ 60 s AND ≤ 150 m) must still bank its moving time —
        // the !isBreak change must not drop legitimate intervals. One 30 s stroll gap, ~50 m on, still moving.
        var pts = MovingLeg(secStart: 0, n: 5);   // 4 s of moving time within the leg (points at s 0..4)
        pts.Add(new GpsData
        {
            GpsLat = Lat,
            GpsLong = LngAfter(pts, 50),           // ~50 m past the leg's end — under the 150 m break threshold
            Altitude = 1000.0,
            Speed = 3.0,
            DateTimeUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc).AddSeconds(34), // 30 s after s=4
        });

        var stats = _sut.ComputeStats(pts);

        // 4 s (leg) + the 30 s sub-break crossing = ~34 s: the non-break gap is banked, as before.
        Assert.True(stats.TimeMovingSeconds > 30,
            $"a non-break moving gap was wrongly dropped ({stats.TimeMovingSeconds:F0} s)");
    }
}
