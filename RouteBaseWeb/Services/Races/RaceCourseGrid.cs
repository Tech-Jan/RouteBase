using RouteBaseWeb.Services.Elevation;

namespace RouteBaseWeb.Services.Races;

/// <summary>
/// The shared course geometry every participant is positioned against: a uniform chainage grid with
/// position, elevation and grade at each node.
///
/// ONE of these serves the whole field. Simulated riders carry only a time column over it (~8 MB for
/// 424 riders at 10 m spacing), which is what makes a 500-rider replay affordable. Real riders are
/// drawn from their own GPS and only borrow this for chainage.
/// </summary>
public sealed record RaceCourseGrid
{
    /// <summary>Chainage in metres — uniform, <c>Distance[i] == i * StepMeters</c>.</summary>
    public required double[] Distance { get; init; }
    public required double[] Lat { get; init; }
    public required double[] Lng { get; init; }
    /// <summary>Smoothed elevation (ElevationProfileSmoother — raw DEM/barometric noise would wreck the grade).</summary>
    public required double[] Elevation { get; init; }
    /// <summary>Grade in PERCENT at each node, over <see cref="GradeWindowMeters"/>.</summary>
    public required double[] GradePercent { get; init; }
    public required double StepMeters { get; init; }

    /// <summary>
    /// The sha of the track content this grid was built from — set by RaceCourseService, null for a grid
    /// built straight from the pure builder.
    ///
    /// It is the grid's CACHE IDENTITY: anything derived from a grid (the cost curve is) must carry it in
    /// its own key, or a course's elevation recalculation moves the grid while the derived thing goes on
    /// serving what it fitted to the OLD terrain.
    /// </summary>
    public string? SourceSha { get; init; }

    public int Count => Distance.Length;
    public double TotalMeters => Distance.Length == 0 ? 0 : Distance[^1];
}

/// <summary>
/// Builds a <see cref="RaceCourseGrid"/> from raw track points. Pure — no DB, no cache, no track store,
/// so the resampling and grade maths are unit-testable without mocks (the I/O lives in RaceCourseService).
/// </summary>
public static class RaceCourseGridBuilder
{
    public const double DefaultStepMeters = 10.0;

    /// <summary>
    /// Grade window. Matches the 100 m windows the course profile was characterised with; anything much
    /// tighter turns DEM ripple into phantom 20% ramps that the pacing model would then "slow down" for.
    /// </summary>
    public const double GradeWindowMeters = 100.0;

    /// <summary>
    /// Resample a track onto a uniform chainage grid. Altitude is smoothed BEFORE resampling (on the raw
    /// arc length, which is what ElevationProfileSmoother expects) — smoothing after would smooth an
    /// already-interpolated signal and understate real slopes.
    /// </summary>
    public static RaceCourseGrid Build(
        IReadOnlyList<double> lat, IReadOnlyList<double> lng, IReadOnlyList<double?> altitude,
        double stepMeters = DefaultStepMeters)
    {
        var n = lat.Count;
        if (n < 2 || lng.Count != n || altitude.Count != n)
            throw new ArgumentException("lat/lng/altitude must be the same length, and at least 2 points.");
        if (stepMeters <= 0) throw new ArgumentException("stepMeters must be positive.", nameof(stepMeters));

        // Cumulative arc length over the raw points.
        var arc = new double[n];
        for (var i = 1; i < n; i++)
            arc[i] = arc[i - 1] + GeoUtils.HaversineDistance(lat[i - 1], lng[i - 1], lat[i], lng[i]);

        var total = arc[^1];
        if (total <= stepMeters)
            throw new ArgumentException("Course is shorter than one grid step.", nameof(stepMeters));

        // Null altitudes (imports without elevation) bridge from their neighbours — a 0 m stand-in would
        // read as a cliff and the pacing model would brake for it.
        var alt = FillAltitudeGaps(altitude);
        var smoothed = ElevationProfileSmoother.SmoothByArcLength(arc, alt);

        var count = (int)Math.Floor(total / stepMeters) + 1;
        var gd = new double[count];
        var glat = new double[count];
        var glng = new double[count];
        var gele = new double[count];

        var j = 0;
        for (var i = 0; i < count; i++)
        {
            var d = i * stepMeters;
            gd[i] = d;
            while (j < n - 2 && arc[j + 1] < d) j++;
            var span = arc[j + 1] - arc[j];
            var f = span <= 0 ? 0 : Math.Clamp((d - arc[j]) / span, 0, 1);
            glat[i] = lat[j] + f * (lat[j + 1] - lat[j]);
            glng[i] = lng[j] + f * (lng[j + 1] - lng[j]);
            gele[i] = smoothed[j] + f * (smoothed[j + 1] - smoothed[j]);
        }

        return new RaceCourseGrid
        {
            Distance = gd,
            Lat = glat,
            Lng = glng,
            Elevation = gele,
            GradePercent = ComputeGrades(gd, gele, stepMeters),
            StepMeters = stepMeters,
        };
    }

