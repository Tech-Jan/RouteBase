namespace RouteBaseWeb.Models;

/// <summary>
/// The Segment.SegmentKind vocabulary (D2): "polyline" (drawn path, 6-stage matcher) or "shapes"
/// (rule document, ShapeSegmentMatcher). DB rows + on the wire (SegmentsGridRow.kind,
/// ShapeSegmentResponse.segmentKind — both stamped); renaming a value is a data migration.
/// </summary>
public static class SegmentKinds
{
    public const string Polyline = "polyline";
    public const string Shapes   = "shapes";
}
