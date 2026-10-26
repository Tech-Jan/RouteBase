using RouteBaseWeb.Models.Queue;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// <see cref="OperationQueue.OperationType.PhotoImport"/>: photo batch import (manual upload)
/// from a staged batch directory. Body moved verbatim from
/// OperationQueueWorker.RunPhotoImportAsync (CR5-01/QW-01).
/// </summary>
public class PhotoImportHandler : IOperationHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PhotoImportProgress _photoProgress;
    private readonly ILogger<PhotoImportHandler> _logger;

    public PhotoImportHandler(
        IServiceScopeFactory scopeFactory,
        PhotoImportProgress photoProgress,
        ILogger<PhotoImportHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _photoProgress = photoProgress;
        _logger = logger;
    }

    public OperationQueue.OperationType Type => OperationQueue.OperationType.PhotoImport;

    public void FailProgress(string userId, string message) => _photoProgress.Fail(userId, message);

    // No SetQueueInfo/GetQueueProgress/Acknowledge override: the Photos page polls its own
    // import-status endpoint, outside the op-toast queue rotation (PhotoImportProgress doc comment).
    public bool HoldsImportSlot => true;

    /// <summary>Cancelled while still queued: the staged batch would never be consumed — delete it.</summary>
    public Task OnDiscardedWhileQueuedAsync(OperationPayload payload)
    {
        if (payload is PhotoImportPayload p)
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<PhotoImportStaging>().Delete(p.UserId, p.BatchId);
        }
        return Task.CompletedTask;
    }

    public async Task RunAsync(OperationPayload payload, CancellationToken ct)
    {
        var p = (PhotoImportPayload)payload;
        string userId = p.UserId, batchId = p.BatchId;

        using var scope = OperationScope.Create(_scopeFactory, userId);

        var staging      = scope.ServiceProvider.GetRequiredService<PhotoImportStaging>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<PhotoImportOrchestrator>();

        var batch = await staging.LoadAsync(userId, batchId);
        int total = batch?.Files.Count ?? 0;

        _photoProgress.Start(userId, batchId, total);

        try
        {
            _logger.LogInformation("OperationQueue: photo import starting (batch {BatchId}, {Total} files)", batchId, total);

            if (total > 0)
            {
                await orchestrator.ImportBatchAsync(
                    userId, batchId,
                    onFileStarted:   name => _photoProgress.RecordFileStarted(userId, name),
                    onFileCompleted: result => _photoProgress.RecordFileCompleted(userId, result),
                    ct);
            }

            _photoProgress.Complete(userId);
            var s = _photoProgress.GetStatus(userId);
            _logger.LogInformation(
                "OperationQueue: photo import done - {I} imported, {D} duplicate, {A} ambiguous, {U} unmatched, {F} failed",
                s.Imported, s.Duplicate, s.Ambiguous, s.Unmatched, s.Failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "OperationQueue: photo import failed for batch {BatchId}", batchId);
            _photoProgress.Fail(userId, ex.Message);
        }
        // NOTE: no staging.Delete here — ambiguous/unmatched files must survive for the user to assign.
        // The orchestrator deletes terminal (imported/failed) staged files; the TTL sweep clears the rest.
    }
}
