using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Races;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// The course grid — pure geometry, no mocks. Everything downstream (the pacing model, chainage,
/// checkpoint placement) is expressed against this grid, so its two invariants matter:
/// chainage is uniform, and grade reflects the real slope rather than DEM ripple.
/// </summary>
public class RaceCourseGridBuilderTests
{
    private const double Lat0 = 47.9;

    // Metres per degree of longitude at Lat0 — lets a test say "a point every 5 m" and mean it.
    private static readonly double MetresPerDegreeLng = 111_320.0 * Math.Cos(Lat0 * Math.PI / 180.0);

    /// <summary>A due-east track: <paramref name="count"/> points, <paramref name="spacing"/> m apart.</summary>
    private static (double[] lat, double[] lng, double?[] alt) Track(
        int count, double spacing, Func<double, double?> altitudeAt)
    {
        var lat = new double[count];
        var lng = new double[count];
        var alt = new double?[count];
        for (var i = 0; i < count; i++)
        {
            var d = i * spacing;
            lat[i] = Lat0;
            lng[i] = 8.1 + d / MetresPerDegreeLng;
            alt[i] = altitudeAt(d);
        }
        return (lat, lng, alt);
    }

    /// <summary>True arc length of the synthetic track, by the same measure the builder uses.</summary>
    private static double TrackLength(double[] lat, double[] lng)
    {
        var total = 0.0;
        for (var i = 1; i < lat.Length; i++)
            total += GeoUtils.HaversineDistance(lat[i - 1], lng[i - 1], lat[i], lng[i]);
        return total;
    }

    [Fact]
    public void Build_ProducesUniformChainage()
    {
        var (lat, lng, alt) = Track(401, 5.0, _ => 500.0);   // ~2000 m of flat

        var grid = RaceCourseGridBuilder.Build(lat, lng, alt, stepMeters: 10.0);

        var expectedCount = (int)Math.Floor(TrackLength(lat, lng) / 10.0) + 1;
        Assert.Equal(10.0, grid.StepMeters);
        Assert.Equal(expectedCount, grid.Count);
        Assert.Equal(2000, grid.TotalMeters, 10.0);
        // THE invariant: chainage is exactly i * step, so a checkpoint at N metres is node N/step.
        for (var i = 0; i < grid.Count; i++)
            Assert.Equal(i * 10.0, grid.Distance[i], 1e-6);
    }

    [Fact]
    public void Build_FlatCourse_HasZeroGrade()
    {
        var (lat, lng, alt) = Track(201, 5.0, _ => 500.0);

        var grid = RaceCourseGridBuilder.Build(lat, lng, alt);

        Assert.All(grid.GradePercent, g => Assert.Equal(0.0, g, 0.1));
    }

    [Fact]
    public void Build_RecoversTheRealSlope()
    {
        // 1 km at +10%, then 1 km at -10%. Sampled away from the corners, which the smoother rounds.
        var (lat, lng, alt) = Track(401, 5.0, d => d <= 1000 ? 500 + d * 0.10 : 600 - (d - 1000) * 0.10);

        var grid = RaceCourseGridBuilder.Build(lat, lng, alt);

        var atClimb = grid.GradePercent[(int)(500 / grid.StepMeters)];
        var atDescent = grid.GradePercent[(int)(1500 / grid.StepMeters)];
        Assert.Equal(10.0, atClimb, 0.5);
        Assert.Equal(-10.0, atDescent, 0.5);
    }

    [Fact]
    public void Build_SmoothsElevationNoise_SoGradeIsNotShredded()
    {
        // A flat road under a noisy DEM: +/- 2 m sawtooth. Unsmoothed, a 2 m step over a 10 m node is a
        // 20% phantom ramp — and the pacing model would dutifully slow riders down for it.
        var (lat, lng, alt) = Track(401, 5.0, d => 500.0 + (((int)(d / 5) % 2 == 0) ? 2.0 : -2.0));

        var grid = RaceCourseGridBuilder.Build(lat, lng, alt);

        Assert.All(grid.GradePercent, g => Assert.InRange(g, -1.5, 1.5));
    }

    [Fact]
    public void Build_BridgesNullAltitudes_RatherThanReadingThemAsSeaLevel()
    {
        // Imports without elevation leave nulls. Treating a null as 0 m would read as an 800 m cliff.
        var (lat, lng, alt) = Track(401, 5.0, d => d is > 800 and < 1200 ? null : 800.0);

        var grid = RaceCourseGridBuilder.Build(lat, lng, alt);

        Assert.All(grid.Elevation, e => Assert.InRange(e, 790, 810));
        Assert.All(grid.GradePercent, g => Assert.InRange(g, -1.0, 1.0));
    }

