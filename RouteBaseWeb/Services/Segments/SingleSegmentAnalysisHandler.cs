using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Queue;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// <see cref="OperationQueue.OperationType.SingleSegmentAnalysis"/>: one segment's analysis,
/// triggered after segment creation/edit or an explicit re-analyze. Body moved verbatim from
/// OperationQueueWorker.RunSingleSegmentAnalysisAsync (CR5-01/QW-01).
/// </summary>
public class SingleSegmentAnalysisHandler : IOperationHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SegmentAnalysisProgress _analysisProgress;
    private readonly ILogger<SingleSegmentAnalysisHandler> _logger;

    public SingleSegmentAnalysisHandler(
        IServiceScopeFactory scopeFactory,
        SegmentAnalysisProgress analysisProgress,
        ILogger<SingleSegmentAnalysisHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _analysisProgress = analysisProgress;
        _logger = logger;
    }

    public OperationQueue.OperationType Type => OperationQueue.OperationType.SingleSegmentAnalysis;

    public void FailProgress(string userId, string message) => _analysisProgress.Fail(userId, message);

    public void SetQueueInfo(string userId, int position, int total) => _analysisProgress.SetQueueInfo(userId, position, total);
    public QueueProgressSnapshot GetQueueProgress(string userId, int pendingCountForUser) =>
        _analysisProgress.BuildQueueProgress(userId, pendingCountForUser);
    public void Acknowledge(string userId) => _analysisProgress.Acknowledge(userId);

    public async Task RunAsync(OperationPayload payload, CancellationToken ct)
    {
        var p = (SingleSegmentAnalysisPayload)payload;
        string userId = p.UserId;
        var segmentId = new SegmentId(p.SegmentId);

        using var scope = OperationScope.Create(_scopeFactory, userId);
        var segmentService = scope.ServiceProvider.GetRequiredService<ISegmentAnalysis>();

        // Explicit threshold (e.g. from the Statistics re-analyze button) wins; else the user's default.
        int matchThreshold = await segmentService.GetEffectiveMatchThresholdAsync(userId, p.MatchThresholdPercent);

        if (!_analysisProgress.TryStart(userId, 1))
            _analysisProgress.ForceStart(userId, 1);

        try
        {
            ct.ThrowIfCancellationRequested();
            _analysisProgress.UpdateProgress(userId, 1, $"Segment {segmentId}", "Analyzing...");
            _logger.LogInformation("OperationQueue: Single-segment analysis for segment {Id}", segmentId);

            await segmentService.AnalyzeSegmentMatchesAsync(segmentId, userId, matchThreshold, ct);

            _analysisProgress.Complete(userId);
            _logger.LogInformation("OperationQueue: Single-segment analysis done for segment {Id}", segmentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "OperationQueue: Single-segment analysis failed for segment {Id}", segmentId);
            _analysisProgress.Fail(userId, ex.Message);
        }
    }
}
