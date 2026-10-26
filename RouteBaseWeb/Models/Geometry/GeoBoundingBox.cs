namespace RouteBaseWeb.Models.Geometry;

/// <summary>
/// An axis-aligned latitude/longitude bounding box.
///
/// Pure value type — knows nothing about EF, SQL, or the map. It exists so the
/// matcher's cheap pre-filter (does a shape's box overlap an activity's box?)
/// can be expressed once and reused by every <see cref="IShape"/>.
///
/// Antimeridian wrap is intentionally not handled — this app's activities are
/// regional, and the existing Activity_Bbox pipeline makes the same assumption.
/// </summary>
public readonly struct GeoBoundingBox
{
    public double MinLat { get; }
    public double MaxLat { get; }
    public double MinLong { get; }
    public double MaxLong { get; }

    public GeoBoundingBox(double minLat, double maxLat, double minLong, double maxLong)
    {
        // Normalise so callers can pass corners in any order.
        MinLat = Math.Min(minLat, maxLat);
        MaxLat = Math.Max(minLat, maxLat);
        MinLong = Math.Min(minLong, maxLong);
        MaxLong = Math.Max(minLong, maxLong);
    }

    /// <summary>
    /// True when this box and <paramref name="other"/> overlap (touching edges count).
    /// This is the same AABB test the segment pipeline runs against Activity_Bbox.
    /// </summary>
    public bool Overlaps(GeoBoundingBox other) =>
        MaxLat >= other.MinLat && MinLat <= other.MaxLat &&
        MaxLong >= other.MinLong && MinLong <= other.MaxLong;

    /// <summary>True when the point lies inside the box (inclusive edges).</summary>
    public bool Contains(double lat, double lng) =>
        lat >= MinLat && lat <= MaxLat && lng >= MinLong && lng <= MaxLong;

    /// <summary>True when <paramref name="other"/> lies fully inside this box (inclusive edges).</summary>
    public bool Contains(GeoBoundingBox other) =>
        other.MinLat >= MinLat && other.MaxLat <= MaxLat &&
        other.MinLong >= MinLong && other.MaxLong <= MaxLong;
}
