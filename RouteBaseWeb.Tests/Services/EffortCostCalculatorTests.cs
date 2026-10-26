using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Tests for the difficulty-weighted effort-cost model
/// (<see cref="EffortCostCalculator.Compute"/>): measured power, Minetti running cost,
/// cycling physics clamping, and the grade fallback.
/// </summary>
public class EffortCostCalculatorTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Builds a synthetic track: n points spaced dtSec apart, dDist metres apart, with optional
    // constant power/speed, a per-step altitude change, and an optional recorded grade (%).
    private static List<GpsSlice> Track(
        int n, double dtSec, double dDist,
        double? power = null, double? speed = null, double altStep = 0, double? gradePct = null) =>
        Enumerable.Range(0, n)
            .Select(i => new GpsSlice
            {
                GpsId       = i,
                DateTimeUtc = T0.AddSeconds(i * dtSec),
                Distance    = i * dDist,
                Speed       = speed,
                Power       = power,
                Altitude    = altStep * i,
                GradeSmooth = gradePct
            })
            .ToList();

    // SE-01: with the Distance channel absent, StepDistance falls back to coordinates; a point with no
    // coordinates must yield a zero-length leg, not a haversine to Null Island (0,0). Both legs touching
    // the middle point are zero → the Run estimate integrates ~no distance/cost (pre-fix: thousands of km).
    [Fact]
    public void StepDistance_MissingCoords_ReturnsZero()
    {
        var pts = new List<GpsSlice>
        {
            new() { GpsId = 0, DateTimeUtc = T0,               GpsLat = 47.0, GpsLong = 11.0, GradeSmooth = 0 },
            new() { GpsId = 1, DateTimeUtc = T0.AddSeconds(1), GpsLat = null, GpsLong = null, GradeSmooth = 0 },
            new() { GpsId = 2, DateTimeUtc = T0.AddSeconds(2), GpsLat = 47.0, GpsLong = 11.0, GradeSmooth = 0 },
        };

        var result = EffortCostCalculator.Compute(pts, "Run", riderMassKg: 70);

        Assert.Equal(0, result.CumulativeKj[^1], 3);
    }

    [Fact]
    public void Compute_ConstantMeasuredPower_IntegratesToWorkKj()
    {
        // 10 points, 1 s apart, 200 W throughout → 9 intervals × 200 W × 1 s = 1800 J = 1.8 kJ.
        var pts = Track(10, dtSec: 1, dDist: 5, power: 200, speed: 5);

        var result = EffortCostCalculator.Compute(pts, "Ride", riderMassKg: 75);

        Assert.False(result.Estimated);
        Assert.Equal(1.8, result.CumulativeKj[^1], 3);
    }

    [Fact]
    public void Compute_FlatRunning_MatchesMinettiBaseline()
    {
        // Flat run, C(0) = 3.6 J/(kg·m). 11 pts × 10 m = 100 m, mass 70 → 3.6·70·100/1000 = 25.2 kJ.
        var pts = Track(11, dtSec: 1, dDist: 10, gradePct: 0);

        var result = EffortCostCalculator.Compute(pts, "Run", riderMassKg: 70);

        Assert.True(result.Estimated);
        Assert.Equal(25.2, result.CumulativeKj[^1], 2);
    }

    [Fact]
    public void Compute_UphillRunning_CostsMoreThanFlat_SameDistance()
    {
        var flat   = EffortCostCalculator.Compute(Track(11, 1, 10, gradePct: 0),  "Run", 70);
        var uphill = EffortCostCalculator.Compute(Track(11, 1, 10, gradePct: 10), "Run", 70);

        Assert.True(uphill.CumulativeKj[^1] > flat.CumulativeKj[^1]);
    }

    [Fact]
    public void Compute_DownhillCycling_NoPower_IsMonotonicAndNonNegative()
    {
        // Descending, constant speed, no power meter → gravity term negative; cost must never drop.
        var pts = Track(11, dtSec: 1, dDist: 10, speed: 8, altStep: -1, gradePct: -10);

        var result = EffortCostCalculator.Compute(pts, "Ride", riderMassKg: 75);

        Assert.True(result.Estimated);
        for (int i = 1; i < result.CumulativeKj.Length; i++)
            Assert.True(result.CumulativeKj[i] >= result.CumulativeKj[i - 1] - 1e-9,
                $"cost not monotonic at {i}");
        Assert.All(result.CumulativeKj, v => Assert.True(v >= 0 && !double.IsNaN(v) && !double.IsInfinity(v)));
    }

    [Fact]
    public void Compute_MissingGrade_FallsBackToAltitudeDelta()
    {
        // No recorded grade; +1 m per 10 m step ≈ 10% climb. Should exceed the flat baseline and stay finite.
        var withAlt = EffortCostCalculator.Compute(Track(11, 1, 10, altStep: 1), "Run", 70);
        var flat    = EffortCostCalculator.Compute(Track(11, 1, 10, gradePct: 0), "Run", 70);

        Assert.All(withAlt.CumulativeKj, v => Assert.True(!double.IsNaN(v) && !double.IsInfinity(v)));
        Assert.True(withAlt.CumulativeKj[^1] > flat.CumulativeKj[^1]);
    }

    [Fact]
    public void Compute_Hiking_UsesMinettiModel()
    {
        // "Hiking" must classify as walk group (Minetti), not the cycling physics model.
        // Same flat track as the Run baseline → identical 25.2 kJ.
        var hike = EffortCostCalculator.Compute(Track(11, 1, 10, gradePct: 0), "hiking", 70);

        Assert.True(hike.Estimated);
        Assert.Equal(25.2, hike.CumulativeKj[^1], 2);
    }

    [Fact]
    public void Compute_Walking_UsesMinettiModel()
    {
        var walk = EffortCostCalculator.Compute(Track(11, 1, 10, gradePct: 0), "Walking", 70);

        Assert.True(walk.Estimated);
        Assert.Equal(25.2, walk.CumulativeKj[^1], 2);
    }

    [Fact]
    public void Compute_CyclingWithoutSpeedColumn_UsesDistanceOverTime()
    {
        // Speed column null everywhere; distance/time present → path speed (ds/dt) feeds the model.
        var noSpeed   = EffortCostCalculator.Compute(Track(11, 1, 10), "Ride", 75);
        var withSpeed = EffortCostCalculator.Compute(Track(11, 1, 10, speed: 10), "Ride", 75);

        Assert.True(noSpeed.CumulativeKj[^1] > 0);
        Assert.Equal(withSpeed.CumulativeKj[^1], noSpeed.CumulativeKj[^1], 1);
    }

    [Fact]
    public void Compute_TooFewPoints_ReturnsZeroCost()
    {
        var result = EffortCostCalculator.Compute(Track(1, 1, 10, power: 200), "Ride", 75);

        Assert.Single(result.CumulativeKj);
        Assert.Equal(0, result.CumulativeKj[0], 6);
    }
}
