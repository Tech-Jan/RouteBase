using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Services;

/// <summary>Computed per-track aggregates derived from GPS points (device totals override these upstream).</summary>
public sealed record ComputedTrackStats(
    double Distance,
    double ElevationGain,
    double ElevationLoss,
    DateTime? StartUtc,
    DateTime? EndUtc,
    double? ElapsedSeconds,
    double? AverageSpeed,
    double? MovingTime,
    double? MaxSpeed,
    double? AverageHeartRate,
    double? MaxHeartRate,
    double? AverageCadence,
    double? MaxCadence,
    double? AveragePower,
    double? MaxPower);

/// <summary>
/// Shared post-processing for every activity-file parser (GPX/TCX/FIT): fills each point's cumulative
/// haversine distance + smoothed speed, then derives track aggregates. Extracted so all three formats
/// produce identical chart/stat inputs (rule of three). Pure — no I/O.
/// </summary>
public static class TrackStatsCalculator
{
    // Keep in sync with GpsNormalizationService.
    private const double MovingSpeedThreshold = 0.5;

    /// <summary>
    /// Fills <see cref="ExternalGpsPoint.Distance"/> (cumulative metres) and <see cref="ExternalGpsPoint.Speed"/>
    /// (smoothed) on <paramref name="points"/>, then returns the derived aggregates. Caller must have set
    /// lat/lng/altitude/HR/cadence/power/timestamp; <paramref name="hasTimestamps"/> gates time-based stats.
    /// </summary>
    public static ComputedTrackStats Compute(List<ExternalGpsPoint> points, bool hasTimestamps)
    {
        if (points.Count == 0)
            throw new ArgumentException("points must be non-empty", nameof(points));

        // Per-point cumulative distance + speed (mirrors Strava's distance/velocity_smooth streams).
        double cumulativeDistance = 0;
        points[0].Distance = 0;
        points[0].Speed    = 0;
        for (int i = 1; i < points.Count; i++)
        {
            cumulativeDistance += GeoUtils.HaversineDistance(
                points[i - 1].Lat, points[i - 1].Lng, points[i].Lat, points[i].Lng);
            points[i].Distance = cumulativeDistance;
        }
        GeoUtils.SmoothSpeeds(points);

        var firstPt = points[0];
        var lastPt  = points[^1];

        DateTime? startUtc = hasTimestamps ? firstPt.TimestampUtc : null;
        DateTime? endUtc   = hasTimestamps ? lastPt.TimestampUtc  : null;

        ComputeElevation(points, out double elevGain, out double elevLoss);

        double? elapsedSec = (hasTimestamps && startUtc.HasValue && endUtc.HasValue)
            ? (endUtc.Value - startUtc.Value).TotalSeconds
            : null;
        if (elapsedSec < 0) elapsedSec = null; // out-of-order timestamps must not yield negative elapsed

        double? avgSpeed = (cumulativeDistance > 0 && elapsedSec is > 0)
            ? cumulativeDistance / elapsedSec.Value
            : null;

        // Moving time + max speed from the smoothed per-point speeds (GI-02: persisted directly).
        double? movingTime = null, maxSpeed = null;
        if (hasTimestamps)
        {
            double moving = 0, max = 0;
            for (int i = 1; i < points.Count; i++)
            {
                var prev = points[i - 1];
                var cur  = points[i];
                if (cur.Speed is > MovingSpeedThreshold &&
                    prev.TimestampUtc.HasValue && cur.TimestampUtc.HasValue)
                {
                    var dt = (cur.TimestampUtc.Value - prev.TimestampUtc.Value).TotalSeconds;
                    if (dt > 0) moving += dt; // backwards timestamps must not subtract moving time
                }
                if (cur.Speed.HasValue && cur.Speed.Value > max) max = cur.Speed.Value;
            }
            movingTime = moving;
            maxSpeed   = max;
        }

        // HR / cadence / power avg+max in one pass (was six LINQ passes, each allocating a list).
        // Max seeding mirrors Enumerable.Max's NaN rule so results stay bit-identical.
        double hrSum = 0, cadSum = 0, powSum = 0;
        int hrN = 0, cadN = 0, powN = 0;
        double hrMax = 0, cadMax = 0, powMax = 0;
        foreach (var p in points)
        {
            if (p.HeartRate is double hr)
            {
                hrSum += hr;
                if (++hrN == 1 || hr > hrMax || double.IsNaN(hrMax)) hrMax = hr;
            }
            if (p.Cadence is double cad)
            {
                cadSum += cad;
                if (++cadN == 1 || cad > cadMax || double.IsNaN(cadMax)) cadMax = cad;
            }
            if (p.Power is double pow)
            {
                powSum += pow;
                if (++powN == 1 || pow > powMax || double.IsNaN(powMax)) powMax = pow;
            }
        }

        return new ComputedTrackStats(
            Distance:         cumulativeDistance,
            ElevationGain:    elevGain,
            ElevationLoss:    elevLoss,
            StartUtc:         startUtc,
            EndUtc:           endUtc,
            ElapsedSeconds:   elapsedSec,
            AverageSpeed:     avgSpeed,
            MovingTime:       movingTime,
            MaxSpeed:         maxSpeed,
            AverageHeartRate: hrN  > 0 ? hrSum  / hrN  : null,
            MaxHeartRate:     hrN  > 0 ? hrMax  : null,
            AverageCadence:   cadN > 0 ? cadSum / cadN : null,
            MaxCadence:       cadN > 0 ? cadMax : null,
            AveragePower:     powN > 0 ? powSum / powN : null,
            MaxPower:         powN > 0 ? powMax : null);
    }

