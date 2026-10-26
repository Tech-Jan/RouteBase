using RouteBaseWeb.Services.Elevation;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Elevation;

/// <summary>
/// Unit tests for <see cref="ElevationFusion"/>, pinned against the research harness
/// (<see cref="ElevationFusionGoldens"/>) exactly as the Whittaker solve is pinned against scipy.
/// The harness is the reference implementation and is validated against surveyed DGM1 LiDAR, so a
/// disagreement here means the PORT is wrong — it is never a reason to loosen a tolerance.
/// </summary>
public class ElevationFusionTests
{
    private static double[] Ramp(int n, double step) =>
        Enumerable.Range(0, n).Select(i => i * step).ToArray();

    /// <summary>
    /// Largest absolute disagreement with the harness, in metres. Asserted as ONE bound over the whole
    /// array rather than per-element decimal rounding, because the two implementations factorise the
    /// same matrix differently — banded Cholesky here, SuperLU LU in scipy — so they cannot agree to the
    /// last bit, and a value that lands on a rounding boundary flips a per-element check while nothing
    /// is wrong. <see cref="GoldenToleranceMeters"/> is still ~1000× tighter than any elevation claim
    /// anyone makes; a real port bug moves these by centimetres, not nanometres.
    /// </summary>
    private const double GoldenToleranceMeters = 1e-6;