    [Fact]
    public void Build_PositionsFollowTheTrack()
    {
        var (lat, lng, alt) = Track(201, 5.0, _ => 500.0);

        var grid = RaceCourseGridBuilder.Build(lat, lng, alt);

        Assert.Equal(lng[0], grid.Lng[0], 1e-9);
        Assert.Equal(Lat0, grid.Lat[grid.Count / 2], 1e-9);

        // Chainage must be REAL distance: the node at 500 m is 500 m along the ground from the start.
        var node = (int)(500 / grid.StepMeters);
        var walked = GeoUtils.HaversineDistance(grid.Lat[0], grid.Lng[0], grid.Lat[node], grid.Lng[node]);
        Assert.Equal(500.0, walked, 0.5);
    }

    [Fact]
    public void Build_CourseShorterThanOneStep_IsRejected()
    {
        var (lat, lng, alt) = Track(3, 2.0, _ => 500.0);     // 4 m long

        Assert.Throws<ArgumentException>(() => RaceCourseGridBuilder.Build(lat, lng, alt, stepMeters: 10.0));
    }

    [Fact]
    public void Build_MismatchedChannelLengths_AreRejected()
    {
        var (lat, lng, _) = Track(10, 10.0, _ => 500.0);

        Assert.Throws<ArgumentException>(
            () => RaceCourseGridBuilder.Build(lat, lng, new double?[5], stepMeters: 10.0));
    }

    // --- Altitude gap filling (sparse elevation is the case this exists for) --------------------------

    [Fact]
    public void FillAltitudeGaps_LeadingTrailingAndInteriorGaps_MatchLinearInterpolation()
    {
        // Head gap holds the first known value, tail gap holds the last, interior gaps interpolate
        // linearly by INDEX. Expected values are hand-computed, so this pins the contract itself and not
        // whatever the implementation happens to do.
        double?[] altitude = [null, null, 100.0, null, null, 400.0, 500.0, null, null];

        var filled = RaceCourseGridBuilder.FillAltitudeGaps(altitude);

        Assert.Equal(new[] { 100.0, 100.0, 100.0, 200.0, 300.0, 400.0, 500.0, 500.0, 500.0 }, filled);
    }

    [Fact]
    public void FillAltitudeGaps_AllNull_IsAFlatCourse()
    {
        var filled = RaceCourseGridBuilder.FillAltitudeGaps(new double?[5]);

        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0, 0.0 }, filled);
    }

    [Fact]
    public void FillAltitudeGaps_ASingleKnownPoint_IsHeldInBothDirections()
    {
        double?[] altitude = [null, null, 742.0, null];

        var filled = RaceCourseGridBuilder.FillAltitudeGaps(altitude);

        Assert.Equal(new[] { 742.0, 742.0, 742.0, 742.0 }, filled);
    }

    [Fact]
    public void FillAltitudeGaps_NoGaps_IsTheInputVerbatim()
    {
        double?[] altitude = [10.0, 20.0, 15.0];

        var filled = RaceCourseGridBuilder.FillAltitudeGaps(altitude);

        Assert.Equal(new[] { 10.0, 20.0, 15.0 }, filled);
    }

    /// <summary>
    /// A DEM refresh leaves NaN wherever it had no data — the Ski to Sea course crosses ~37 km of water.
    /// Read as a value, one NaN spreads through the smoother into every node, and the replay payload then
    /// dies mid-serialization (System.Text.Json refuses to write NaN). It is a GAP, like a null.
    /// </summary>
    [Fact]
    public void FillAltitudeGaps_NonFiniteSamples_AreGapsNotValues()
    {
        double?[] altitude = [10.0, double.NaN, double.PositiveInfinity, 40.0];

        var filled = RaceCourseGridBuilder.FillAltitudeGaps(altitude);

        Assert.Equal(new[] { 10.0, 20.0, 30.0, 40.0 }, filled);
    }

    [Fact]
    public void Build_AnAltitudeChannelFullOfNaN_StillYieldsAFiniteProfile()
    {
        var (lat, lng, alt) = Track(600, 10.0, _ => double.NaN);

        var grid = RaceCourseGridBuilder.Build(lat, lng, alt);

        Assert.All(grid.Elevation, e => Assert.True(double.IsFinite(e)));
        Assert.All(grid.GradePercent, g => Assert.True(double.IsFinite(g)));
    }

    [Fact]
    public void Build_NaNOverTheMiddleOfTheCourse_BridgesTheHole()
    {
        // 6 km of climb, a 2 km hole in the middle (water), then the climb resumes.
        var (lat, lng, alt) = Track(600, 10.0, d => d is > 2000 and < 4000 ? double.NaN : d / 10.0);

        var grid = RaceCourseGridBuilder.Build(lat, lng, alt);

        Assert.All(grid.Elevation, e => Assert.True(double.IsFinite(e)));
        // The bridge is a straight line between the two shores, so mid-hole sits between them.
        var mid = grid.Elevation[grid.Count / 2];
        Assert.InRange(mid, 200.0, 400.0);
    }
}
