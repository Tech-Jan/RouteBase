namespace RouteBaseWeb.Models.Geometry;

/// <summary>
/// A painted region used to define a shape-based segment.
///
/// The whole matcher is written against this abstraction, so adding circles or
/// rotated rectangles later means writing a new implementation — not touching
/// the matcher, the store, or the UI orchestration.
///
/// Three responsibilities only:
///   * <see cref="Bbox"/>            — the cheap AABB used by the Activity_Bbox pre-filter.
///   * <see cref="Contains"/>        — the precise per-point test used by the confirm stage.
///   * <see cref="IntersectsSegment"/> — the per-edge test that catches a fast track
///     segment flying through the shape between two outside samples.
/// </summary>
public interface IShape
{
    GeoBoundingBox Bbox { get; }

    bool Contains(double lat, double lng);

    /// <summary>True when the segment (lat0,lng0)→(lat1,lng1) touches the shape, even when
    /// both endpoints lie outside it. Lets "passes/avoids" survive sparse sampling.</summary>
    bool IntersectsSegment(double lat0, double lng0, double lat1, double lng1);
}
