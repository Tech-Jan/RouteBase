using RouteBaseWeb.Helpers;

namespace RouteBaseWeb.Services;

/// <summary>
/// Computes a difficulty-weighted "effort cost" (cumulative kilojoules) for a single segment
/// effort from its ordered GPS slices. The cost integrates each step of the effort's own path,
/// so steeper / longer lines accumulate more cost regardless of segment match%.
///
/// Source priority (per effort):
///   1. Measured power meter  → mechanical work  ∫ max(P,0)·dt
///   2. No power, running     → Minetti grade-adjusted metabolic cost × rider mass, over distance
///   3. No power, cycling/etc → physics power model (gravity + rolling + aero + inertia) × time
///
/// Pure and persistence-ignorant so it can be unit-tested in isolation.
/// </summary>
internal static class EffortCostCalculator
{
    // Physics constants for the estimated cycling model (named so they can be tuned later).
    private const double G   = 9.81;   // gravitational acceleration (m/s²)
    private const double Rho = 1.225;  // air density at sea level (kg/m³)
    private const double Crr = 0.005;  // rolling-resistance coefficient (road tyre)
    private const double CdA = 0.30;   // drag area (m²) — road bike on the hoods

    // Fraction of points that must carry positive power for an effort to count as "power measured".
    private const double PowerCoverageThreshold = 0.5;

    // Grade is clamped before feeding the Minetti polynomial / atan to avoid GPS-spike blowups.
    private const double MaxGrade = 0.45;

    public sealed record CostResult(double[] CumulativeKj, bool Estimated);

    /// <summary>
    /// Returns the per-point cumulative cost (kJ) array (same length as <paramref name="pts"/>,
    /// index 0 == 0) plus whether the cost was estimated (no power meter).
    /// </summary>
    public static CostResult Compute(IReadOnlyList<GpsSlice> pts, string? activityType, double riderMassKg)
    {
        int n = pts.Count;
        var cum = new double[n];
        if (n < 2) return new CostResult(cum, true);

        bool hasPower = HasPowerMeter(pts);
        // Canonical classifier — walk group covers hiking (EffortCostCalculator once forked its own
        // "hike"-substring test that Minetti-missed "Hiking"; use the shared helper instead).
        var typeGroup = ActivityTypeHelper.GetTypeGroup(activityType);
        bool isRun    = typeGroup is "run" or "walk";

        for (int i = 1; i < n; i++)
        {
            var prev = pts[i - 1];
            var curr = pts[i];

            double dt = StepSeconds(prev, curr);
            double ds = StepDistance(prev, curr);
            double incr;

            if (hasPower)
            {
                // Measured mechanical work: integrate positive power over elapsed time.
                double p = curr.Power ?? prev.Power ?? 0;
                incr = Math.Max(p, 0) * dt / 1000.0;
            }
            else
            {
                double grade = Grade(prev, curr, ds);
                if (isRun)
                {
                    // Minetti metabolic cost C(i) [J/(kg·m)] integrated over distance travelled.
                    incr = MinettiCost(grade) * riderMassKg * ds / 1000.0;
                }
                else
                {
                    // Physics power model integrated over time.
                    double v     = curr.Speed ?? (dt > 0 ? ds / dt : 0);              // m/s, fall back to path speed
                    double theta = Math.Atan(grade);
                    double a     = dt > 0 ? ((curr.Speed ?? 0) - (prev.Speed ?? 0)) / dt : 0;
                    double force = riderMassKg * G * (Math.Sin(theta) + Crr * Math.Cos(theta))
                                 + 0.5 * Rho * CdA * v * v;
                    double power = (force + riderMassKg * a) * v;
                    incr = Math.Max(power, 0) * dt / 1000.0;
                }
            }

            if (double.IsNaN(incr) || double.IsInfinity(incr) || incr < 0) incr = 0;
            cum[i] = cum[i - 1] + incr;
        }

        return new CostResult(cum, !hasPower);
    }

    private static bool HasPowerMeter(IReadOnlyList<GpsSlice> pts)
    {
        int positive = 0;
        foreach (var p in pts)
            if (p.Power.HasValue && p.Power.Value > 0) positive++;
        return positive >= pts.Count * PowerCoverageThreshold;
    }

    private static double StepSeconds(GpsSlice prev, GpsSlice curr)
    {
        // UTC is the only per-point timestamp (GPS.datetime_loc dropped — SL-01).
        if (prev.DateTimeUtc.HasValue && curr.DateTimeUtc.HasValue)
            return Math.Max(0, (curr.DateTimeUtc.Value - prev.DateTimeUtc.Value).TotalSeconds);
        return 0;
    }

    private static double StepDistance(GpsSlice prev, GpsSlice curr)
    {
        if (prev.Distance.HasValue && curr.Distance.HasValue)
        {
            double d = curr.Distance.Value - prev.Distance.Value;
            if (d >= 0) return d;
        }
        // SE-01: missing endpoint coords → zero-length leg (not a haversine to Null Island (0,0)),
        // which the CumulativeCostKj NaN/Infinity guard would not catch. Callers treat ds == 0 correctly
        // (Minetti incr = 0; the physics model falls back to curr.Speed).
        if (!(prev.GpsLat.HasValue && prev.GpsLong.HasValue && curr.GpsLat.HasValue && curr.GpsLong.HasValue))
            return 0;
        return GeoUtils.HaversineDistance(
            prev.GpsLat.Value, prev.GpsLong.Value, curr.GpsLat.Value, curr.GpsLong.Value);
    }

    /// <summary>
    /// Grade as a rise/run fraction. Prefers the recorded smoothed grade; otherwise derives it
    /// from the altitude delta over the horizontal run (path distance with the climb removed).
    /// </summary>
    private static double Grade(GpsSlice prev, GpsSlice curr, double ds)
    {
        if (curr.GradeSmooth.HasValue)
            return Math.Clamp(curr.GradeSmooth.Value / 100.0, -MaxGrade, MaxGrade);

        double dh = (curr.Altitude ?? 0) - (prev.Altitude ?? 0);
        double run = ds * ds > dh * dh ? Math.Sqrt(ds * ds - dh * dh) : ds;
        if (run <= 0) return 0;
        return Math.Clamp(dh / run, -MaxGrade, MaxGrade);
    }

    /// <summary>Minetti energy cost of running, J/(kg·m), as a function of gradient i (rise/run).</summary>
    private static double MinettiCost(double i)
        => 155.4 * Pow(i, 5) - 30.4 * Pow(i, 4) - 43.3 * Pow(i, 3) + 46.3 * i * i + 19.5 * i + 3.6;

    private static double Pow(double x, int n)
    {
        double r = 1;
        for (int k = 0; k < n; k++) r *= x;
        return r;
    }
}
