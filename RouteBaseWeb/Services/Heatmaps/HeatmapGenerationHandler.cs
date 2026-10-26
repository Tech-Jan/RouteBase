using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Queue;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// <see cref="OperationQueue.OperationType.HeatmapGeneration"/>: renders a heatmap-kind segment's
/// tile pyramid. Body moved verbatim from OperationQueueWorker.RunHeatmapGenerationAsync
/// (CR5-01/QW-01).
/// </summary>
public class HeatmapGenerationHandler : IOperationHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HeatmapProgress _heatmapProgress;
    private readonly ILogger<HeatmapGenerationHandler> _logger;

    public HeatmapGenerationHandler(
        IServiceScopeFactory scopeFactory,
        HeatmapProgress heatmapProgress,
        ILogger<HeatmapGenerationHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _heatmapProgress = heatmapProgress;
        _logger = logger;
    }

    public OperationQueue.OperationType Type => OperationQueue.OperationType.HeatmapGeneration;

    public void FailProgress(string userId, string message) => _heatmapProgress.Fail(userId, message);

    public string QueueDiscriminator => QueueOperationTypes.Heatmap;
    public void SetQueueInfo(string userId, int position, int total) => _heatmapProgress.SetQueueInfo(userId, position, total);
    public QueueProgressSnapshot GetQueueProgress(string userId, int pendingCountForUser) =>
        _heatmapProgress.BuildQueueProgress(userId, pendingCountForUser);
    public void Acknowledge(string userId) => _heatmapProgress.Acknowledge(userId);

    /// <summary>
    /// HM-10: Attach flipped the row to `pending` before enqueueing; discarding the item without
    /// demoting it would wedge the heatmap in `pending` forever (every future Attach hits the
    /// pending/running 409 guard). FailPendingAsync demotes only pending.
    /// </summary>
    public async Task OnDiscardedWhileQueuedAsync(OperationPayload payload)
    {
        if (payload is not HeatmapGenerationPayload p) return;
        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IHeatmapLifecycle>()
            .FailPendingAsync(new SegmentId(p.SegmentId), "Cancelled by user");
    }

    public async Task RunAsync(OperationPayload payload, CancellationToken ct)
    {
        var p = (HeatmapGenerationPayload)payload;
        string userId = p.UserId;
        var segmentId = new SegmentId(p.SegmentId);

        using var scope = OperationScope.Create(_scopeFactory, userId);
        var service = scope.ServiceProvider.GetRequiredService<IHeatmapGenerationService>();

        if (!_heatmapProgress.TryStart(userId, segmentId))
        {
            // A prior run left IsRunning=true (crash / never-acknowledged). Surface it before
            // steamrolling: the queue is single-worker, so no live run can actually be in flight.
            _logger.LogWarning(
                "OperationQueue: heatmap progress for user {UserId} was still marked running ({Status}) — force-resetting for segment {Id}",
                userId, _heatmapProgress.GetStatus(userId), segmentId);
            _heatmapProgress.ForceStart(userId, segmentId);
        }

        try
        {
            _logger.LogInformation("OperationQueue: Heatmap generation for segment {Id}", segmentId);
            // The service owns the Heatmap row's DB status and rethrows on cancel/error.
            await service.GenerateAsync(segmentId, userId, ct);
            _heatmapProgress.Complete(userId);
            _logger.LogInformation("OperationQueue: Heatmap generation done for segment {Id}", segmentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "OperationQueue: Heatmap generation failed for segment {Id}", segmentId);
            _heatmapProgress.Fail(userId, ex.Message);
        }
    }
}
