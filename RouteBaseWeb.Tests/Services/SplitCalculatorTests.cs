using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

public class SplitCalculatorTests
{
    [Fact]
    public void EmptyList_ReturnsEmpty()
    {
        var result = SplitCalculator.CalculateSplits([], 1.0m);
        Assert.Empty(result);
    }

    [Fact]
    public void SinglePoint_NoSplits()
    {
        var gps = new List<GpsData>
        {
            new() { Distance = 0, Altitude = 100 }
        };
        var result = SplitCalculator.CalculateSplits(gps, 1.0m);
        Assert.Empty(result);
    }

    [Fact]
    public void ExactlyOneSplit_ProducesSingleSplit()
    {
        var baseTime = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var gps = new List<GpsData>
        {
            new() { Distance = 0,    Altitude = 100, DateTimeUtc = baseTime },
            new() { Distance = 500,  Altitude = 110, DateTimeUtc = baseTime.AddMinutes(2) },
            new() { Distance = 1000, Altitude = 120, DateTimeUtc = baseTime.AddMinutes(5) }
        };

        var result = SplitCalculator.CalculateSplits(gps, 1.0m);

        Assert.Single(result);
        Assert.Equal(1, result[0].SplitNumber);
        Assert.Equal(1.0, result[0].Distance);
        Assert.Equal(1.0, result[0].CumulativeDistance);
        Assert.Equal(TimeSpan.FromMinutes(5), result[0].Time);
    }

