using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// FP-09: direct tests for the shared <see cref="TrackStatsCalculator"/> — all three parsers depend on
/// its distance/elevation/moving-time/content-id logic, previously covered only incidentally.
/// </summary>
public class TrackStatsCalculatorTests
{
    private static ExternalGpsPoint P(double lat, double lng, double? alt = null, DateTime? ts = null) =>
        new() { Lat = lat, Lng = lng, Altitude = alt, TimestampUtc = ts };

    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Compute_SinglePoint_ZeroDistanceAndSpeeds()
    {
        var pts = new List<ExternalGpsPoint> { P(47.0, 8.0, 500, T0) };
        var s = TrackStatsCalculator.Compute(pts, hasTimestamps: true);

        Assert.Equal(0, s.Distance);
        Assert.Equal(0, s.MovingTime);
        Assert.Equal(0, s.MaxSpeed);
        Assert.Equal(0, s.ElapsedSeconds);
    }

    [Fact]
    public void Compute_DuplicateTimestamps_NoDivideByZero()
    {
        // Two points ~100 m apart sharing one timestamp: dt=0 must not produce NaN/Inf speed.
        var pts = new List<ExternalGpsPoint> { P(47.0, 8.0, null, T0), P(47.0009, 8.0, null, T0) };
        var s = TrackStatsCalculator.Compute(pts, hasTimestamps: true);

        Assert.All(pts, p => Assert.True(double.IsFinite(p.Speed!.Value)));
        Assert.Equal(0, s.MovingTime);
    }

    [Fact]
    public void Compute_MissingAltitudes_SkipsElevationPairs()
    {
        // altitudes [100, null, 130]: the null breaks both adjacent pairs → no gain counted.
        var pts = new List<ExternalGpsPoint>
        {
            P(47.0, 8.0, 100, T0),
            P(47.001, 8.0, null, T0.AddSeconds(1)),
            P(47.002, 8.0, 130, T0.AddSeconds(2)),
        };
        var s = TrackStatsCalculator.Compute(pts, hasTimestamps: true);

        Assert.Equal(0, s.ElevationGain);
    }

    [Fact]
    public void Compute_KnownTrack_DistanceMatchesHaversine()
    {
        var pts = new List<ExternalGpsPoint>
        {
            P(47.0000, 8.0000, 500, T0),
            P(47.0010, 8.0010, 500, T0.AddSeconds(10)),
            P(47.0020, 8.0025, 500, T0.AddSeconds(20)),
        };
        var expected =
            GeoUtils.HaversineDistance(47.0000, 8.0000, 47.0010, 8.0010) +
            GeoUtils.HaversineDistance(47.0010, 8.0010, 47.0020, 8.0025);

        var s = TrackStatsCalculator.Compute(pts, hasTimestamps: true);

        Assert.Equal(expected, s.Distance, 1); // within 0.1 m
    }

    [Fact]
    public void ComputeContentId_Deterministic_Positive_And_6dpSensitive()
    {
        var a = new List<ExternalGpsPoint> { P(47.100000, 8.100000), P(47.200000, 8.200000) };
        var b = new List<ExternalGpsPoint> { P(47.100000, 8.100000), P(47.200000, 8.200000) };

        var idA = TrackStatsCalculator.ComputeContentId(a, 1234.0);
        var idB = TrackStatsCalculator.ComputeContentId(b, 1234.0);
        Assert.Equal(idA, idB);
        Assert.True(idA > 0);

        // 7th decimal change rounds away at 6 dp → same id.
        var sub6 = new List<ExternalGpsPoint> { P(47.1000001, 8.100000), P(47.200000, 8.200000) };
        Assert.Equal(idA, TrackStatsCalculator.ComputeContentId(sub6, 1234.0));

        // 5th decimal change survives 6-dp rounding → different id.
        var over6 = new List<ExternalGpsPoint> { P(47.100010, 8.100000), P(47.200000, 8.200000) };
        Assert.NotEqual(idA, TrackStatsCalculator.ComputeContentId(over6, 1234.0));
    }

    [Fact]
    public void Compute_BackwardsTimestamps_NoNegativeMovingOrElapsed()
    {
        // FP-06a: reversed timestamps must not yield negative moving/elapsed time.
        var pts = new List<ExternalGpsPoint>
        {
            P(47.0000, 8.0000, 500, T0.AddSeconds(20)),
            P(47.0010, 8.0010, 500, T0.AddSeconds(10)),
            P(47.0020, 8.0020, 500, T0),
        };
        var s = TrackStatsCalculator.Compute(pts, hasTimestamps: true);

        Assert.True(s.MovingTime >= 0);
        Assert.Null(s.ElapsedSeconds); // last < first → negative span suppressed to null
    }

    [Fact]
    public void FindNonFiniteAggregate_AllFiniteOrNull_ReturnsNull()
    {
        // A1 (2026-07-20): the router's import guard — absent stats and sane values both pass.
        Assert.Null(TrackStatsCalculator.FindNonFiniteAggregate(new ExternalActivitySummary()));
        Assert.Null(TrackStatsCalculator.FindNonFiniteAggregate(new ExternalActivitySummary
        {
            Distance = 50_000, ElevationGain = 500, AverageHeartRate = 140, MaxPower = 900,
        }));
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void FindNonFiniteAggregate_NamesTheOffendingStat(double bad)
    {
        Assert.Equal("TotalDescent",
            TrackStatsCalculator.FindNonFiniteAggregate(new ExternalActivitySummary { TotalDescent = bad }));
        Assert.Equal("AverageHeartRate",
            TrackStatsCalculator.FindNonFiniteAggregate(new ExternalActivitySummary { AverageHeartRate = bad }));
    }
}
