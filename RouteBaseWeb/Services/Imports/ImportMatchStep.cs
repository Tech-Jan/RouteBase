using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services.Imports;

/// <summary>
/// Shared post-import matching step, used by both import handlers (moved verbatim from
/// OperationQueueWorker; CR5-01 "NOT duplicated"). Threshold resolution is Segments' policy —
/// see <see cref="ISegmentAnalysis.GetEffectiveMatchThresholdAsync"/>. Lives with the import
/// slice, not in Services/Queue: only the import handlers call it — the queue layer must know
/// nothing about what operations do.
/// </summary>
internal static class ImportMatchStep
{
    /// <summary>
    /// Incremental per-import match (inval-06): matches every freshly imported track against its
    /// candidate segments, run once after the import batch drains. Uses the user's default
    /// threshold (no explicit override at import time), so the produced rows are equivalent to a
    /// full analyze-all for those tracks (inval-05). Candidate-segment bboxes are aggregated once
    /// for the batch and per-track failures are isolated inside
    /// <see cref="ISegmentAnalysis.MatchNewTracksAsync"/>.
    /// </summary>
    public static async Task MatchImportedTracksAsync(
        IServiceScope scope, string userId, IReadOnlyCollection<int> importedTrackIds, CancellationToken ct)
    {
        if (importedTrackIds.Count == 0) return;

        var segmentService = scope.ServiceProvider.GetRequiredService<ISegmentAnalysis>();
        int matchThreshold = await segmentService.GetEffectiveMatchThresholdAsync(userId, null);

        await segmentService.MatchNewTracksAsync(importedTrackIds.ToList(), userId, matchThreshold, ct);
    }
}
