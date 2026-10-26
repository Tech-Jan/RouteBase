using RouteBaseWeb.Models.Queue;

namespace RouteBaseWeb.Services.Segments;

/// <summary>One segment's analysis; null threshold → the user's configured default.</summary>
public sealed record SingleSegmentAnalysisPayload(string UserId, int SegmentId, int? MatchThresholdPercent) : OperationPayload(UserId);