    private static void AssertMatchesHarness(double[] expected, double[] got, string what)
    {
        Assert.Equal(expected.Length, got.Length);
        double worst = expected.Zip(got, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(worst <= GoldenToleranceMeters,
            $"{what} drifted from the harness by {worst:E3} m (bound {GoldenToleranceMeters:E0} m)");
    }

    private static double Gain(double[] profile, double deadband = 2.0)
    {
        // The production accumulator: bank |Δ| once it clears the deadband, then reset.
        double total = 0, acc = 0;
        for (int i = 1; i < profile.Length; i++)
        {
            acc += profile[i] - profile[i - 1];
            if (Math.Abs(acc) < deadband) continue;
            if (acc > 0) total += acc;
            acc = 0;
        }
        return total;
    }

    // ── the measured constants ────────────────────────────────────────────────────────────────

    [Fact]
    public void MeasuredConstants_MatchTheResearchValues()
    {
        // Each is a measurement, not a knob: variogram sill and correlation length over 95 activities,
        // increment noise over 22 stationary windows, drift by loop closure. λ's length is the ONE
        // fitted parameter. A drift here means someone re-tuned without re-measuring.
        Assert.Equal(7.65, ElevationFusion.SigmaDemMeters);
        Assert.Equal(390.0, ElevationFusion.DemCorrelationLengthMeters);
        Assert.Equal(0.16, ElevationFusion.SigmaBaroIncrementMeters);
        Assert.Equal(4.2, ElevationFusion.BaroDriftMetersPerHour);
        Assert.Equal(10.0, ElevationFusion.FusedSmoothLengthMeters);
        Assert.Equal(0.5, ElevationFusion.MinPressureCoverage);
    }

    // ── the barometer-less contract (B3) ──────────────────────────────────────────────────────

    [Fact]
    public void NoPressure_IsByteIdenticalToTheDemOnlySmoother()
    {
        var arc = Ramp(ElevationFusionGoldens.HillCount, ElevationFusionGoldens.HillStepMeters);
        var time = Ramp(ElevationFusionGoldens.HillCount, ElevationFusionGoldens.HillCadenceSeconds);
        var none = new double[ElevationFusionGoldens.HillCount];
        Array.Fill(none, double.NaN);

        // Assert.Equal on the array, NOT a tolerance: "an equivalent weighting" was ASSUMED to
        // reproduce production once and did not — the flat regime fires on 49 % of real activities and
        // the weighted path drifted up to 7.3 m of elevation there. Delegation is the guarantee.
        Assert.Equal(
            ElevationProfileSmoother.SmoothByArcLength(arc, ElevationFusionGoldens.HillDem),
            ElevationFusion.SmoothByArcLength(arc, ElevationFusionGoldens.HillDem, none, time));
    }

    [Fact]
    public void CoverageBelowTheThreshold_FallsBackForTheWholeTrack_NeverMidTrack()
    {
        // A sensor that dies at 25 % must not leave the first quarter fused and the rest smoothed —
        // that seam is a visible step in the profile.
        int n = ElevationFusionGoldens.HillCount;
        var arc = Ramp(n, ElevationFusionGoldens.HillStepMeters);
        var time = Ramp(n, ElevationFusionGoldens.HillCadenceSeconds);
        var dying = (double[])ElevationFusionGoldens.HillPressureHpa.Clone();
        for (int i = n / 4; i < n; i++) dying[i] = double.NaN;

        Assert.False(ElevationFusion.HasUsablePressure(dying));
        Assert.Equal(
            ElevationProfileSmoother.SmoothByArcLength(arc, ElevationFusionGoldens.HillDem),
            ElevationFusion.SmoothByArcLength(arc, ElevationFusionGoldens.HillDem, dying, time));
    }

    [Fact]
    public void TheFallbackLength_IsTheCallersChoice_NotTheSmootherDefault()
    {
        // The gain call site passes 70 m; falling back must land on THAT, not silently on 105.
        var arc = Ramp(ElevationFusionGoldens.HillCount, ElevationFusionGoldens.HillStepMeters);
        var time = Ramp(ElevationFusionGoldens.HillCount, ElevationFusionGoldens.HillCadenceSeconds);
        var none = new double[ElevationFusionGoldens.HillCount];
        Array.Fill(none, double.NaN);

        Assert.Equal(
            ElevationProfileSmoother.SmoothByArcLength(
                arc, ElevationFusionGoldens.HillDem, ElevationProfileSmoother.GainSmoothLengthMeters),
            ElevationFusion.SmoothByArcLength(
                arc, ElevationFusionGoldens.HillDem, none, time,
                fallbackLengthMeters: ElevationProfileSmoother.GainSmoothLengthMeters));
    }

    [Theory]
    [InlineData(50, true)]     // exactly 50 % passes
    [InlineData(49, false)]
    public void CoverageThreshold_IsExplicitAndSymmetric(int finiteCount, bool expected)
    {
        var p = new double[100];
        Array.Fill(p, double.NaN);
        for (int i = 0; i < finiteCount; i++) p[i] = 980.0;
        Assert.Equal(expected, ElevationFusion.HasUsablePressure(p));
    }

    [Fact]
    public void HasUsablePressure_RejectsNullAndEmpty()
    {
        Assert.False(ElevationFusion.HasUsablePressure(null));
        Assert.False(ElevationFusion.HasUsablePressure([]));
    }

    [Fact]
    public void MismatchedChannelLengths_FallBackRatherThanThrow()
    {
        var arc = Ramp(60, 5.0);
        var dem = Ramp(60, 1.0);
        Assert.Equal(
            ElevationProfileSmoother.SmoothByArcLength(arc, dem),
            ElevationFusion.SmoothByArcLength(arc, dem, Ramp(30, 1.0), Ramp(60, 1.0)));
    }

    [Fact]
    public void TooFewNodesToSolve_FallsBackToTheDemOnlySmoother()
    {
        // Mirrors the reference's per-segment `continue`: below 10 usable nodes there is nothing worth
        // solving, and the DEM-only answer stands for that stretch.
        var arc = Ramp(8, 20.0);
        var dem = new[] { 100.0, 101.0, 103.0, 108.0, 112.0, 111.0, 109.0, 104.0 };
        var p = Enumerable.Repeat(980.0, 8).ToArray();
        Assert.Equal(
            ElevationProfileSmoother.SmoothByArcLength(arc, dem),
            ElevationFusion.SmoothByArcLength(arc, dem, p, Ramp(8, 2.0)));
    }

    // ── cross-implementation goldens ──────────────────────────────────────────────────────────

    [Fact]
    public void IsaAltitude_MatchesTheHarness()
    {
        var got = ElevationFusion.IsaAltitude([1013.25, 1000.0, 950.0, 900.0, 850.0]);
        double[] expected = [0.0, 110.90101210401826, 540.4174737941003, 988.6462689985277, 1457.5136605829255];
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], got[i], precision: 9);
    }

    [Fact]
    public void Destaircase_AnchorsEachLatchedBatchAtItsLastTimestamp()
    {
        // Three runs of a latched barometer with a NaN in the middle of the third. Each batch's value
        // describes the END of its run, so the interpolation anchors there — putting the whole step on
        // one sample instead would inject a zero-order-hold edge as if it were terrain.
        double[] p = [1000.00, 1000.00, 1000.00, 999.80, 999.80, 999.50, double.NaN, 999.50, 999.20];
        var got = ElevationFusion.Destaircase(p, Ramp(9, 1.0));
        double[] expected = [1000.0, 1000.0, 1000.0, 999.9, 999.8, 999.5, 999.5, 999.5, 999.2];
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], got[i], precision: 9);
    }

    [Fact]
    public void Destaircase_SurvivesDuplicateTimestampsAcrossABatchBoundary()
    {
        // Regression: the phone stamps several points on the same second often enough that one measured
        // commute had 432 of 3776. Collapsing duplicate anchor times DURING batch construction compares
        // against a time that is not final yet, deletes the preceding batch's anchor, and here erases
        // the whole 1000.0 → 999.5 step. It bent that real profile by ~0.6 m and no synthetic fixture
        // could see it — their timestamps strictly increase.
        double[] p = [1000.0, 1000.0, 999.5, 999.5, 999.5, 999.0];
        double[] t = [0.0, 1.0, 1.0, 2.0, 3.0, 3.0];
        var got = ElevationFusion.Destaircase(p, t);
        double[] expected = [1000.0, 1000.0, 1000.0, 999.5, 999.0, 999.0];
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], got[i], precision: 9);
    }

    [Fact]
    public void AlphaWeights_DecimateToOneAnchorPerCorrelationLength_AndAlwaysPinTheTail()
    {
        // One anchor per 390 m on a 5 m grid = every 78th node, plus the tail so no stretch floats.
        // Trusting all 200 nodes instead would believe the DEM 78× more than the variogram supports.
        var a = ElevationFusion.AlphaWeights(200, 5.0);
        var nonzero = Enumerable.Range(0, 200).Where(i => a[i] != 0.0).ToArray();
        Assert.Equal([0, 78, 156, 199], nonzero);
        Assert.All(nonzero, i => Assert.Equal(1.0 / (7.65 * 7.65), a[i], precision: 12));

        var shortCorr = ElevationFusion.AlphaWeights(12, 5.0, correlationLengthMeters: 30.0);
        Assert.Equal([0, 6, 11], Enumerable.Range(0, 12).Where(i => shortCorr[i] != 0.0).ToArray());
    }

    [Fact]
    public void BetaWeights_MatchTheHarness_AndCollapseAsTheGapGrows()
    {
        var got = ElevationFusion.BetaWeights([0.5, 1.0, 3.0, 60.0, 600.0]);
        double[] expected = [18.63932898415657, 17.825311942959, 15.174506828528072,
                             2.8968713789107765, 0.33431398769724524];
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], got[i], precision: 9);
        // A stop inflates Δt, so trust in the barometric slope collapses with no special case.
        Assert.True(got[^1] < got[0] / 50.0);

        // The latch correction: one reading spread over 6 points carries a sixth of the information.
        Assert.Equal(74.44168734491315, ElevationFusion.BetaWeights([1.0], samplesPerNode: 6.0)[0], precision: 9);
    }

    [Fact]
    public void Fuse_MatchesTheHarnessOnASmallExplicitSystem()
    {
        // Localises a band-assembly or right-hand-side error away from the end-to-end golden.
        double[] dem = [100.0, 101.0, 103.0, 108.0, 112.0, 111.0, 109.0, 104.0, 100.0];
        double[] baro = [50.0, 51.5, 54.0, 58.0, 61.0, 60.0, 57.5, 53.0, 49.5];
        var alpha = ElevationFusion.AlphaWeights(9, 5.0, correlationLengthMeters: 20.0);
        var beta = ElevationFusion.BetaWeights(Enumerable.Repeat(2.0, 8).ToArray());
        var lambda = Enumerable.Repeat(ElevationProfileSmoother.Lambda(10.0, 5.0), 7).ToArray();

        var got = ElevationFusion.Fuse(dem, baro, alpha, beta, lambda);
        AssertMatchesHarness(ElevationFusionGoldens.FuseExpected, got, "the small explicit solve");
    }

    [Fact]
    public void Fuse_WithNoSurvivingDemAnchor_IsRejectedRatherThanSolvedSingular()
    {
        double[] dem = [double.NaN, double.NaN, double.NaN, double.NaN, double.NaN];
        double[] baro = [50.0, 51.0, 52.0, 53.0, 54.0];
        var alpha = ElevationFusion.AlphaWeights(5, 5.0);
        var beta = ElevationFusion.BetaWeights([1.0, 1.0, 1.0, 1.0]);
        var lambda = Enumerable.Repeat(16.0, 3).ToArray();
        Assert.Throws<ArgumentException>(() => ElevationFusion.Fuse(dem, baro, alpha, beta, lambda));
    }

    [Fact]
    public void SmoothByArcLength_MatchesTheHarnessEndToEnd_OnALatchedBarometer()
    {
        // THE primary gate. A ~45 m climb with a 5-lobe undulation, DEM slip noise, and pressure
        // delivered in latched runs of 6 — the delivery regime 16 of 21 real recordings are in.
        var arc = Ramp(ElevationFusionGoldens.HillCount, ElevationFusionGoldens.HillStepMeters);
        var time = Ramp(ElevationFusionGoldens.HillCount, ElevationFusionGoldens.HillCadenceSeconds);

        var got = ElevationFusion.SmoothByArcLength(
            arc, ElevationFusionGoldens.HillDem, ElevationFusionGoldens.HillPressureHpa, time);

        AssertMatchesHarness(ElevationFusionGoldens.HillFused, got, "the fused hill profile");

        // And it is genuinely a different answer, not the fallback wearing a hat.
        var demOnly = ElevationProfileSmoother.SmoothByArcLength(arc, ElevationFusionGoldens.HillDem);
        Assert.True(got.Zip(demOnly, (a, b) => Math.Abs(a - b)).Max() > 5.0);
    }

    // ── the flat regime must NOT be mirrored on the fused path ────────────────────────────────

    [Fact]
    public void FlatGround_KeepsTheBarometricSignal_WhereTheDemOnlyPathCrushesItToNothing()
    {
        // Decision pin, not a preference. Production re-smooths at L=200 m when relief < 8 m to kill
        // DEM noise — but flat ground is exactly where the barometer carries real signal the DEM cannot
        // resolve. On the one real same-day repeat pair where it fires, fusion holds a 10 % cross-pass
        // spread and the flat regime blows it out to 120 %.
        //
        // In miniature here: a ~3 m real undulation under 6 m of slip noise. The DEM-only path returns
        // a profile with EXACTLY ZERO gain; fusion recovers the undulation to within ~7 %.
        var arc = Ramp(ElevationFusionGoldens.FlatCount, ElevationFusionGoldens.FlatStepMeters);
        var time = Ramp(ElevationFusionGoldens.FlatCount, ElevationFusionGoldens.FlatCadenceSeconds);

        var fused = ElevationFusion.SmoothByArcLength(
            arc, ElevationFusionGoldens.FlatDem, ElevationFusionGoldens.FlatPressureHpa, time);
        AssertMatchesHarness(ElevationFusionGoldens.FlatFused, fused, "the fused flat profile");

        var demOnly = ElevationProfileSmoother.SmoothByArcLength(arc, ElevationFusionGoldens.FlatDem);
        Assert.True(ElevationProfileSmoother.Relief(demOnly) < ElevationProfileSmoother.FlatReliefThresholdMeters,
            "the fixture must actually engage production's flat regime");
        Assert.Equal(0.0, Gain(demOnly));
        Assert.InRange(Gain(fused), 16.0, 22.0);   // the fixture's true gain is 18.07 m
    }

    // ── the dynamic-pressure seam (measured, deliberately off) ────────────────────────────────

    [Fact]
    public void DynamicPressure_IsWiredButOffByDefault()
    {
        // Δh = −c·v²/2g is real (c ≈ 0.39, positive in all 5 measured pairs) but ~1.3 m at corpus
        // speeds, and the regime where it would matter (15 m/s ⇒ ~4.5 m) is untested — so it ships at
        // zero, with a live seam rather than a comment.
        int n = ElevationFusionGoldens.HillCount;
        var arc = Ramp(n, ElevationFusionGoldens.HillStepMeters);
        var time = Ramp(n, ElevationFusionGoldens.HillCadenceSeconds);
        var accelerating = Enumerable.Range(0, n).Select(i => 2.0 + 13.0 * i / (n - 1.0)).ToArray();

        // Handing over a speed channel changes nothing while the coefficient is zero.
        var off = ElevationFusion.SmoothByArcLength(
            arc, ElevationFusionGoldens.HillDem, ElevationFusionGoldens.HillPressureHpa, time,
            speedMetersPerSecond: accelerating);
        AssertMatchesHarness(ElevationFusionGoldens.HillFused, off, "the coefficient-zero path");

        // Turning it on does. Only Δbaro reaches the solve, so a CONSTANT speed is invisible by
        // construction (a pure level the DEM anchor absorbs) — the correction can only act where the
        // speed itself changes, which is exactly the claim being made for it.
        var varying = ElevationFusion.SmoothByArcLength(
            arc, ElevationFusionGoldens.HillDem, ElevationFusionGoldens.HillPressureHpa, time,
            dynamicPressureCoefficient: 0.39, speedMetersPerSecond: accelerating);
        Assert.True(off.Zip(varying, (a, b) => Math.Abs(a - b)).Max() > 0.5);

        var constant = ElevationFusion.SmoothByArcLength(
            arc, ElevationFusionGoldens.HillDem, ElevationFusionGoldens.HillPressureHpa, time,
            dynamicPressureCoefficient: 0.39,
            speedMetersPerSecond: Enumerable.Repeat(15.0, n).ToArray());
        Assert.Equal(off, constant);
    }
}
