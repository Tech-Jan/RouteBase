using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Services;

/// <summary>
/// Shared geodesic math utilities.
/// </summary>
public static class GeoUtils
{
    private const double EarthRadiusMeters = 6371000;

    /// <summary>
    /// Haversine distance between two lat/lng points in metres.
    /// </summary>
    public static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>
    /// <inheritdoc cref="SmoothSpeeds(IList{ExternalGpsPoint},int)"/>
    /// Overload for stored <see cref="GpsData"/> points (e.g. during finalization).
    /// </summary>
    public static void SmoothSpeeds(IList<GpsData> points, int windowSeconds = 5)
        => SmoothSpeedsCore(points, p => p.DateTimeUtc, p => p.Distance, (p, s) => p.Speed = s, windowSeconds);

    /// <summary>
    /// Overwrites the Speed on each point using a sliding time-window average,
    /// matching Strava's velocity_smooth algorithm:
    ///   speed[i] = (distance[i] - distance[j]) / (time[i] - time[j])
    /// where j is the earliest index whose timestamp is within <paramref name="windowSeconds"/> of i.
    /// Points must already have Distance and TimestampUtc populated.
    /// Points without timestamps are left at speed = 0.
    /// </summary>
    public static void SmoothSpeeds(IList<ExternalGpsPoint> points, int windowSeconds = 5)
        => SmoothSpeedsCore(points, p => p.TimestampUtc, p => p.Distance, (p, s) => p.Speed = s, windowSeconds);

    // Shared sliding-window smoothing core; the two overloads differ only in their time property name.
    private static void SmoothSpeedsCore<T>(
        IList<T> points,
        Func<T, DateTime?> time,
        Func<T, double?> distance,
        Action<T, double> setSpeed,
        int windowSeconds)
    {
        if (points.Count == 0) return;

        int j = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var tI = time(points[i]);
            if (tI is null || distance(points[i]) is null)
            {
                setSpeed(points[i], 0);
                continue;
            }

            // Advance j until the window covers at least windowSeconds
            while (j < i - 1)
            {
                var tJ1 = time(points[j + 1]);
                if (tJ1 is null) { j++; continue; }
                if ((tI.Value - tJ1.Value).TotalSeconds >= windowSeconds)
                    j++;
                else
                    break;
            }

            var tJ = time(points[j]);
            if (tJ is null || distance(points[i]) is not double dI || distance(points[j]) is not double dJ)
            {
                setSpeed(points[i], 0);
                continue;
            }

            double dt = (tI.Value - tJ.Value).TotalSeconds;
            double dd = dI - dJ;
            setSpeed(points[i], dt > 0 ? dd / dt : 0);
        }
    }
}
