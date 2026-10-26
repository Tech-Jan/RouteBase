namespace RouteBaseWeb.Models;

// Response contracts for HeatmapsApiController. Serialized camelCase (web defaults);
// wire shapes pinned by HeatmapsApiWireShapeTests. The metadata shape mirrors the TS
// HeatmapMeta interface (segment-stats/heatmap-layer.ts) — edit both in the same commit.

/// <summary>
/// 202 body of POST /api/heatmaps/{segmentId} — echoes the id whose render was enqueued.
/// Neither TS caller (heatmap-controls.ts generate, shape-filter.ts attachHeatmap) reads it;
/// named per the DTO convention regardless.
/// </summary>
public sealed record HeatmapAcceptedResponse(int SegmentId);

/// <summary>
/// Success body of GET /api/heatmaps/{segmentId} — metadata + generation status, the single
/// poll/display shape. Zoom/normalization/bounds/generatedAt are null until a render completes;
/// <paramref name="Error"/> is the failure message of a 'failed' render (success-body field,
/// not the {error} error convention).
/// </summary>
public sealed record HeatmapMetaResponse(
    int SegmentId,
    [property: WireValues(typeof(HeatmapStatuses))] string Status,
    string? Error,
    string Layer,
    int? MinZoom,
    int? MaxZoom,
    double? CountMax,
    HeatmapBoundsDto? Bounds,
    string? ActivityTypes,
    DateTimeOffset? GeneratedAt);

/// <summary>Geographic bounds of the rendered tile pyramid (bounds key of <see cref="HeatmapMetaResponse"/>).</summary>
public sealed record HeatmapBoundsDto(double South, double West, double North, double East);
