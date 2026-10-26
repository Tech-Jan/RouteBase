using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// Value object capturing the triple <c>(segmentId, userId, friendIds)</c> for a single-segment
/// analysis run. The engine adapters (MatchEngineService / EffortMathService) read
/// <see cref="SegmentId"/> / <see cref="UserId"/> directly; the only SQL filter it still emits is the
/// own-effort <see cref="DeleteFilter"/> used by SegmentService.Analysis.cs.
/// </summary>
public sealed class AnalysisScope
{
    public SegmentId SegmentId { get; }
    public string UserId { get; }
    public IReadOnlyList<string> FriendIds { get; }

    public AnalysisScope(SegmentId segmentId, string userId, List<string> friendIds)
    {
        SegmentId = segmentId;
        UserId = userId;
        FriendIds = friendIds;
    }

    /// <summary>
    /// Returns a complete WHERE clause for DELETE FROM GPSinSegment, scoping to the
    /// current user's own activities on the segment. Used by Analysis.cs.
    /// </summary>
    public string DeleteFilter(SqlBatch b)
    {
        b.Param("$userId", UserId);
        b.Param("$segmentId", SegmentId.Value);
        return "WHERE segment_id = $segmentId AND track_id IN (SELECT track_ID FROM Activity WHERE user_id = $userId)";
    }
}