    /// <summary>
    /// Deterministic 64-bit id for timestamp-less files (no wall-clock to key on): FNV-1a over point
    /// count, first/last lat/lng (6-dp), and total distance — same file → same id across restarts, so
    /// re-import dedupe-skips. Masked positive, clear of the small filename-id range. (GI-07.)
    /// </summary>
    public static long ComputeContentId(List<ExternalGpsPoint> pts, double totalDistance)
    {
        const ulong FnvOffset = 14695981039346656037;
        const ulong FnvPrime  = 1099511628211;
        ulong hash = FnvOffset;

        void Fold(byte[] bytes)
        {
            foreach (var b in bytes) { hash ^= b; hash *= FnvPrime; }
        }

        var first = pts[0];
        var last  = pts[^1];
        Fold(BitConverter.GetBytes(pts.Count));
        Fold(BitConverter.GetBytes(Math.Round(first.Lat, 6)));
        Fold(BitConverter.GetBytes(Math.Round(first.Lng, 6)));
        Fold(BitConverter.GetBytes(Math.Round(last.Lat, 6)));
        Fold(BitConverter.GetBytes(Math.Round(last.Lng, 6)));
        Fold(BitConverter.GetBytes((long)totalDistance));

        return (long)(hash & 0x7FFF_FFFF_FFFF_FFFF);
    }

    /// <summary>
    /// Name of the first non-finite persisted stat on <paramref name="s"/>, or null when all are
    /// finite/absent. Import-seam guard (A1, 2026-07-20): parsers finite-check each VALUE, but sums can
    /// overflow during aggregation (elevation deltas, HR/cadence/power averages), and one persisted
    /// Infinity 500s every JSON response containing the activity. The router fails the parse on a
    /// non-null return. Lat/lng are excluded — they are range-checked per point.
    /// </summary>
    public static string? FindNonFiniteAggregate(ExternalActivitySummary s)
    {
        if (!FiniteOrNull(s.Distance))         return nameof(s.Distance);
        if (!FiniteOrNull(s.TotalTime))        return nameof(s.TotalTime);
        if (!FiniteOrNull(s.MovingTime))       return nameof(s.MovingTime);
        if (!FiniteOrNull(s.ElapsedTime))      return nameof(s.ElapsedTime);
        if (!FiniteOrNull(s.ElevationGain))    return nameof(s.ElevationGain);
        if (!FiniteOrNull(s.TotalDescent))     return nameof(s.TotalDescent);
        if (!FiniteOrNull(s.AverageSpeed))     return nameof(s.AverageSpeed);
        if (!FiniteOrNull(s.MaxSpeed))         return nameof(s.MaxSpeed);
        if (!FiniteOrNull(s.AverageHeartRate)) return nameof(s.AverageHeartRate);
        if (!FiniteOrNull(s.MaxHeartRate))     return nameof(s.MaxHeartRate);
        if (!FiniteOrNull(s.AverageCadence))   return nameof(s.AverageCadence);
        if (!FiniteOrNull(s.MaxCadence))       return nameof(s.MaxCadence);
        if (!FiniteOrNull(s.AveragePower))     return nameof(s.AveragePower);
        if (!FiniteOrNull(s.MaxPower))         return nameof(s.MaxPower);
        if (!FiniteOrNull(s.Calories))         return nameof(s.Calories);
        return null;
    }

