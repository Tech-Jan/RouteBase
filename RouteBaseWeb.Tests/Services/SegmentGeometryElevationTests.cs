using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// SE-06: <see cref="SegmentGeometry.CalculateElevation"/> must bridge null-altitude gaps (deltas
/// between consecutive KNOWN altitudes), never treat a null as 0 m — which fabricated a full
/// descent+ascent of the terrain height around each gap.
/// </summary>
public class SegmentGeometryElevationTests
{
    private static SegmentGpsPoint P(int? alt) => new() { Lat = 47.0, Lng = 11.0, Altitude = alt };

    [Fact]
    public void CalculateElevation_NullAltitudeMidList_NoSpuriousSwing()
    {
        // 500 → (null) → 510: a single +10 m climb, not a 500 m drop then 510 m climb.
        var (asc, desc) = SegmentGeometry.CalculateElevation([P(500), P(null), P(510)]);

        Assert.Equal(10, asc, 3);
        Assert.Equal(0, desc, 3);
    }

    [Fact]
    public void CalculateElevation_AllNull_Zero()
    {
        var (asc, desc) = SegmentGeometry.CalculateElevation([P(null), P(null), P(null)]);

        Assert.Equal(0, asc, 3);
        Assert.Equal(0, desc, 3);
    }
}
