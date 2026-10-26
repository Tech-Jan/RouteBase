using RouteBaseWeb.Models.Queue;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// <see cref="OperationQueue.OperationType.TrackEditDerive"/> queue glue (SingleSegmentAnalysisHandler
/// template): impersonate the enqueuer, resolve <see cref="TrackEditDeriveService"/>, run it under the
/// segment-analysis progress lifecycle (the toast renders as an analysis op). Re-runnable — a discarded
/// derive needs no queued cleanup (§5.2); a later save/revert re-derives.
/// </summary>
public class TrackEditDeriveHandler : IOperationHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SegmentAnalysisProgress _progress;
    private readonly ILogger<TrackEditDeriveHandler> _logger;

    public TrackEditDeriveHandler(
        IServiceScopeFactory scopeFactory, SegmentAnalysisProgress progress, ILogger<TrackEditDeriveHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _progress = progress;
        _logger = logger;
    }

    public OperationQueue.OperationType Type => OperationQueue.OperationType.TrackEditDerive;

    public void FailProgress(string userId, string message) => _progress.Fail(userId, message);

    public void SetQueueInfo(string userId, int position, int total) => _progress.SetQueueInfo(userId, position, total);
    public QueueProgressSnapshot GetQueueProgress(string userId, int pendingCountForUser) =>
        _progress.BuildQueueProgress(userId, pendingCountForUser);
    public void Acknowledge(string userId) => _progress.Acknowledge(userId);

    public async Task RunAsync(OperationPayload payload, CancellationToken ct)
    {
        var p = (TrackEditDerivePayload)payload;
        using var scope = OperationScope.Create(_scopeFactory, p.UserId);
        var service = scope.ServiceProvider.GetRequiredService<TrackEditDeriveService>();

        if (!_progress.TryStart(p.UserId, 1)) _progress.ForceStart(p.UserId, 1);
        try
        {
            ct.ThrowIfCancellationRequested();
            var (title, message) = Label(p.Reason);
            _progress.UpdateProgress(p.UserId, 1, title, message);
            await service.RunAsync(p, ct);
            _progress.Complete(p.UserId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "OperationQueue: track-edit derive failed for commit {CommitId}", p.CommitId);
            _progress.Fail(p.UserId, ex.Message);
        }
    }

    // The toast is the only thing that varies by reason — a recording that just finished uploading must not
    // tell the user their tracks are being "edited".
    private static (string Title, string Message) Label(DeriveReason reason) => reason switch
    {
        DeriveReason.Recording        => ("Recording", "Matching segments…"),
        DeriveReason.ElevationRefresh => ("Elevation", "Recomputing segment efforts…"),
        _                             => ("Track edit", "Recomputing edited tracks…"),
    };
}
