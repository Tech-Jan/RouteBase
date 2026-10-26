using RouteBaseWeb.Models.Queue;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>Heatmap render for one shapes segment (row already flipped to pending by Attach).</summary>
public sealed record HeatmapGenerationPayload(string UserId, int SegmentId) : OperationPayload(UserId);
