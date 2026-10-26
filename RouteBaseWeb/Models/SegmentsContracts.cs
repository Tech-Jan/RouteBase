using System.Text.Json;
using RouteBaseWeb.Services;

namespace RouteBaseWeb.Models;

// Response contracts for SegmentsApiController. Serialized camelCase (web defaults);
// wire shapes pinned by SegmentsApiWireShapeTests. Each mirrors a TS type — edit both
// in the same commit.

/// <summary>Response of GET /api/SegmentsApi (list).
/// Mirrors TS <c>SegmentItem</c> (dashboard/dashboard-widgets.ts) / <c>ActivitySegmentSummary</c> (activity/activity-types.ts).</summary>
public sealed record SegmentSummaryDto(
    int SegmentId, string? SegmentName, double? SegmentDistance,
    double? SegmentAscent, double? SegmentDescent, string? ActivityType);

/// <summary>Response of GET /api/SegmentsApi/cards?limit=N — exactly the fields a dashboard segment
/// card paints. Deliberately NOT SegmentSummaryDto: that one grows with the segments page's needs,
/// while this is the card contract (PERF-4). Mirrors TS <c>SegmentCardDto</c>.</summary>
public sealed record SegmentCardDto(
    int SegmentId, string? SegmentName, double? SegmentDistance, double? SegmentAscent);

/// <summary>
/// 409 body when segment analysis is already running. Carries more than the sanctioned bare
/// <c>{error}</c> convention (the caller's live progress snapshot) — unread by the TS client.
/// </summary>
// Uniform error body (D32): `{ code, message, status }` (this body is always a 409).
public sealed record SegmentAnalysisConflictBody(string Message, AnalysisProgressSnapshot Status, string Code = ApiErrorCodes.Conflict);

/// <summary>Response of POST /analyze-all and POST /{id}/analyze on success (202 Accepted). Unread by the TS client.</summary>
public sealed record AnalysisQueuedResponse(string Message);

/// <summary>Response of POST /api/SegmentsApi (create polyline segment). Mirrors the subset TS reads
/// (activity/activity-segments.ts, segment-create/segment-create.ts).</summary>
public sealed record CreatedSegmentResponse(
    int SegmentId, string? SegmentName, double? SegmentDistance, double? SegmentAscent,
    double? SegmentDescent, string? ActivityType, bool AnalysisQueued);

/// <summary>Response of POST/PUT .../shapes (create/update shape segment).</summary>
public sealed record ShapeSegmentResponse(
    int SegmentId, string? SegmentName,
    [property: WireValues(typeof(SegmentKinds))] string SegmentKind,
    string? ActivityType, bool AnalysisQueued);

/// <summary>Response of GET /{id}/shape-segment. Mirrors TS <c>EditShapeSegment</c> (overview/shape-filter/shape-filter.ts).</summary>
public sealed record ShapeSegmentEditResponse(
    string? SegmentName, string? ActivityType, string[]? MatchOwnerIds, JsonElement Rules);

/// <summary>Response of POST shapes/preview, and the early-return branch of shapes/preview/exact
/// (no shapes drawn yet / invalid rules). Mirrors TS <c>PreviewResult</c>'s base fields
/// (overview/shape-filter/shape-filter-service.ts).</summary>
public sealed record ShapePreviewResponse(int Count, IReadOnlyList<int> TrackIds);

/// <summary>Response of the successful branch of POST shapes/preview/exact — adds the effort count and
/// truncation flag PreviewResult declares optional (only ever sent by this branch).</summary>
public sealed record ShapePreviewExactResponse(int Count, IReadOnlyList<int> TrackIds, int EffortCount, bool Truncated);

/// <summary>Detailed effort projection served by GET /{id}/efforts-detailed — a TRIMMED subset of
/// SegmentEffortDetailed (no cross-user/offset/exclusion fields; this endpoint's TS consumer,
/// activity/activity-segments.ts, never reads them). Mirrors the like-named TS interface
/// (activity/activity-types.ts) — that interface over-declares the full entity shape; this DTO
/// pins what actually goes on the wire here.</summary>
public sealed record SegmentEffortSummaryDto(
    int GpsInSegmentId, int SegmentId, string? SegmentName, int ActivityId, string? ActivityName,
    string? ActivityDate, string? ActivityType, int MatchPercentage, double Distance, double ElapsedTime,
    double? AverageSpeed, double? AverageHeartRate, double? AverageCadence, double? AveragePower,
    double Elevation, int GpsIdEntry, int GpsIdExit);

/// <summary>Zone rectangle of GET /{id}/shapes. Mirrors TS <c>ShapeRect</c> (core/core-types.ts).</summary>
public sealed record ZoneRectDto(double MinLat, double MaxLat, double MinLong, double MaxLong, string Role);

/// <summary>One directed gate line of GET /{id}/shapes. Two endpoints, not a rect — a gate segment has
/// no zones, so without this a gate-only segment has no drawable outline. Mirrors TS <c>GateLine</c>
/// (core/core-types.ts).</summary>
public sealed record GateLineDto(double Lat1, double Lng1, double Lat2, double Lng2, string Role);

/// <summary>Response of GET /{id}/shapes — the drawable outline of a shape segment: zone rectangles plus
/// gate lines (either list may be empty). Was a bare List&lt;ZoneRectDto&gt;; gates were added so a
/// gate-only segment still has an outline. Mirrors TS <c>SegmentOutline</c> (core/core-types.ts).</summary>
public sealed record SegmentOutlineDto(IReadOnlyList<ZoneRectDto> Zones, IReadOnlyList<GateLineDto> Gates);

/// <summary>One matched effort's simplified lat/lng polyline (minimap preview of a shape/gate segment).</summary>
public sealed record EffortPolylineDto(IReadOnlyList<LatLngDto> Points);

/// <summary>Response of GET /{id}/efforts-summary — every visible matched effort's downsampled track
/// through a shape/gate segment, for the list minimap. <c>Truncated</c> flags the effort set was capped.
/// Mirrors TS <c>EffortsSummary</c> (core/core-types.ts).</summary>
public sealed record EffortsSummaryDto(IReadOnlyList<EffortPolylineDto> Efforts, bool Truncated);

/// <summary>Body of PATCH /api/SegmentsApi/{id}/visibility.</summary>
public sealed record UpdateSegmentVisibilityRequest(string Visibility);
