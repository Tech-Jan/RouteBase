using RouteBaseWeb.Models;
using System.Collections.Concurrent;
using RouteBaseWeb.Models.Queue;
using RouteBaseWeb.Services.Imports;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services;

/// <summary>
/// <see cref="OperationQueue.OperationType.TrackImport"/>: activity-file batch import (manual
/// upload: GPX / TCX / FIT) from a staged batch directory. Body moved verbatim from
/// OperationQueueWorker.RunTrackImportAsync (CR5-01/QW-01).
/// </summary>
public class TrackImportHandler : IOperationHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ActivityImportProgress _importProgress;
    private readonly ILogger<TrackImportHandler> _logger;

    public TrackImportHandler(
        IServiceScopeFactory scopeFactory,
        ActivityImportProgress importProgress,
        ILogger<TrackImportHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _importProgress = importProgress;
        _logger = logger;
    }

    public OperationQueue.OperationType Type => OperationQueue.OperationType.TrackImport;

    public void FailProgress(string userId, string message) => _importProgress.Fail(userId, message);

    public bool HoldsImportSlot => true;
    public string QueueDiscriminator => QueueOperationTypes.TrackImport;
    public void SetQueueInfo(string userId, int position, int total) => _importProgress.SetQueueInfo(userId, position, total);
    public QueueProgressSnapshot GetQueueProgress(string userId, int pendingCountForUser) =>
        _importProgress.BuildQueueProgress(userId, pendingCountForUser);
    public void Acknowledge(string userId) => _importProgress.Acknowledge(userId);

    /// <summary>Cancelled while still queued: the staged batch would never be consumed — delete it.</summary>
    public Task OnDiscardedWhileQueuedAsync(OperationPayload payload)
    {
        if (payload is TrackImportPayload p)
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<TrackImportStaging>().Delete(p.UserId, p.BatchId);
        }
        return Task.CompletedTask;
    }

    public async Task RunAsync(OperationPayload payload, CancellationToken ct)
    {
        var p = (TrackImportPayload)payload;
        string userId = p.UserId, batchId = p.BatchId;

        using var scope = OperationScope.Create(_scopeFactory, userId);

        var staging      = scope.ServiceProvider.GetRequiredService<TrackImportStaging>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<TrackFileImportOrchestrator>();

        var batch = await staging.LoadAsync(userId, batchId);
        int total = batch?.Files.Count ?? 0;

        if (!_importProgress.TryStart(userId, total, "gpx"))
        {
            _logger.LogWarning("Track import: progress already marked running, forcing start");
            _importProgress.ForceStart(userId, total, "gpx");
        }

        // Declared OUTSIDE the try so the finally can still match whatever landed — see the finally below.
        var importedTrackIds = new ConcurrentQueue<int>();

        try
        {
            _logger.LogInformation("OperationQueue: track import starting (batch {BatchId}, {Total} files)", batchId, total);

            if (total == 0)
            {
                _importProgress.Complete(userId, 0, 0, 0);
                return;
            }

            _importProgress.UpdateProgress(userId, 0, "", "Parsing files...", 0, 0, 0);

            var (imported, skipped, failed) = await orchestrator.ImportBatchParallelAsync(
                userId, batchId,
                onFileStarted:   name => _importProgress.RecordFileStarted(userId, name),
                onFileCompleted: (name, outcome, error) =>
                    _importProgress.RecordFileCompleted(userId, name, outcome, error),
                onActivityImported: importedTrackIds.Enqueue,
                ct);

            _importProgress.Complete(userId, imported, skipped, failed);
            _logger.LogInformation(
                "OperationQueue: track import done - {I} imported, {S} skipped, {F} failed",
                imported, skipped, failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "OperationQueue: track import failed for batch {BatchId}", batchId);
            _importProgress.Fail(userId, ex.Message);
        }
        finally
        {
            // Incremental per-import match for the freshly imported tracks (inval-04/06).
            //
            // In the finally, NOT the try body: an OCE from a cancelled batch, or any caught failure, used
            // to skip the match and leave committed tracks with zero efforts — and a re-import pre-skips
            // them as already present, so they never re-matched. Same fix as ActivityImportHandler.
            //
            // CancellationToken.None deliberately: a cancelled token is the exact case this covers.
            // Bounded by what landed (the match no-ops on an empty set).
            try
            {
                await ImportMatchStep.MatchImportedTracksAsync(scope, userId, importedTrackIds, CancellationToken.None);
            }
            catch (Exception matchEx)
            {
                // Never let the salvage replace the in-flight exception (a throw from a finally wins).
                _logger.LogError(matchEx,
                    "OperationQueue: post-import segment match failed for batch {BatchId}; {Count} track(s) left unmatched",
                    batchId, importedTrackIds.Count);
            }

            staging.Delete(userId, batchId); // reservation release is centralized in the worker (QW-04)
        }
    }
}
