namespace RouteBaseWeb.Models.Geometry;

/// <summary>
/// An axis-aligned rectangle on the lat/long plane — the only <see cref="IShape"/>
/// implemented today. Because it is axis-aligned, its bounding box is itself, so
/// <see cref="Contains"/> is a plain range check.
///
/// Pure value type: no EF, no map, no SQL → unit-testable in isolation.
/// (Circle / rotated rectangle would be future <see cref="IShape"/> implementations.)
/// </summary>
public sealed class GeoRect : IShape
{
    public double MinLat { get; }
    public double MaxLat { get; }
    public double MinLong { get; }
    public double MaxLong { get; }

    public GeoRect(double minLat, double maxLat, double minLong, double maxLong)
    {
        // Normalise so callers can pass opposite corners in any order.
        MinLat = Math.Min(minLat, maxLat);
        MaxLat = Math.Max(minLat, maxLat);
        MinLong = Math.Min(minLong, maxLong);
        MaxLong = Math.Max(minLong, maxLong);
    }

    public GeoBoundingBox Bbox => new(MinLat, MaxLat, MinLong, MaxLong);

    public bool Contains(double lat, double lng) =>
        lat >= MinLat && lat <= MaxLat && lng >= MinLong && lng <= MaxLong;

    /// <summary>
    /// Liang–Barsky segment-vs-AABB clip: true when the segment intersects the rectangle,
    /// including the both-endpoints-inside and pure fly-through cases. lat is the y axis,
    /// lng the x axis — same equirectangular convention as the rest of the matcher.
    /// </summary>
    public bool IntersectsSegment(double lat0, double lng0, double lat1, double lng1)
    {
        double dx = lng1 - lng0, dy = lat1 - lat0;
        // Degenerate segment (single point) → plain containment.
        if (dx == 0 && dy == 0) return Contains(lat0, lng0);

        double t0 = 0, t1 = 1;
        // Four clip edges: left, right, bottom, top. p = direction into the edge, q = distance to it.
        Span<double> p = [-dx, dx, -dy, dy];
        Span<double> q = [lng0 - MinLong, MaxLong - lng0, lat0 - MinLat, MaxLat - lat0];

        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0)
            {
                if (q[i] < 0) return false;   // parallel to this edge and outside the slab
                continue;
            }
            double r = q[i] / p[i];
            if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else          { if (r < t0) return false; if (r < t1) t1 = r; }
        }
        return t0 <= t1;
    }
}
