namespace RouteBaseWeb.Services.Matching;

/// <summary>Axis-aligned lat/lng bounding box.</summary>
public readonly record struct Bbox(double MinLat, double MaxLat, double MinLng, double MaxLng);

/// <summary>
/// Single source of the segment↔track candidate-selection geometry: the ±0.002° segment pad and the
/// inclusive-overlap predicate. Both the whole-segment match (<see cref="MatchEngineService"/>) and the
/// incremental per-import match (SegmentAnalysis) select candidates through these, so the two filters
/// can never drift. Pure logic, no I/O — unit-testable without mocks.
/// </summary>
public static class SegmentBboxGeometry
{
    /// <summary>Asymmetric ±0.002° pad applied to a segment bbox (metres-asymmetric; ported as-is).</summary>
    public const double SegmentPadDegrees = 0.002;

    /// <summary>Pad a raw segment bbox by <see cref="SegmentPadDegrees"/> on both axes.</summary>
    public static Bbox PadSegment(Bbox raw) => new(
        raw.MinLat - SegmentPadDegrees, raw.MaxLat + SegmentPadDegrees,
        raw.MinLng - SegmentPadDegrees, raw.MaxLng + SegmentPadDegrees);

    /// <summary>Inclusive bbox overlap on both axes.</summary>
    public static bool Overlaps(Bbox a, Bbox b) =>
        a.MaxLat >= b.MinLat && a.MinLat <= b.MaxLat &&
        a.MaxLng >= b.MinLng && a.MinLng <= b.MaxLng;
}
