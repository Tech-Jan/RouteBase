using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Elevation;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// What <see cref="GpsNormalizationService.SmoothAltitudes"/> promises about WHICH smoother it runs.
/// The choice is per call site, not a shared default: gain wants
/// <see cref="ElevationProfileSmoother.GainSmoothLengthMeters"/> (70 m), while the race grade profile
/// keeps the 105 m default. A revert to the default here is invisible in every band-style assertion —
/// the profile still looks right, the ascent is just ~5 % low again — so it gets an exact pin.
/// </summary>
public class GpsNormalizationSmoothingContractTests
{
    private const double Lat = 47.0;
    private const double MetresPerDegreeLng = 75930.0;   // at 47° N

    /// <summary>A continuous climb-then-descend leg with DEM-like noise: one segment, no breaks,
    /// no spikes (every deviation is far under the 30 m spike gate).</summary>
    internal static List<GpsData> HillLeg(int n = 300, double stepMeters = 10.0, int seed = 7)
    {
        var rng = new Random(seed);
        var t0 = new DateTime(2025, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, n).Select(i =>
        {
            double f = (double)i / (n - 1);
            double hill = 400.0 + 120.0 * Math.Sin(Math.PI * f) + 25.0 * Math.Sin(6.0 * Math.PI * f);
            return new GpsData
            {
                GpsLat = Lat,
                GpsLong = i * stepMeters / MetresPerDegreeLng,
                Altitude = hill + (rng.NextDouble() - 0.5) * 6.0,
                DateTimeUtc = t0.AddSeconds(i * 3),
            };
        }).ToList();
    }

    /// <summary>Cumulative arc length over a leg's coordinates — what the service feeds the smoother.</summary>
    internal static double[] ArcOf(IReadOnlyList<GpsData> points)
    {
        var arc = new double[points.Count];
        for (int i = 1; i < points.Count; i++)
            arc[i] = arc[i - 1] + GeoUtils.HaversineDistance(
                points[i - 1].GpsLat!.Value, points[i - 1].GpsLong!.Value,
                points[i].GpsLat!.Value, points[i].GpsLong!.Value);
        return arc;
    }

    [Fact]
    public void GainLength_IsNotTheSharedDefault()
    {
        // The two production call sites must stay separable; equal values would mean someone collapsed
        // them back onto one constant and the race grade path silently inherited the gain retune.
        Assert.Equal(70.0, ElevationProfileSmoother.GainSmoothLengthMeters);
        Assert.Equal(105.0, ElevationProfileSmoother.DefaultSmoothLengthMeters);
    }

    [Fact]
    public void SmoothAltitudes_UsesTheGainLength_NotTheDefault()
    {
        var points = HillLeg();
        var arc = ArcOf(points);
        var alt = points.Select(p => p.Altitude!.Value).ToArray();

        var atGainLength = ElevationProfileSmoother.SmoothByArcLength(
            arc, alt, ElevationProfileSmoother.GainSmoothLengthMeters);
        var atDefault = ElevationProfileSmoother.SmoothByArcLength(arc, alt);

        new GpsNormalizationService().SmoothAltitudes(points);
        var produced = points.Select(p => p.Altitude!.Value).ToArray();

        Assert.Equal(atGainLength, produced);
        Assert.NotEqual(atDefault, produced);
    }

    // ── the barometer-less contract (B3) ──────────────────────────────────────────────────────
    //
    // "Some users or recordings won't have a barometer, I still want the code to work then and give
    // good results like I have right now." These pin exactly that, with Assert.Equal on the array
    // rather than a tolerance — the defect this guards was invisible to tolerance-based tests once.

    /// <summary>The elevations a leg gets when no barometer is involved at all.</summary>
    private static double[] DemOnlyResult(List<GpsData> points)
    {
        var copy = points.Select(p => new GpsData
        {
            GpsLat = p.GpsLat, GpsLong = p.GpsLong, Altitude = p.Altitude, DateTimeUtc = p.DateTimeUtc,
        }).ToList();
        new GpsNormalizationService().SmoothAltitudes(copy);
        return copy.Select(p => p.Altitude!.Value).ToArray();
    }

