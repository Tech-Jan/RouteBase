using System.Collections.Concurrent;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Queue;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Imports;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Queue;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Services;

/// <summary>
/// <see cref="OperationQueue.OperationType.ActivityImport"/>: provider-agnostic activity import
/// (Strava today). Body moved verbatim from OperationQueueWorker.RunImportAsync (CR5-01/QW-01);
/// the serial fallback stays — it is the generic path for future non-parallel providers (INC-02).
/// </summary>
public class ActivityImportHandler : IOperationHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ActivityImportProgress _importProgress;
    private readonly ILogger<ActivityImportHandler> _logger;

    public ActivityImportHandler(
        IServiceScopeFactory scopeFactory,
        ActivityImportProgress importProgress,
        ILogger<ActivityImportHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _importProgress = importProgress;
        _logger = logger;
    }

    public OperationQueue.OperationType Type => OperationQueue.OperationType.ActivityImport;

    public void FailProgress(string userId, string message) => _importProgress.Fail(userId, message);

    public bool HoldsImportSlot => true;
    public string QueueDiscriminator => QueueOperationTypes.Import;
    public void SetQueueInfo(string userId, int position, int total) => _importProgress.SetQueueInfo(userId, position, total);
    public QueueProgressSnapshot GetQueueProgress(string userId, int pendingCountForUser) =>
        _importProgress.BuildQueueProgress(userId, pendingCountForUser);
    public void Acknowledge(string userId) => _importProgress.Acknowledge(userId);

    public async Task RunAsync(OperationPayload payload, CancellationToken ct)
    {
        var p = (ActivityImportPayload)payload;
        string userId = p.UserId, providerId = p.ProviderId;
        DateTime? after = p.After;

        using var scope = OperationScope.Create(_scopeFactory, userId);

        var providers     = scope.ServiceProvider.GetServices<IActivitySyncService>();
        var provider      = providers.FirstOrDefault(sync => sync.ProviderId == providerId);
        var importService = scope.ServiceProvider.GetRequiredService<IActivityImportService>();

        if (provider == null)
        {
            _logger.LogError("OperationQueue: No IActivitySyncService registered for provider '{Provider}'", providerId);
            _importProgress.Fail(userId, $"Unknown provider: {providerId}");
            return;
        }

        if (!_importProgress.TryStart(userId, 0, providerId))
        {
            _logger.LogWarning("Import already marked running, forcing start");
            _importProgress.ForceStart(userId, 0, providerId);
        }

        // Fresh trackIds surfaced by the import paths (serial adds inline; parallel via callback).
        // ConcurrentQueue not List: the parallel callback's thread-safety no longer rests on the
        // "single persister ⇒ serial" invariant holding — a race here would be silent corruption.
        //
        // Declared OUTSIDE the try so the finally can still match whatever landed — see the finally below.
        var importedTrackIds = new ConcurrentQueue<int>();

        try
        {
            _importProgress.UpdateProgress(userId, 0, "", $"Fetching activities from {provider.DisplayName}...", 0, 0, 0);
            _logger.LogInformation("OperationQueue: {Provider} import starting (after: {After})", provider.DisplayName, after);

            List<ExternalActivitySummary> activities;
            try
            {
                activities = await provider.FetchAllActivitiesAsync(after);
            }
            catch (ProviderRateLimitException ex)
            {
                _logger.LogWarning("OperationQueue: {Provider} import aborted - rate limit hit", provider.DisplayName);
                _importProgress.Fail(userId, ex.Message);
                return;
            }

            if (activities.Count == 0)
            {
                _importProgress.Complete(userId, 0, 0, 0);
                _logger.LogInformation("OperationQueue: {Provider} import - no activities found", provider.DisplayName);
                return;
            }

            _importProgress.SetTotal(userId, activities.Count);
            _logger.LogInformation("OperationQueue: {Provider} import - {Count} activities", provider.DisplayName, activities.Count);

            int imported = 0, skipped = 0, failed = 0;

            if (provider is IParallelActivityImporter parallel)
            {
                try
                {
                    (imported, skipped, failed) = await parallel.ImportActivitiesParallelAsync(
                        activities, userId,
                        (current, name, imp, skp, fld) =>
                            _importProgress.UpdateProgress(userId, current, name, "Importing...", imp, skp, fld),
                        onActivityImported: importedTrackIds.Enqueue,
                        ct);
                }
                catch (ProviderRateLimitException ex)
                {
                    _logger.LogWarning("OperationQueue: {Provider} import aborted mid-import - rate limit hit", provider.DisplayName);
                    _importProgress.Fail(userId, ex.Message);
                    return;
                }
            }
            else
            {
                for (int i = 0; i < activities.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var activity = activities[i];
                    var name = activity.Name ?? $"Activity {activity.ExternalId}";
                    _importProgress.UpdateProgress(userId, i + 1, name, "Importing...", imported, skipped, failed);

                    try
                    {
                        var existing = await importService.FindExistingAsync(
                            activity.ExternalId, providerId, userId);
                        if (existing is not null)
                        {
                            skipped++;
                            _importProgress.UpdateProgress(
                                userId, i + 1, name, ImportSkipReason.Message(existing),
                                imported, skipped, failed);
                            continue;
                        }

                        var result = await provider.ImportActivityAsync(activity, userId);
                        if (result != null)
                        {
                            imported++;
                            // Activity_Bbox is maintained inside ActivityImportService.ImportFromSummaryAsync.
                            // The belt-and-suspenders call here guards against providers that bypass that path.
                            try { await importService.UpsertActivityBboxAsync(result.TrackId); }
                            catch (Exception bboxEx)
                            {
                                _logger.LogWarning(bboxEx,
                                    "Bbox upsert for track {Id} failed; pipeline will backfill on next analysis",
                                    result.TrackId);
                            }
                            // Seam-2: bbox upserted above (and inside ImportFromSummaryAsync) before the match.
                            importedTrackIds.Enqueue(result.TrackId.Value);
                        }
                        else failed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error importing {Provider} activity {Id}", providerId, activity.ExternalId);
                        failed++;
                    }

                    _importProgress.UpdateProgress(userId, i + 1, name, "Importing...", imported, skipped, failed);
                }
            }

            _importProgress.Complete(userId, imported, skipped, failed);
            _logger.LogInformation("OperationQueue: {Provider} import done - {I} imported, {S} skipped, {F} failed",
                provider.DisplayName, imported, skipped, failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "OperationQueue: {Provider} import failed", providerId);
            _importProgress.Fail(userId, ex.Message);
        }
        finally
        {
            // Incremental per-import match: every freshly imported track against its candidate segments,
            // once the batch has drained (single-writer safe). inval-01/06.
            //
            // In a finally, NOT the try body: every early exit above skipped it — an OCE from
            // ct.ThrowIfCancellationRequested, the rate-limit `return`, or any caught exception — which
            // left committed tracks with zero efforts. A re-import then pre-skips those tracks as already
            // present, so they never re-matched; it self-healed only at the next analysis that happened to
            // touch them.
            //
            // CancellationToken.None deliberately: a cancelled token is the exact case this exists to
            // cover, so honouring it here would restore the bug. The work is bounded by what actually
            // landed — MatchImportedTracksAsync returns immediately on an empty set, so a cancel that
            // imported nothing costs nothing.
            try
            {
                await ImportMatchStep.MatchImportedTracksAsync(scope, userId, importedTrackIds, CancellationToken.None);
            }
            catch (Exception matchEx)
            {
                // Never let the salvage replace the in-flight exception (a throw from a finally wins).
                _logger.LogError(matchEx,
                    "OperationQueue: post-import segment match failed for {Provider}; {Count} track(s) left unmatched",
                    providerId, importedTrackIds.Count);
            }
        }
        // Reservation release is centralized in the worker's finally (QW-04) so pre-try exits
        // (unknown provider, scope/DI throws) can't leak it.
    }
}
