namespace RouteBaseWeb.Services;

/// <summary>
/// Deletes finished export ZIPs once they are past their retention window.
///
/// Without this the store only ever grows: an export of a large library is tens of megabytes, and the
/// button invites re-running it whenever the table filter changes. A ZIP is a derived artifact the user
/// can always rebuild, so an expired one costs them a click, not data — which is why the window is short
/// (<see cref="ActivityExportStorageOptions.Retention"/>) rather than generous.
///
/// No DB leg, unlike the photo orphan sweep: the file IS the record. Age comes from the file's own mtime.
/// </summary>
public sealed class ActivityExportSweepService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    private readonly IActivityExportStore _store;
    private readonly ActivityExportStorageOptions _options;
    private readonly ILogger<ActivityExportSweepService> _logger;
    private readonly TimeProvider _clock;

    public ActivityExportSweepService(
        IActivityExportStore store,
        ActivityExportStorageOptions options,
        ILogger<ActivityExportSweepService> logger,
        TimeProvider clock)
    {
        _store = store;
        _options = options;
        _logger = logger;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        Sweep();
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            Sweep();
    }

    private void Sweep()
    {
        try
        {
            var deleted = _store.SweepOlderThan(_options.Retention, _clock.GetUtcNow());
            if (deleted > 0)
                _logger.LogInformation("ActivityExportSweep: deleted {Count} expired export(s)", deleted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Activity export sweep failed");
        }
    }
}