    /// <summary>A plausible barometer for a leg: ISA-inverted altitude, latched in runs of 6 and
    /// quantised to 0.01 hPa, which is how the phone actually delivers it.</summary>
    private static void AttachBarometer(List<GpsData> points, int latchRun = 6)
    {
        for (int i = 0; i < points.Count; i++)
        {
            double alt = points[i - i % latchRun].Altitude!.Value;
            points[i].Pressure = Math.Round(
                1013.25 * Math.Pow(1.0 - alt / 44330.0, 1.0 / 0.1902949), 2);
        }
    }

    [Fact]
    public void NoBarometer_IsByteIdenticalToTodaysPath()
    {
        var points = HillLeg();
        var expected = DemOnlyResult(points);

        new GpsNormalizationService().SmoothAltitudes(points);

        Assert.All(points, p => Assert.Null(p.Pressure));
        Assert.Equal(expected, points.Select(p => p.Altitude!.Value).ToArray());
    }

    [Fact]
    public void ABarometerThatDiesMidRide_FallsBackForTheWholeTrack_NotMidTrack()
    {
        // 25 % coverage: below the threshold, so the WHOLE track takes the DEM-only path. A per-segment
        // decision would fuse the first quarter and smooth the rest, leaving a step at the changeover.
        var points = HillLeg();
        var expected = DemOnlyResult(points);
        AttachBarometer(points);
        for (int i = points.Count / 4; i < points.Count; i++) points[i].Pressure = null;

        new GpsNormalizationService().SmoothAltitudes(points);

        Assert.Equal(expected, points.Select(p => p.Altitude!.Value).ToArray());
    }

    [Fact]
    public void AGarbagePressureChannel_IsNotTreatedAsABarometer()
    {
        // RecordingRequestValidator already rejects this band on ingest; the kernel is the second line,
        // for paths it never sees (edit replay, sync). A channel of zeroes reads as 44 330 m of ISA
        // altitude per point, so it must fall back rather than fuse.
        var points = HillLeg();
        var expected = DemOnlyResult(points);
        foreach (var p in points) p.Pressure = 0.0;

        new GpsNormalizationService().SmoothAltitudes(points);

        Assert.Equal(expected, points.Select(p => p.Altitude!.Value).ToArray());
    }

    [Fact]
    public void AGoodBarometer_DoesEngageFusion()
    {
        // The inverse guard: without this, every test above would also pass on a dispatch that never
        // fuses at all.
        var points = HillLeg();
        var demOnly = DemOnlyResult(points);
        AttachBarometer(points);

        new GpsNormalizationService().SmoothAltitudes(points);
        var fused = points.Select(p => p.Altitude!.Value).ToArray();

        Assert.NotEqual(demOnly, fused);
        Assert.True(fused.Zip(demOnly, (a, b) => Math.Abs(a - b)).Max() > 0.5);
    }

    [Fact]
    public void ARecordingBreak_IsNotBankedAsClimbing_EvenWithABarometer()
    {
        // Drive to the trailhead: a 2 h gap and 400 m of real altitude change, none of it ridden.
        // Pressure keeps changing across it, so a barometer that bridged the gap would bank the whole
        // step. Mirrors ComputeStats_ElevationStepAcrossGap_IsNotCountedAsAscent, with the sensor on.
        var sut = new GpsNormalizationService();
        var t0 = new DateTime(2025, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var points = new List<GpsData>();
        for (int leg = 0; leg < 2; leg++)
        {
            double alt = leg == 0 ? 100.0 : 500.0;
            double lngBase = leg * 3000.0 / MetresPerDegreeLng;
            for (int i = 0; i < 150; i++)
                points.Add(new GpsData
                {
                    GpsLat = Lat,
                    GpsLong = lngBase + i * 10.0 / MetresPerDegreeLng,
                    Altitude = alt,
                    DateTimeUtc = t0.AddSeconds(leg * 7200 + i * 2),
                });
        }
        AttachBarometer(points);

        sut.SmoothAltitudes(points);
        var stats = sut.ComputeStats(points);

        Assert.True(stats.TotalAscentMeters < 5.0,
            $"the 400 m step across the 2 h gap was banked as ascent ({stats.TotalAscentMeters:F0} m)");
        // And nothing spurious appeared INSIDE either flat segment.
        var alts = points.Select(p => p.Altitude!.Value).ToArray();
        Assert.True(alts.Take(150).Zip(alts.Take(150).Skip(1), (a, b) => Math.Abs(b - a)).Max() < 1.0);
        Assert.True(alts.Skip(150).Zip(alts.Skip(151), (a, b) => Math.Abs(b - a)).Max() < 1.0);
    }
}
