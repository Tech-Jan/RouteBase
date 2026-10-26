using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Xunit;
using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Tests.Services;

public class GeoUtilsTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Runs SmoothSpeeds on BOTH overloads (GpsData + ExternalGpsPoint) from the same (time, dist)
    // spec and returns the resulting speed array — the two overloads must behave identically.
    private static double[] SmoothBoth(params (DateTime? time, double? dist)[] pts)
    {
        var gps = pts.Select(p => new GpsData { DateTimeUtc = p.time, Distance = p.dist }).ToList();
        var ext = pts.Select(p => new ExternalGpsPoint { TimestampUtc = p.time, Distance = p.dist }).ToList();
        GeoUtils.SmoothSpeeds(gps);
        GeoUtils.SmoothSpeeds(ext);

        var gpsSpeeds = gps.Select(p => p.Speed ?? 0).ToArray();
        var extSpeeds = ext.Select(p => p.Speed ?? 0).ToArray();
        Assert.Equal(gpsSpeeds, extSpeeds); // overloads agree
        return gpsSpeeds;
    }

    [Fact]
    public void SmoothSpeeds_ConstantPace_YieldsConstantSpeed()
    {
        // 10 points, 1 s / 5 m apart → 5 m/s from index 1 on; index 0 is 0 (dt=0).
        var pts = Enumerable.Range(0, 10)
            .Select(i => ((DateTime?)T0.AddSeconds(i), (double?)(i * 5.0)))
            .ToArray();

        var speeds = SmoothBoth(pts);

        Assert.Equal(0.0, speeds[0]);
        for (int i = 1; i < speeds.Length; i++)
            Assert.Equal(5.0, speeds[i], 6);
    }

    [Fact]
    public void SmoothSpeeds_NullTimestampPoint_GetsZeroSpeed_OthersUnaffected()
    {
        var speeds = SmoothBoth(
            (T0,               0.0),
            (T0.AddSeconds(1), 5.0),
            (null,             10.0),  // no timestamp → speed 0
            (T0.AddSeconds(3), 15.0));

        Assert.Equal(0.0, speeds[0]); // dt=0
        Assert.Equal(5.0, speeds[1], 6);
        Assert.Equal(0.0, speeds[2]); // null timestamp
        Assert.True(speeds[3] > 0);   // recovers after the gap
    }

    [Fact]
    public void SmoothSpeeds_WindowAveragesOverSpike()
    {
        // A single distance spike is averaged over the 5 s window rather than read as a huge dd/dt step.
        var speeds = SmoothBoth(
            (T0,               0.0),
            (T0.AddSeconds(1), 5.0),
            (T0.AddSeconds(2), 10.0),
            (T0.AddSeconds(3), 60.0),  // +50 m spike in one step
            (T0.AddSeconds(4), 65.0));

        // Windowed speed at the spike (index 3): (60-0)/3 = 20, not the raw 50 m/s step.
        Assert.Equal(20.0, speeds[3], 6);
    }

    [Fact]
    public void SamePoint_ReturnsZero()
    {
        var result = GeoUtils.HaversineDistance(47.0, 11.0, 47.0, 11.0);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void KnownDistance_LondonToNewYork()
    {
        // London (51.5074, -0.1278) to New York (40.7128, -74.0060)
        // Expected: ~5570 km
        var result = GeoUtils.HaversineDistance(51.5074, -0.1278, 40.7128, -74.0060);
        Assert.InRange(result, 5_550_000, 5_600_000);
    }

    [Fact]
    public void ShortDistance_OneDegreeLatitude()
    {
        // 1 degree latitude ≈ 111 km at any longitude
        var result = GeoUtils.HaversineDistance(47.0, 11.0, 48.0, 11.0);
        Assert.InRange(result, 110_000, 112_000);
    }

    [Fact]
    public void SymmetricDistance_AtoB_EqualsB_toA()
    {
        var ab = GeoUtils.HaversineDistance(47.0, 11.0, 48.5, 12.5);
        var ba = GeoUtils.HaversineDistance(48.5, 12.5, 47.0, 11.0);
        Assert.Equal(ab, ba, precision: 6);
    }

    [Fact]
    public void ToRadians_CommonValues()
    {
        Assert.Equal(0.0, GeoUtils.ToRadians(0));
        Assert.Equal(Math.PI, GeoUtils.ToRadians(180), precision: 10);
        Assert.Equal(Math.PI / 2, GeoUtils.ToRadians(90), precision: 10);
        Assert.Equal(-Math.PI, GeoUtils.ToRadians(-180), precision: 10);
    }

    [Fact]
    public void AntipodalPoints_MaximumDistance()
    {
        // North Pole to South Pole — half the circumference of the Earth (~20,015 km)
        var result = GeoUtils.HaversineDistance(90.0, 0.0, -90.0, 0.0);
        Assert.InRange(result, 20_000_000, 20_030_000);
    }

    [Fact]
    public void SouthernHemisphere_DistanceIsPositive()
    {
        // Both points in southern hemisphere (negative latitudes)
        var result = GeoUtils.HaversineDistance(-33.8688, 151.2093, -37.8136, 144.9631); // Sydney to Melbourne
        Assert.InRange(result, 710_000, 720_000);
    }

    [Fact]
    public void CrossingAntimeridian_ShortPathIsCorrect()
    {
        // Two points near the antimeridian: 179°E and 179°W are 2° apart, not 358°
        // Haversine handles this correctly via sin periodicity
        var shortPath  = GeoUtils.HaversineDistance(0.0,  179.0, 0.0, -179.0); // 2° apart
        var controlPath = GeoUtils.HaversineDistance(0.0, 0.0, 0.0, 2.0);       // 2° apart at prime meridian
        Assert.Equal(controlPath, shortPath, precision: 0); // within 1 metre
    }
}
