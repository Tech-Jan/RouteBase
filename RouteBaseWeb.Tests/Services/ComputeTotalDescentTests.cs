using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Tests.Services;

public class ComputeTotalDescentTests
{
    private readonly ActivityImportService _sut;

    public ComputeTotalDescentTests()
    {
        // ComputeTotalDescent is a pure function — context + blob store are unused.
        _sut = new ActivityImportService(null!, null!, NullLogger<ActivityImportService>.Instance);
    }

    [Fact]
    public void EmptyList_ReturnsZero()
    {
        var result = _sut.ComputeTotalDescent([]);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void SinglePoint_ReturnsZero()
    {
        var points = new List<ExternalGpsPoint>
        {
            new() { Altitude = 100 }
        };
        Assert.Equal(0.0, _sut.ComputeTotalDescent(points));
    }

    [Fact]
    public void AllNullAltitudes_ReturnsZero()
    {
        var points = new List<ExternalGpsPoint>
        {
            new() { Altitude = null },
            new() { Altitude = null },
            new() { Altitude = null }
        };
        Assert.Equal(0.0, _sut.ComputeTotalDescent(points));
    }

    [Fact]
    public void MonotonicallyAscending_ReturnsZero()
    {
        var points = new List<ExternalGpsPoint>
        {
            new() { Altitude = 100 },
            new() { Altitude = 200 },
            new() { Altitude = 300 }
        };
        Assert.Equal(0.0, _sut.ComputeTotalDescent(points));
    }

    [Fact]
    public void MonotonicallyDescending_ReturnsTotalDrop()
    {
        var points = new List<ExternalGpsPoint>
        {
            new() { Altitude = 300 },
            new() { Altitude = 200 },
            new() { Altitude = 100 }
        };
        Assert.Equal(200.0, _sut.ComputeTotalDescent(points));
    }

    [Fact]
    public void MixedAscentsAndDescents_SumsOnlyDescents()
    {
        // 100 -> 200 (+100) -> 150 (-50) -> 250 (+100) -> 180 (-70)
        var points = new List<ExternalGpsPoint>
        {
            new() { Altitude = 100 },
            new() { Altitude = 200 },
            new() { Altitude = 150 },
            new() { Altitude = 250 },
            new() { Altitude = 180 }
        };
        Assert.Equal(120.0, _sut.ComputeTotalDescent(points)); // 50 + 70
    }

    [Fact]
    public void NullsInterspersed_SkippedCorrectly()
    {
        // 300 -> null -> 200 -> null -> 100
        // Only compares non-null pairs: 300->200 (-100), 200->100 (-100)
        var points = new List<ExternalGpsPoint>
        {
            new() { Altitude = 300 },
            new() { Altitude = null },
            new() { Altitude = 200 },
            new() { Altitude = null },
            new() { Altitude = 100 }
        };
        Assert.Equal(200.0, _sut.ComputeTotalDescent(points));
    }

    [Fact]
    public void FlatTerrain_ReturnsZero()
    {
        var points = new List<ExternalGpsPoint>
        {
            new() { Altitude = 500 },
            new() { Altitude = 500 },
            new() { Altitude = 500 }
        };
        Assert.Equal(0.0, _sut.ComputeTotalDescent(points));
    }

    [Fact]
    public void BelowSeaLevel_NegativeAltitudes_ComputesCorrectly()
    {
        // Descent from -10 m to -50 m = 40 m of descent
        var points = new List<ExternalGpsPoint>
        {
            new() { Altitude = -10 },
            new() { Altitude = -50 }
        };
        Assert.Equal(40.0, _sut.ComputeTotalDescent(points));
    }

    [Fact]
    public void TwoPoints_OnlyAscent_ReturnsZero()
    {
        var points = new List<ExternalGpsPoint>
        {
            new() { Altitude = 100 },
            new() { Altitude = 200 }
        };
        Assert.Equal(0.0, _sut.ComputeTotalDescent(points));
    }

    [Fact]
    public void TwoPoints_Descending_ReturnsExactDrop()
    {
        var points = new List<ExternalGpsPoint>
        {
            new() { Altitude = 1234.5 },
            new() { Altitude = 1000.0 }
        };
        Assert.Equal(234.5, _sut.ComputeTotalDescent(points), precision: 10);
    }
}