    [Fact]
    public void TwoFullSplits_ProducesTwoSplits()
    {
        var baseTime = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var gps = new List<GpsData>
        {
            new() { Distance = 0,    Altitude = 100, DateTimeUtc = baseTime },
            new() { Distance = 1000, Altitude = 120, DateTimeUtc = baseTime.AddMinutes(5) },
            new() { Distance = 2000, Altitude = 100, DateTimeUtc = baseTime.AddMinutes(10) }
        };

        var result = SplitCalculator.CalculateSplits(gps, 1.0m);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].SplitNumber);
        Assert.Equal(2, result[1].SplitNumber);
        Assert.Equal(2.0, result[1].CumulativeDistance);
    }

    [Fact]
    public void PartialFinalSplit_IncludedIfAbove10Percent()
    {
        var baseTime = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var gps = new List<GpsData>
        {
            new() { Distance = 0,    Altitude = 100, DateTimeUtc = baseTime },
            new() { Distance = 1000, Altitude = 120, DateTimeUtc = baseTime.AddMinutes(5) },
            new() { Distance = 1500, Altitude = 110, DateTimeUtc = baseTime.AddMinutes(8) }
        };

        var result = SplitCalculator.CalculateSplits(gps, 1.0m);

        Assert.Equal(2, result.Count);
        Assert.Equal(0.5, result[1].Distance);
    }

    [Fact]
    public void TinyRemainder_ExcludedIfBelow10Percent()
    {
        var baseTime = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var gps = new List<GpsData>
        {
            new() { Distance = 0,    Altitude = 100, DateTimeUtc = baseTime },
            new() { Distance = 1000, Altitude = 120, DateTimeUtc = baseTime.AddMinutes(5) },
            new() { Distance = 1050, Altitude = 121, DateTimeUtc = baseTime.AddMinutes(5.5) }
        };

        var result = SplitCalculator.CalculateSplits(gps, 1.0m);

        // 50m remaining is only 5% of 1000m split — should be excluded
        Assert.Single(result);
    }

    [Fact]
    public void ElevationGainAndLoss_ComputedCorrectly()
    {
        var baseTime = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var gps = new List<GpsData>
        {
            new() { Distance = 0,   Altitude = 100, DateTimeUtc = baseTime },
            new() { Distance = 250, Altitude = 150, DateTimeUtc = baseTime.AddMinutes(1) },  // +50
            new() { Distance = 500, Altitude = 120, DateTimeUtc = baseTime.AddMinutes(2) },  // -30
            new() { Distance = 750, Altitude = 180, DateTimeUtc = baseTime.AddMinutes(3) },  // +60
            new() { Distance = 1000, Altitude = 160, DateTimeUtc = baseTime.AddMinutes(4) }  // -20
        };

        var result = SplitCalculator.CalculateSplits(gps, 1.0m);

        Assert.Single(result);
        Assert.Equal(110.0, result[0].ElevationGain);  // 50 + 60
        Assert.Equal(50.0, result[0].ElevationLoss);    // 30 + 20
        Assert.Equal(60.0, result[0].NetElevationChange); // 110 - 50
    }

    [Fact]
    public void SpeedAndPace_ComputedCorrectly()
    {
        var baseTime = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var gps = new List<GpsData>
        {
            new() { Distance = 0,    Altitude = 100, DateTimeUtc = baseTime },
            new() { Distance = 1000, Altitude = 100, DateTimeUtc = baseTime.AddMinutes(6) }
        };

        var result = SplitCalculator.CalculateSplits(gps, 1.0m);

        Assert.Single(result);
        Assert.Equal(10.0, result[0].Speed);  // 1km / 0.1h = 10 km/h
        Assert.Equal(6.0, result[0].Pace);    // 6 min / 1km
    }

    [Fact]
    public void SegmentTime_UtcOnlyPoints_Computed()
    {
        // UTC is the only per-point timestamp after SL-01 (GPS.datetime_loc dropped); split time
        // must still compute from it.
        var baseUtc = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var gps = new List<GpsData>
        {
            new() { Distance = 0,    Altitude = 100, DateTimeUtc = baseUtc },
            new() { Distance = 1000, Altitude = 100, DateTimeUtc = baseUtc.AddMinutes(5) }
        };

        var result = SplitCalculator.CalculateSplits(gps, 1.0m);

        Assert.Single(result);
        Assert.Equal(TimeSpan.FromMinutes(5), result[0].Time);
    }

    [Fact]
    public void NoTimestamps_SpeedAndPaceAreZero()
    {
        var gps = new List<GpsData>
        {
            new() { Distance = 0,    Altitude = 100 },
            new() { Distance = 1000, Altitude = 100 }
        };

        var result = SplitCalculator.CalculateSplits(gps, 1.0m);

        Assert.Single(result);
        Assert.Equal(0.0, result[0].Speed);
        Assert.Equal(0.0, result[0].Pace);
    }

    [Fact]
    public void NonZeroStartDistance_ProducesSameSplitsAsZeroBased()
    {
        // Same track as TwoFullSplits but every Distance offset by 5000 m (e.g. a mid-track slice).
        // Splits are relative to the track's first distance → still exactly 2 splits of 1.0 km each.
        var baseTime = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var gps = new List<GpsData>
        {
            new() { Distance = 5000, Altitude = 100, DateTimeUtc = baseTime },
            new() { Distance = 6000, Altitude = 120, DateTimeUtc = baseTime.AddMinutes(5) },
            new() { Distance = 7000, Altitude = 100, DateTimeUtc = baseTime.AddMinutes(10) }
        };

        var result = SplitCalculator.CalculateSplits(gps, 1.0m);

        Assert.Equal(2, result.Count);
        Assert.Equal(1.0, result[0].Distance);
        Assert.Equal(1.0, result[1].Distance);
        Assert.Equal(6.0, result[0].CumulativeDistance); // cumulative stays absolute
        Assert.Equal(7.0, result[1].CumulativeDistance);
    }

    [Fact]
    public void LargerSplitDistance_FewerSplits()
    {
        var baseTime = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var gps = new List<GpsData>
        {
            new() { Distance = 0,    Altitude = 100, DateTimeUtc = baseTime },
            new() { Distance = 2000, Altitude = 100, DateTimeUtc = baseTime.AddMinutes(6) },
            new() { Distance = 4000, Altitude = 100, DateTimeUtc = baseTime.AddMinutes(12) },
            new() { Distance = 5000, Altitude = 100, DateTimeUtc = baseTime.AddMinutes(15) }
        };

        var result = SplitCalculator.CalculateSplits(gps, 4.0m); // 4km splits

        // 4km split + 1km remainder (25% > 10%, so included as partial)
        Assert.Equal(2, result.Count);
        Assert.Equal(4.0, result[0].Distance);
        Assert.Equal(1.0, result[1].Distance);
    }
}
