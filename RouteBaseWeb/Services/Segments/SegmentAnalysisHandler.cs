using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Queue;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Queue;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// <see cref="OperationQueue.OperationType.SegmentAnalysis"/>: analyze-all over the user's
/// segments. Body moved verbatim from OperationQueueWorker.RunAnalysisAsync (CR5-01/QW-01).
/// </summary>
public class SegmentAnalysisHandler : IOperationHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SegmentAnalysisProgress _analysisProgress;
    private readonly ILogger<SegmentAnalysisHandler> _logger;

    public SegmentAnalysisHandler(
        IServiceScopeFactory scopeFactory,
        SegmentAnalysisProgress analysisProgress,
        ILogger<SegmentAnalysisHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _analysisProgress = analysisProgress;
        _logger = logger;
    }

    public OperationQueue.OperationType Type => OperationQueue.OperationType.SegmentAnalysis;

    public void FailProgress(string userId, string message) => _analysisProgress.Fail(userId, message);

    public void SetQueueInfo(string userId, int position, int total) => _analysisProgress.SetQueueInfo(userId, position, total);
    public QueueProgressSnapshot GetQueueProgress(string userId, int pendingCountForUser) =>
        _analysisProgress.BuildQueueProgress(userId, pendingCountForUser);
    public void Acknowledge(string userId) => _analysisProgress.Acknowledge(userId);

    public async Task RunAsync(OperationPayload payload, CancellationToken ct)
    {
        string userId = ((SegmentAnalysisPayload)payload).UserId;

        using var scope = OperationScope.Create(_scopeFactory, userId);
        var context = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
        var segmentService = scope.ServiceProvider.GetRequiredService<ISegmentAnalysis>();

        int matchThreshold = await segmentService.GetEffectiveMatchThresholdAsync(userId, null);

        var segments = await context.Segments
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.SegmentName)
            .Select(s => new { s.SegmentId, s.SegmentName })
            .ToListAsync(ct);

        if (!_analysisProgress.TryStart(userId, segments.Count))
        {
            _logger.LogWarning("Analysis already marked running, forcing start");
            _analysisProgress.ForceStart(userId, segments.Count);
        }

        try
        {
            _logger.LogInformation("OperationQueue: Segment analysis starting - {Count} segments", segments.Count);

            // SB-02: each segment is delete-then-rebuilt by its own per-segment analyze. The old upfront
            // ClearAllSegmentMatchesAsync wiped EVERY segment before the loop, so a Cancel (a normal UI
            // action) or a crash mid-run left every not-yet-reached segment with zero efforts — worse
            // than doing nothing. Track processed ids and run a narrowed safety-net wipe AFTER a clean
            // full pass instead; cancellation/exception skips it, so prior efforts survive.
            var processedIds = new List<SegmentId>(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var seg = segments[i];
                _analysisProgress.UpdateProgress(userId, i + 1, seg.SegmentName ?? $"Segment {seg.SegmentId}", "Analyzing...");
                _logger.LogInformation("OperationQueue: Segment {Current}/{Total}: {Name}",
                    i + 1, segments.Count, seg.SegmentName);
                await segmentService.AnalyzeSegmentMatchesAsync(seg.SegmentId, userId, matchThreshold, ct);
                processedIds.Add(seg.SegmentId);
            }

            // Clean up own-efforts of any segment that vanished mid-run (never reached by the loop).
            await segmentService.ClearOrphanedSegmentMatchesAsync(userId, processedIds, ct);

            _analysisProgress.Complete(userId);
            _logger.LogInformation("OperationQueue: Segment analysis done");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "OperationQueue: Segment analysis failed");
            _analysisProgress.Fail(userId, ex.Message);
        }
    }
}
