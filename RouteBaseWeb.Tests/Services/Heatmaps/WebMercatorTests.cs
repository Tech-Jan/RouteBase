using RouteBaseWeb.Services.Heatmaps;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

public class WebMercatorTests
{
    [Fact]
    public void Origin_MapsToCenterOfWorld()
    {
        var (x, y) = WebMercator.LatLonToGlobalPx(0, 0, 0);
        Assert.Equal(128.0, x, 9);
        Assert.Equal(128.0, y, 9);
    }

    [Theory]
    [InlineData(47.5, 11.3, 16)]
    [InlineData(-33.86, 151.21, 12)]
    [InlineData(69.65, 18.96, 7)]
    public void RoundTrip_IsExact(double lat, double lon, int zoom)
    {
        var (x, y) = WebMercator.LatLonToGlobalPx(lat, lon, zoom);
        var (lat2, lon2) = WebMercator.GlobalPxToLatLon(x, y, zoom);
        Assert.Equal(lat, lat2, 9);
        Assert.Equal(lon, lon2, 9);
    }

    [Theory]
    [InlineData(0, 256, 0)]
    [InlineData(255, 256, 0)]
    [InlineData(256, 256, 1)]
    [InlineData(-1, 256, -1)]
    [InlineData(-256, 256, -1)]
    [InlineData(-257, 256, -2)]
    public void FloorDiv_FloorsTowardNegativeInfinity(int value, int divisor, int expected)
    {
        Assert.Equal(expected, WebMercator.FloorDiv(value, divisor));
    }
}
