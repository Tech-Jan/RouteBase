using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Races;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// Fitting the grade→cost curve from a real effort. The reference effort is the COURSE ITSELF — a race
/// course is a ride you actually did — so the synthetic field inherits a real rider's terrain response
/// rather than a constant someone typed in.
/// </summary>
public class RaceCostCurveServiceTests
{
    /// <summary>The effort's content sha — half the curve's cache identity. See FitFromEffortAsync.</summary>
    private const string EffortSha = "effort-sha";

    private const double Lat0 = 47.9;
    private static readonly double MetresPerDegreeLng = 111_320.0 * Math.Cos(Lat0 * Math.PI / 180.0);

    private sealed class FakeTrackReader(DecodedTrack? track) : ITrackReader
    {
        /// <summary>How many times the curve actually went to the track store — the cache pin's evidence.</summary>
        public int Reads { get; private set; }

        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(track);
        }
    }

    /// <summary>
    /// A course whose first half climbs and second half descends, ridden by someone who is
    /// <paramref name="climbSecondsPerMetre"/> up and <paramref name="descentSecondsPerMetre"/> down.
    /// </summary>
    private static (RaceCourseGrid Grid, DecodedTrack Ride) Build(
        double climbSecondsPerMetre, double descentSecondsPerMetre, int points = 401, double spacing = 5.0)
    {
        var lat = new double[points];
        var lng = new double[points];
        var alt = new double?[points];
        var epoch = new double[points];

        var elapsed = 0.0;
        for (var i = 0; i < points; i++)
        {
            var d = i * spacing;
            lat[i] = Lat0;
            lng[i] = 8.1 + d / MetresPerDegreeLng;
            // +10% for the first half, -10% for the second.
            var half = (points - 1) * spacing / 2;
            alt[i] = d <= half ? 500 + d * 0.10 : 500 + half * 0.10 - (d - half) * 0.10;

            if (i > 0) elapsed += spacing * (d <= half ? climbSecondsPerMetre : descentSecondsPerMetre);
            epoch[i] = 1_000_000 + elapsed;
        }

        // A real grid always carries its course's content sha (RaceCourseService stamps it). The cache
        // is keyed on it, so the fixture must supply one or nothing would be cached.
        var grid = RaceCourseGridBuilder.Build(lat, lng, alt) with { SourceSha = "course-sha" };
        var ride = DecodedTrackExtensions.FromDoubleChannels(epoch, lat, lng, null, null, null, null, null, null, null);
        return (grid, ride);
    }

    /// <summary>Each call gets its OWN cache — a shared one would leak fits between tests.</summary>
    private static RaceCostCurveService Service(DecodedTrack? ride) =>
        new(new FakeTrackReader(ride), new MemoryCache(new MemoryCacheOptions()));

    /// <summary>Seconds per metre at a grade, by the same clamped interpolation the browser uses.</summary>
    private static double CostAt(RaceCostCurve curve, double gradePercent)
    {
        var b = gradePercent - curve.MinBin;
        if (b <= 0) return curve.SecondsPerMetre[0];
        if (b >= curve.SecondsPerMetre.Length - 1) return curve.SecondsPerMetre[^1];
        var i = (int)Math.Floor(b);
        return curve.SecondsPerMetre[i] + (b - i) * (curve.SecondsPerMetre[i + 1] - curve.SecondsPerMetre[i]);
    }

    [Fact]
    public async Task Fit_LearnsTheRidersRealTerrainResponse()
    {
        // He grinds up at 0.4 s/m (9 km/h) and lets go down at 0.1 s/m (36 km/h).
        var (grid, ride) = Build(climbSecondsPerMetre: 0.4, descentSecondsPerMetre: 0.1);

        var curve = await Service(ride).FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1));

        Assert.NotNull(curve);
        Assert.InRange(CostAt(curve!, 10), 0.32, 0.48);    // the climb he actually rode
        Assert.InRange(CostAt(curve!, -10), 0.08, 0.13);   // the descent he actually rode
    }

    [Fact]
    public async Task Fit_IsMonotoneInGrade_SoADescentCannotOscillate()
    {
        // THE property. Cost must never fall as the road tilts up; an oscillating descent curve is what
        // flings synthetic riders down the mountain at 90 km/h.
        var (grid, ride) = Build(0.4, 0.1);

        var curve = await Service(ride).FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1));

        Assert.NotNull(curve);
        for (var i = 1; i < curve!.SecondsPerMetre.Length; i++)
            Assert.True(curve.SecondsPerMetre[i] >= curve.SecondsPerMetre[i - 1] - 1e-12,
                $"cost fell from bin {i - 1} to {i} — the curve is not isotonic");
    }

    [Fact]
    public async Task Fit_ImpliesNoAbsurdSpeeds()
    {
        var (grid, ride) = Build(0.4, 0.1);

        var curve = await Service(ride).FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1));

        Assert.NotNull(curve);
        foreach (var cost in curve!.SecondsPerMetre)
        {
            var kmh = 3.6 / cost;
            Assert.InRange(kmh, 1, 90);
        }
    }

    [Fact]
    public async Task Fit_CourseWithNoGps_IsNull()
    {
        var (grid, _) = Build(0.4, 0.1);

        Assert.Null(await Service(null).FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1)));
    }

    [Fact]
    public async Task Fit_CourseWithNoElapsedTime_IsNull()
    {
        // A route with synthesized (constant) timestamps carries no pace to learn. Better to fall back
        // to a default curve than to fit nonsense and call it "his terrain response".
        var (grid, _) = Build(0.4, 0.1);
        var points = 401;
        var lat = new double[points];
        var lng = new double[points];
        var epoch = new double[points];
        for (var i = 0; i < points; i++)
        {
            lat[i] = grid.Lat[Math.Min(i, grid.Count - 1)];
            lng[i] = grid.Lng[Math.Min(i, grid.Count - 1)];
            epoch[i] = 1_000_000;   // every point at the same instant
        }
        var frozen = DecodedTrackExtensions.FromDoubleChannels(epoch, lat, lng, null, null, null, null, null, null, null);

        Assert.Null(await Service(frozen).FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1)));
    }

    /// <summary>
    /// A course whose grade sweeps steadily from -10% to +10%, so each 1% bin is fed by ONE stretch of it
    /// — which is what lets a stretch with no timestamps be seen for what it does to that stretch's bins.
    /// The rider costs 0.15 s/m at 0% and 0.01 s/m more per 1% of grade.
    /// </summary>
    private static (RaceCourseGrid Grid, DecodedTrack Ride) SweptGradeCourse(Func<int, bool>? timestampHole)
    {
        const int points = 401;
        const double spacing = 5.0;
        var lat = new double[points];
        var lng = new double[points];
        var alt = new double?[points];
        var epoch = new double[points];

        var elapsed = 0.0;
        for (var i = 0; i < points; i++)
        {
            var d = i * spacing;
            lat[i] = Lat0;
            lng[i] = 8.1 + d / MetresPerDegreeLng;

            var grade = 0.01 * d - 10.0;                       // -10% at the start, +10% at the finish
            alt[i] = 500 + 0.5e-4 * d * d - 0.10 * d;          // its integral
            if (i > 0) elapsed += spacing * (0.15 + 0.01 * grade);
            epoch[i] = timestampHole?.Invoke(i) == true ? double.NaN : 1_000_000 + elapsed;
        }

        // A real grid always carries its course's content sha (RaceCourseService stamps it). The cache
        // is keyed on it, so the fixture must supply one or nothing would be cached.
        var grid = RaceCourseGridBuilder.Build(lat, lng, alt) with { SourceSha = "course-sha" };
        var ride = DecodedTrackExtensions.FromDoubleChannels(epoch, lat, lng, null, null, null, null, null, null, null);
        return (grid, ride);
    }

    /// <summary>
    /// THE Ski to Sea bug. A stretch of the effort carries no timestamps — a relay course stitched out of
    /// legs, one of them imported without a clock. Those points read back as NaN (TrackChannel), so their
    /// costs are NaN, and the plausibility bounds cannot reject them: every comparison with NaN is false.
    /// Waved through, they take over their bins' medians, PAVA spreads them across the neighbours, and the
    /// curve ships as NaN. System.Text.Json then refuses to write it and kills the response MID-STREAM —
    /// the browser sees HTTP 200 with a truncated body (ERR_INCOMPLETE_CHUNKED_ENCODING) and the replay
    /// page never opens.
    ///
    /// It hid until the course's elevation was RECALCULATED. With no elevation every grade is 0, so every
    /// sample fell in a single bin, the fit gave up (fewer than 3 solid bins) and the browser quietly used
    /// its default curve. Real elevation spread the samples across bins — and the curve that was finally
    /// emitted was full of NaN.
    /// </summary>
    [Fact]
    public async Task Fit_AStretchOfTheEffortWithNoTimestamps_DoesNotPoisonTheCurve()
    {
        // 400 m of the course (grades 0% to +4%) with no clock — every sample in those bins is NaN.
        var (grid, ride) = SweptGradeCourse(timestampHole: i => i is >= 200 and < 280);

        var curve = await Service(ride).FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1));

        Assert.NotNull(curve);
        Assert.All(curve!.SecondsPerMetre, c => Assert.True(double.IsFinite(c), "the curve carries a NaN"));
    }

    [Fact]
    public async Task Fit_AnEffortWithNoTimestampAtAll_IsNull()
    {
        // Nothing survives the fit, so there is nothing to learn — the browser falls back to its default
        // curve. What it must NOT do is emit a curve of NaN.
        var (grid, ride) = SweptGradeCourse(timestampHole: _ => true);

        Assert.Null(await Service(ride).FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1)));
    }

    // --- Caching (the fit is a pure function of the course; it was redone on EVERY replay request) -----

    [Fact]
    public async Task FitFromEffortAsync_SecondCallHitsCache()
    {
        var (grid, ride) = Build(0.4, 0.1);
        var reader = new FakeTrackReader(ride);
        var service = new RaceCostCurveService(reader, new MemoryCache(new MemoryCacheOptions()));

        await service.FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1));
        await service.FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1));

        Assert.Equal(1, reader.Reads);   // the grid beside it is cached 30 min; the curve now matches
    }

    [Fact]
    public async Task FitFromEffortAsync_CachedCurveEqualsFreshCurve()
    {
        var (grid, ride) = Build(0.4, 0.1);
        var service = new RaceCostCurveService(
            new FakeTrackReader(ride), new MemoryCache(new MemoryCacheOptions()));

        var cold = await service.FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1));
        var warm = await service.FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1));

        Assert.NotNull(cold);
        Assert.Equal(cold!.MinBin, warm!.MinBin);
        Assert.Equal(cold.SecondsPerMetre, warm.SecondsPerMetre);   // element for element
    }

    [Fact]
    public async Task FitFromEffortAsync_AnUnfittableCourse_CachesItsNull()
    {
        // Otherwise the course that can NEVER produce a curve is the one that redoes the whole fit on
        // every single page load.
        var (grid, _) = Build(0.4, 0.1);
        var reader = new FakeTrackReader(null);
        var service = new RaceCostCurveService(reader, new MemoryCache(new MemoryCacheOptions()));

        Assert.Null(await service.FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1)));
        Assert.Null(await service.FitFromEffortAsync(new TrackId(1), EffortSha, grid, new TrackId(1)));

        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task FitFromEffortAsync_DifferentCourses_DoNotShareACurve()
    {
        var (gridA, rideA) = Build(0.4, 0.1);
        var reader = new FakeTrackReader(rideA);
        var service = new RaceCostCurveService(reader, new MemoryCache(new MemoryCacheOptions()));

        await service.FitFromEffortAsync(new TrackId(1), EffortSha, gridA, new TrackId(1));
        await service.FitFromEffortAsync(new TrackId(2), EffortSha, gridA, new TrackId(2));   // a different course track id

        Assert.Equal(2, reader.Reads);
    }

    /// <summary>
    /// The cache key used to be the COURSE id alone — true only while the effort WAS always the course.
    /// Now that a race can name its own reference effort, two races over one course with different
    /// reference rides would serve each other's curve: a pacing bug that reads as bad physics rather
    /// than as a cache bug, and one no fixture would ever catch by accident.
    /// </summary>
    [Fact]
    public async Task FitFromEffortAsync_SameCourse_DifferentEfforts_DoNotShareACurve()
    {
        var (grid, ride) = Build(0.4, 0.1);
        var reader = new FakeTrackReader(ride);
        var service = new RaceCostCurveService(reader, new MemoryCache(new MemoryCacheOptions()));

        await service.FitFromEffortAsync(effortTrackId: new TrackId(7), effortSha: EffortSha, grid, courseTrackId: new TrackId(1));
        await service.FitFromEffortAsync(effortTrackId: new TrackId(9), effortSha: EffortSha, grid, courseTrackId: new TrackId(1));

        Assert.Equal(2, reader.Reads);
    }

    /// <summary>
    /// Recalculating a course's elevation rebuilds its grid under a new content sha. The curve is fitted to
    /// that grid's GRADES, so it must be refitted — under a key of track ids alone it was not, and the whole
    /// field went on being paced by the terrain from before the recalculation until the process restarted.
    /// </summary>
    [Fact]
    public async Task FitFromEffortAsync_ARecalculatedCourse_RefitsTheCurve()
    {
        var (grid, ride) = Build(0.4, 0.1);
        var reader = new FakeTrackReader(ride);
        var service = new RaceCostCurveService(reader, new MemoryCache(new MemoryCacheOptions()));

        await service.FitFromEffortAsync(new TrackId(1), EffortSha, grid with { SourceSha = "before-refresh" }, new TrackId(1));
        await service.FitFromEffortAsync(new TrackId(1), EffortSha, grid with { SourceSha = "after-refresh" }, new TrackId(1));

        Assert.Equal(2, reader.Reads);
    }

    /// <summary>
    /// Editing the REFERENCE EFFORT must refit the curve.
    ///
    /// The key already carried the course grid's sha, on the argument that "recalculating the course's
    /// elevation must refit". But the curve is fitted from the EFFORT's timestamps and positions — and the
    /// moment a race could name an effort OTHER than its course (Race.ReferenceTrackId, the whole reason
    /// this method exists), the very same hole opened on that axis: trimming or DEM-refreshing the
    /// reference ride moves ITS head object while the course's sha never moves, so the key held still and
    /// the entire field went on being paced by the pre-edit ride for the full 30-minute TTL.
    ///
    /// Same ids, same grid — only the effort's content changed.
    /// </summary>
    [Fact]
    public async Task FitFromEffortAsync_WhenTheEffortIsEdited_Refits()
    {
        var (grid, ride) = Build(0.4, 0.1);
        var reader = new FakeTrackReader(ride);
        var service = new RaceCostCurveService(reader, new MemoryCache(new MemoryCacheOptions()));

        await service.FitFromEffortAsync(new TrackId(1), "before-the-trim", grid, courseTrackId: new TrackId(2));
        await service.FitFromEffortAsync(new TrackId(1), "after-the-trim", grid, courseTrackId: new TrackId(2));

        Assert.Equal(2, reader.Reads);
    }

    /// <summary>
    /// An effort with no content version cannot be identified, so its curve must not be cached — the same
    /// rule RaceCourseService applies to the grid. Cached under a key that cannot vary, a stale curve
    /// would be pinned to a grid that IS rebuilt live on every request.
    /// </summary>
    [Fact]
    public async Task FitFromEffortAsync_WithNoContentSha_IsNotCached()
    {
        var (grid, ride) = Build(0.4, 0.1);
        var reader = new FakeTrackReader(ride);
        var service = new RaceCostCurveService(reader, new MemoryCache(new MemoryCacheOptions()));

        await service.FitFromEffortAsync(new TrackId(1), effortSha: null, grid, courseTrackId: new TrackId(2));
        await service.FitFromEffortAsync(new TrackId(1), effortSha: null, grid, courseTrackId: new TrackId(2));

        Assert.Equal(2, reader.Reads);   // refitted, not served from a key it cannot key on
    }

    /// <summary>
    /// The same ride, with a contiguous run of points that have NO GPS FIX — what a water crossing or a
    /// tunnel leaves behind. The grid is built from the SURVIVING points, because that is exactly what
    /// RaceCourseService does before gridding; the effort track keeps the holes as NaN, which is how
    /// TrackChannel reads a per-point NULL.
    /// </summary>
    private static (RaceCourseGrid Grid, DecodedTrack Ride) BuildWithGpsHole(
        double climbSecondsPerMetre, double descentSecondsPerMetre,
        int holeStart, int holeCount, int points = 401, double spacing = 5.0)
    {
        var lat = new double[points];
        var lng = new double[points];
        var alt = new double?[points];
        var epoch = new double[points];

        var elapsed = 0.0;
        for (var i = 0; i < points; i++)
        {
            var d = i * spacing;
            lat[i] = Lat0;
            lng[i] = 8.1 + d / MetresPerDegreeLng;
            var half = (points - 1) * spacing / 2;
            alt[i] = d <= half ? 500 + d * 0.10 : 500 + half * 0.10 - (d - half) * 0.10;
            if (i > 0) elapsed += spacing * (d <= half ? climbSecondsPerMetre : descentSecondsPerMetre);
            epoch[i] = 1_000_000 + elapsed;
        }

        bool InHole(int i) => i >= holeStart && i < holeStart + holeCount;
        var kept = Enumerable.Range(0, points).Where(i => !InHole(i)).ToList();

        // The grid: only the points that HAVE a fix. Its chainage therefore runs straight across the gap.
        var grid = RaceCourseGridBuilder.Build(
            kept.Select(i => lat[i]).ToArray(),
            kept.Select(i => lng[i]).ToArray(),
            kept.Select(i => alt[i]).ToArray()) with { SourceSha = "course-sha" };

        // The effort: the holes are still in it, as NaN.
        var rideLat = (double[])lat.Clone();
        var rideLng = (double[])lng.Clone();
        foreach (var i in Enumerable.Range(holeStart, holeCount))
        {
            rideLat[i] = double.NaN;
            rideLng[i] = double.NaN;
        }
        var ride = DecodedTrackExtensions.FromDoubleChannels(
            epoch, rideLat, rideLng, null, null, null, null, null, null, null);

        return (grid, ride);
    }

    /// <summary>
    /// A GPS hole must not put the effort's arc length on a DIFFERENT SCALE from the grid's.
    ///
    /// RaceCourseService drops a point with no fix before gridding, so the grid measures the chord straight
    /// across the gap. ArrivalPerNode bridged the same gap by adding ZERO metres — so the effort's arc
    /// stopped advancing exactly where the grid's kept going (1795 m against 2000 m here), and every node
    /// past the hole was matched to an arrival from the wrong stretch of the ride. The last 205 m of grid
    /// then all clamped to the final arrival, so their samples fell out of the fit entirely.
    ///
    /// These are two measurements of ONE ride — the default reference effort IS the course track — and
    /// they are supposed to agree. The curve they disagree about is what paces the whole field.
    ///
    /// The hole here is deliberately large (650 m of a 2 km course). A SMALL hole does not move the fitted
    /// curve, and that is not luck — each bin takes a MEDIAN, so contaminating a fifth of a bin's samples
    /// changes nothing. That is exactly why this survived: it degrades quietly, in proportion to the hole,
    /// with no threshold at which anything visibly breaks. At this size the climb bin fitted at 0.10 s/m
    /// instead of 0.40 — the model believed the rider climbed at descent pace.
    ///
    /// This course canoes and kayaks across water. The holes are the reason the NaN guards exist at all.
    /// </summary>
    [Fact]
    public async Task FitFromEffortAsync_AGpsHole_DoesNotShiftTheCurve()
    {
        var (cleanGrid, cleanRide) = Build(0.4, 0.1);
        var clean = await Service(cleanRide).FitFromEffortAsync(new TrackId(1), EffortSha, cleanGrid, new TrackId(1));

        var (holedGrid, holedRide) = BuildWithGpsHole(0.4, 0.1, holeStart: 60, holeCount: 130);
        var holed = await Service(holedRide).FitFromEffortAsync(new TrackId(1), EffortSha, holedGrid, new TrackId(1));

        Assert.NotNull(clean);
        Assert.NotNull(holed);

        // He rode the same ride at the same pace. Losing a few fixes must not change his physics.
        Assert.Equal(CostAt(clean!, 10), CostAt(holed!, 10), 2);
        Assert.Equal(CostAt(clean, -10), CostAt(holed, -10), 2);
    }

    [Fact]
    public void Pava_PoolsAnOscillatingDescentIntoAPlateau()
    {
        // The real artefact from the spike: one technical switchback bin read SLOWER than the bins 5%
        // either side of it. Isotonic regression absorbs it without anyone hand-picking outliers — and
        // the descent speed CEILING falls out for free as the pooled plateau.
        double[] raw = [0.076, 0.205, 0.074, 0.111, 0.161, 0.324];
        double[] weights = [10, 10, 10, 10, 10, 10];

        var fitted = RaceCostCurveService.Pava(raw, weights);

        for (var i = 1; i < fitted.Length; i++)
            Assert.True(fitted[i] >= fitted[i - 1] - 1e-12);
        Assert.Equal(raw.Length, fitted.Length);
        // The 0.205 spike is gone: it cannot stand above its steeper-descent neighbours any more.
        Assert.True(fitted[1] < 0.205);
    }
}
