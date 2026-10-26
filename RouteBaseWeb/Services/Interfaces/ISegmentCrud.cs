using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>Slim projection of a segment's kind + rule document (no GPS graph).</summary>
public sealed record SegmentRuleFields(string? SegmentKind, string? MatchRulesJson);

/// <summary>
/// Segment CRUD: listing, paging, existence, creation (polyline + shape), in-place shape edits,
/// deletion, and downsampled GPS summaries. The heatmap row + tile lifecycle is owned elsewhere (B2),
/// no longer on this interface.
/// </summary>
public interface ISegmentCrud
{
    Task<List<Segment>> GetAllSegmentsAsync();
    /// <summary>The N segments the dashboard card shows, projected in SQL — the widget used to fetch
    /// EVERY visible segment to render three rows (PERF-4). Same visibility gate, same ordering.</summary>
    Task<List<SegmentCardDto>> GetTopSegmentCardsAsync(int limit);
    /// <summary>Cheap existence check — AnyAsync stops at the first matching row.</summary>
    Task<bool> SegmentExistsAsync(SegmentId segmentId);
    Task<List<Segment>> GetSegmentsForActivityAsync(TrackId activityId);
    Task<Segment?> GetSegmentWithGpsAsync(SegmentId segmentId);
    /// <summary>Kind + rule document only (visibility-scoped, no GPS graph) — for consumers that
    /// read the match rules and nothing else (e.g. the /shapes minimap endpoint).</summary>
    Task<SegmentRuleFields?> GetSegmentRulesAsync(SegmentId segmentId);
    /// <summary>Owner-scoped fetch (no GPS graph) — 404-no-leak for non-owner access (SA-03).</summary>
    Task<Segment?> GetOwnedSegmentAsync(SegmentId segmentId);
    Task<Segment> CreateSegmentAsync(string segmentName, string activityType, List<SegmentGpsPoint> gpsPoints);

    /// <summary>
    /// Creates a shape-defined segment (segment_kind = "shapes") from an already-validated
    /// rule document (canonical JSON, see Services.Segments.Rules.RuleDocument). Owner scope now lives
    /// inside the document (an <c>owner</c> track filter), folded in by the controller — no separate column.
    /// </summary>
    Task<Segment> CreateShapeSegmentAsync(string segmentName, string activityType, string rulesJson);

    /// <summary>
    /// Overwrites an existing shape-defined segment in place (by id): replaces its name,
    /// activity type, and rule document. Returns null if the segment is missing, not owned
    /// by the current user, or not a shape segment. Efforts are rebuilt by re-running
    /// analysis (the caller queues it), so this method does not touch GPSinSegment.
    /// </summary>
    Task<Segment?> UpdateShapeSegmentAsync(SegmentId segmentId, string segmentName, string activityType, string rulesJson);

    /// <summary>
    /// Sets a segment's visibility (private/friends/public). Owner-only; false when the value is not a
    /// recognised visibility, or the segment is not the caller's (not-yours and not-there are the same
    /// answer, so this cannot enumerate other people's segment ids).
    /// </summary>
    Task<bool> UpdateSegmentVisibilityAsync(SegmentId segmentId, string visibility);

    Task<bool> DeleteSegmentAsync(SegmentId segmentId);

    /// <summary>
    /// Returns up to <paramref name="maxPoints"/> downsampled lat/lng pairs for a segment
    /// without loading the full GPS entity. Cached. Used by minimap previews.
    /// </summary>
    Task<List<GpsCoordinate>> GetSegmentGpsSummaryAsync(SegmentId segmentId, int maxPoints = 50);
}
