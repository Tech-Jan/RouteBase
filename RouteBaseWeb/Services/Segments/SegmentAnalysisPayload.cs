using RouteBaseWeb.Models.Queue;

namespace RouteBaseWeb.Services.Segments;

/// <summary>Analyze-all: every segment owned by the user.</summary>
public sealed record SegmentAnalysisPayload(string UserId) : OperationPayload(UserId);
