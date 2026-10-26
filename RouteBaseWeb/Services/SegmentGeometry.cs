using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services;

/// <summary>
/// Pure geometry helpers owned by <see cref="SegmentCrud"/>: Ramer-Douglas-Peucker simplification,
/// distance and elevation calculation, and geo-cell encoding. No database or cache access —
/// all methods are stateless.
/// </summary>
internal static class SegmentGeometry
{
    internal static List<SegmentGpsPoint> SimplifySegmentPoints(List<SegmentGpsPoint> points, double tolerance)
    {
        return points.Count <= 2 ? points : RamerDouglasPeucker(points, tolerance);
    }

    /// <summary>
    /// Upper bound on the point count handed to <see cref="RamerDouglasPeucker"/>. RDP's recursion depth
    /// is O(n) on adversarial geometry and its per-call scan makes it O(n²) worst-case, so an unbounded
    /// input can StackOverflow (uncatchable → process crash) or pin a core for minutes. Bounding the
    /// *input* removes both risks, which is what lets a whole-activity polyline of any length be accepted.
    /// </summary>
    internal const int RdpWorkingCap = 5000;

    /// <summary>
    /// Uniform stride down to at most <paramref name="maxPoints"/> points, always preserving the first
    /// and last point. Used to bound the working set before simplification. Callers compute distance and
    /// elevation on the RAW points beforehand, so this thinning does not affect stored accuracy — it only
    /// caps the geometry RDP has to chew through.
    /// </summary>
    internal static List<SegmentGpsPoint> StrideDownsample(List<SegmentGpsPoint> points, int maxPoints)
    {
        if (maxPoints < 2 || points.Count <= maxPoints) return points;

        var kept = StrideIndices(points.Count, maxPoints);
        var result = new List<SegmentGpsPoint>(kept.Count);
        foreach (var i in kept) result.Add(points[i]);
        return result;
    }

    /// <summary>
    /// The indices <see cref="StrideDownsample"/> keeps — the same stride, exposed so a caller holding
    /// PARALLEL channels (race replay: lat/lng/elapsed/chainage) can thin them all to the SAME indices.
    /// Channels of differing length would be a bug, and a second stride implementation would be a second
    /// engine. Returns the identity indices when no thinning is needed.
    /// </summary>
    internal static List<int> StrideIndices(int count, int maxPoints)
    {
        if (maxPoints < 2 || count <= maxPoints)
            return Enumerable.Range(0, Math.Max(count, 0)).ToList();

        int stride = (count + maxPoints - 1) / maxPoints; // ceil → kept count ≤ maxPoints
        var kept = new List<int>(count / stride + 2);
        for (int i = 0; i < count; i += stride)
            kept.Add(i);
        if ((count - 1) % stride != 0) // the stride missed the true endpoint — keep it
        {
            // …without busting the budget: maxPoints is a hard cap (a rider's wire payload is sized on
            // it), so when the stride already filled it, the endpoint REPLACES the last strided point.
            if (kept.Count >= maxPoints) kept.RemoveAt(kept.Count - 1);
            kept.Add(count - 1);
        }
        return kept;
    }

    internal static List<SegmentGpsPoint> RamerDouglasPeucker(List<SegmentGpsPoint> points, double epsilon)
    {
        if (points.Count < 3) return new List<SegmentGpsPoint>(points);

        var result = new List<SegmentGpsPoint>();
        RdpRecurse(points, 0, points.Count - 1, epsilon, result);
        result.Add(points[^1]);
        return result;
    }

    private static void RdpRecurse(
        List<SegmentGpsPoint> points, int start, int end, double epsilon, List<SegmentGpsPoint> result)
    {
        if (end - start < 2)
        {
            result.Add(points[start]);
            return;
        }

        double dmax = 0;
        int index = start;

        for (int i = start + 1; i < end; i++)
        {
            double d = PerpendicularDistance(points[i], points[start], points[end]);
            if (d > dmax) { dmax = d; index = i; }
        }

        if (dmax > epsilon)
        {
            RdpRecurse(points, start, index, epsilon, result);
            RdpRecurse(points, index, end, epsilon, result);
        }
        else
        {
            result.Add(points[start]);
        }
    }

    private static double PerpendicularDistance(SegmentGpsPoint p, SegmentGpsPoint a, SegmentGpsPoint b)
    {
        double dx = b.Lng - a.Lng, dy = b.Lat - a.Lat;
        double mag = Math.Sqrt(dx * dx + dy * dy);
        if (mag > 0) { dx /= mag; dy /= mag; }

        double pvx = p.Lng - a.Lng, pvy = p.Lat - a.Lat;
        double dot = dx * pvx + dy * pvy;
        double ax = pvx - dot * dx, ay = pvy - dot * dy;
        return Math.Sqrt(ax * ax + ay * ay);
    }

    internal static double CalculateDistance(List<SegmentGpsPoint> points)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++)
            total += GeoUtils.HaversineDistance(points[i-1].Lat, points[i-1].Lng, points[i].Lat, points[i].Lng);
        return total;
    }

    // SE-06: sum deltas between CONSECUTIVE KNOWN altitudes, bridging null gaps. Treating a null
    // altitude as 0 m (create-from-track points legitimately carry null altitudes from barometer/DEM
    // gaps) fabricated a full descent+ascent of the terrain height around each gap, permanently
    // corrupting the stored SegmentAscent/SegmentDescent. Leading/trailing nulls contribute nothing;
    // all-null → (0, 0), same as before.
    internal static (double ascent, double descent) CalculateElevation(List<SegmentGpsPoint> points)
    {
        double asc = 0, desc = 0;
        double? prevAlt = null;
        foreach (var p in points)
        {
            if (p.Altitude is not double a) continue;
            if (prevAlt is double prev)
            {
                double d = a - prev;
                if (d > 0) asc += d; else desc += Math.Abs(d);
            }
            prevAlt = a;
        }
        return (asc, desc);
    }
}
