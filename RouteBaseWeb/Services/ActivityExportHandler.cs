using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Queue;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services;

/// <summary>
/// <see cref="OperationQueue.OperationType.ActivityExport"/>: builds the "Export GPX (N)" ZIP in the
/// background instead of on the request thread. A few hundred rides take minutes, and holding an HTTP
/// response open for that means the user cannot leave the page and a proxy timeout silently loses the
/// work. On the queue, they can navigate away and collect the file from the op-toast when it lands.
/// </summary>
public class ActivityExportHandler : IOperationHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ActivityExportProgress _progress;
    private readonly IActivityExportStore _store;
    private readonly ILogger<ActivityExportHandler> _logger;

    public ActivityExportHandler(
        IServiceScopeFactory scopeFactory,
        ActivityExportProgress progress,
        IActivityExportStore store,
        ILogger<ActivityExportHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _progress = progress;
        _store = store;
        _logger = logger;
    }

    public OperationQueue.OperationType Type => OperationQueue.OperationType.ActivityExport;

    public void FailProgress(string userId, string message) => _progress.Fail(userId, message);
    public void SetQueueInfo(string userId, int position, int total) => _progress.SetQueueInfo(userId, position, total);
    public QueueProgressSnapshot GetQueueProgress(string userId, int pendingCountForUser) =>
        _progress.BuildQueueProgress(userId, pendingCountForUser);
    public void Acknowledge(string userId) => _progress.Acknowledge(userId);
    public string QueueDiscriminator => QueueOperationTypes.Export;

    public async Task RunAsync(OperationPayload payload, CancellationToken ct)
    {
        var args = (ActivityExportPayload)payload;
        var userId = args.UserId;

        using var scope = OperationScope.Create(_scopeFactory, userId);
        var query = scope.ServiceProvider.GetRequiredService<IActivityQueryService>();
        var exporter = scope.ServiceProvider.GetRequiredService<IActivityExportService>();

        if (!_progress.TryStart(userId, args.JobId))
        {
            _logger.LogWarning("Export already marked running for {UserId}, forcing start", userId);
            _progress.ForceStart(userId, args.JobId);
        }

        // Built into a temp file, then MOVED into the store: a crash or cancel mid-build leaves a temp file
        // the OS reclaims, never a truncated ZIP sitting at a download URL the toast is already offering.
        var tempPath = Path.GetTempFileName();
        try
        {
            _progress.UpdateProgress(userId, "Collecting activities...", 0, args.TrackIds.Count);

            // Owned ∩ requested, resolved here rather than trusted from the payload: the id set came from
            // the browser's table scope, which legitimately contains rides the user does not own.
            var owned = await query.GetFilteredActivityIdsAsync(
                activityType: null, startDate: null, endDate: null,
                ownerUserIds: [userId],
                restrictIds: args.TrackIds.Distinct().Select(id => new TrackId(id)).ToList());

            BulkExportManifest manifest;
            await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
            {
                manifest = await exporter.WriteAllAsZipAsync(
                    temp, userId, owned.Select(t => t.Value).ToList(), ct,
                    onProgress: (done, total) => _progress.UpdateProgress(userId, "Building ZIP...", done, total));
            }

            ct.ThrowIfCancellationRequested();
            var size = new FileInfo(tempPath).Length;
            _store.Commit(userId, args.JobId, tempPath);
            _progress.CompleteWithFile(userId, size, manifest.Skipped.Count);

            _logger.LogInformation(
                "Activity export job {JobId}: {Count} GPX entries, {Bytes} bytes, for user {UserId}",
                args.JobId, manifest.IncludedIds.Count, size, userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Activity export job {JobId} failed", args.JobId);
            _progress.Fail(userId, ex.Message);
        }
        finally
        {
            // No-op once Commit moved it; the cancel/failure paths are why this is here.
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