    /// <summary>Grade (%) over a centred <see cref="GradeWindowMeters"/> window, clamped at the ends.</summary>
    private static double[] ComputeGrades(double[] distance, double[] elevation, double stepMeters)
    {
        var n = distance.Length;
        var half = Math.Max(1, (int)Math.Round(GradeWindowMeters / stepMeters / 2));
        var grade = new double[n];
        for (var i = 0; i < n; i++)
        {
            var lo = Math.Max(0, i - half);
            var hi = Math.Min(n - 1, i + half);
            var dd = distance[hi] - distance[lo];
            grade[i] = dd <= 0 ? 0 : (elevation[hi] - elevation[lo]) / dd * 100.0;
        }
        return grade;
    }

    /// <summary>
    /// Linear-interpolate missing altitudes between known neighbours; flat-fill if none are known.
    ///
    /// A non-finite sample (NaN/±∞) counts as MISSING, not as a value. A DEM refresh leaves NaN wherever it
    /// had no data — over water, say — and one NaN taken for a number spreads through the smoother into
    /// every node of the profile, which then cannot even be serialized as JSON.
    ///
    /// One forward pass. Both cursors are monotone, so this is O(n) — it used to re-scan the whole known
    /// index list for every gap point, which is O(n²) on exactly the tracks this function exists for
    /// (sparse FIT/TCX elevation, a merged multi-day ride): ~450M comparisons on a 30k-point course, paid
    /// on the first uncached replay request.
    /// </summary>
    internal static double[] FillAltitudeGaps(IReadOnlyList<double?> altitude)
    {
        var n = altitude.Count;
        var alt = new double[n];

        bool Known(int i) => altitude[i] is { } v && double.IsFinite(v);

        var first = -1;
        for (var i = 0; i < n; i++)
        {
            if (!Known(i)) continue;
            first = i;
            break;
        }
        if (first < 0) return alt;   // no elevation at all → flat course, grade 0 everywhere

        var prev = -1;      // last index with a value
        double prevVal = 0; // ... and that value, captured at the guard (H2 — no nullable re-derefs)
        var next = first;   // next index with a value, at or after i; only ever moves forward
        for (var i = 0; i < n; i++)
        {
            if (altitude[i] is double known && double.IsFinite(known))
            {
                alt[i] = known;
                prev = i;
                prevVal = known;
                continue;
            }

            while (next < n && (next <= i || !Known(next))) next++;
            double? nextVal = next < n ? altitude[next] : null;

            // `first` proved a value exists, so the leading-gap branch always has one ahead;
            // ?? 0 is the unreachable arm the types demand.
            if (prev < 0) alt[i] = nextVal ?? 0;                 // leading gap → hold the first value
            else if (nextVal is not double nv) alt[i] = prevVal; // trailing gap → hold the last value
            else
            {
                var t = (double)(i - prev) / (next - prev);
                alt[i] = prevVal + t * (nv - prevVal);
            }
        }
        return alt;
    }
}
