using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Background job entry point for rendering a heatmap-kind segment's tile pyramid.
/// Resolves matching tracks, paints them, builds the pyramid, persists tiles, and
/// updates the Heatmap row's status/metadata.
/// </summary>
public interface IHeatmapGenerationService
{
    /// <summary>
    /// Generate (or regenerate) the heatmap for <paramref name="segmentId"/>. Owns the full
    /// lifecycle: status running → done, or failed (+error) on exception; a cancellation leaves
    /// status=failed "Cancelled" with no orphan tiles.
    /// </summary>
    Task GenerateAsync(SegmentId segmentId, string userId, CancellationToken ct = default);
}