    private static bool FiniteOrNull(double? v) => !v.HasValue || double.IsFinite(v.Value);

    // OBS-1 (2026-07-22) physical sanity ceilings — deliberately generous (orders of magnitude past
    // any real activity, calibrated against the live data: worst real distance ~268 km, ascent
    // ~11.6 km, speed-glitch ~289 m/s) so only clearly-corrupt device/lap totals — double.MaxValue
    // brags and negative sign errors — are rejected, never a large-but-real ultra. Every stat also
    // has an implicit floor of 0: no activity has a negative distance/ascent/speed/time/count.
    private const double MaxDistanceMeters   = 1e8;   // 100,000 km
    private const double MaxElevationMeters  = 1e6;   // 1,000 km of climb/descent
    private const double MaxSeconds          = 1e8;   // ~3.2 years
    private const double MaxSpeedMps         = 1e4;   // 36,000 km/h
    private const double MaxHeartRate        = 1e4;   // bpm
    private const double MaxCadence          = 1e4;   // rpm / spm
    private const double MaxPower            = 1e6;   // watts
    private const double MaxCalories         = 1e7;   // kcal

    /// <summary>
    /// Name of the first persisted stat outside its accepted physical range (negative, or above a
    /// generous ceiling), or null when all are in range/absent. Import-seam guard (OBS-1): device,
    /// lap, and extension totals are finite-checked per value but never range-checked, so a
    /// double.MaxValue distance or a negative ascent/calorie total persists as-is. The router fails
    /// the parse on a non-null return. Run AFTER <see cref="FindNonFiniteAggregate"/>, so values are
    /// already finite here; the checks stay robust to non-finite input regardless (NaN/Inf fail).
    /// Lat/lng are excluded — range-checked per point.
    /// </summary>
    public static string? FindOutOfRangeStat(ExternalActivitySummary s)
    {
        if (!InRange(s.Distance, MaxDistanceMeters))        return nameof(s.Distance);
        if (!InRange(s.ElevationGain, MaxElevationMeters))  return nameof(s.ElevationGain);
        if (!InRange(s.TotalDescent, MaxElevationMeters))   return nameof(s.TotalDescent);
        if (!InRange(s.TotalTime, MaxSeconds))              return nameof(s.TotalTime);
        if (!InRange(s.MovingTime, MaxSeconds))             return nameof(s.MovingTime);
        if (!InRange(s.ElapsedTime, MaxSeconds))            return nameof(s.ElapsedTime);
        if (!InRange(s.AverageSpeed, MaxSpeedMps))          return nameof(s.AverageSpeed);
        if (!InRange(s.MaxSpeed, MaxSpeedMps))              return nameof(s.MaxSpeed);
        if (!InRange(s.AverageHeartRate, MaxHeartRate))     return nameof(s.AverageHeartRate);
        if (!InRange(s.MaxHeartRate, MaxHeartRate))         return nameof(s.MaxHeartRate);
        if (!InRange(s.AverageCadence, MaxCadence))         return nameof(s.AverageCadence);
        if (!InRange(s.MaxCadence, MaxCadence))             return nameof(s.MaxCadence);
        if (!InRange(s.AveragePower, MaxPower))             return nameof(s.AveragePower);
        if (!InRange(s.MaxPower, MaxPower))                 return nameof(s.MaxPower);
        if (!InRange(s.Calories, MaxCalories))              return nameof(s.Calories);
        return null;
    }

    // In range = absent, or within [0, max]. NaN/Infinity fail both comparisons, so they are
    // reported as out of range too (belt-and-suspenders behind FindNonFiniteAggregate).
    private static bool InRange(double? v, double max) => !v.HasValue || (v.Value >= 0 && v.Value <= max);

    private static void ComputeElevation(List<ExternalGpsPoint> points, out double gain, out double loss)
    {
        gain = 0; loss = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (!points[i].Altitude.HasValue || !points[i - 1].Altitude.HasValue) continue;
            var delta = points[i].Altitude!.Value - points[i - 1].Altitude!.Value;
            if (delta > 0) gain += delta;
            else loss += -delta;
        }
    }

}
