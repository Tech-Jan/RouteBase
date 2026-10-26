namespace RouteBaseWeb.Services;

/// <summary>
/// Periodic TTL sweep of the import-staging temp roots (PI-07). Both track and photo staging accumulate
/// abandoned batch dirs (a begin with no commit/abort); the old startup-only one-shots never reclaimed them
/// on a server up for weeks. Runs immediately (replacing the startup sweeps), then every 6 hours, removing
/// batch dirs older than 24 h. A single service covers both roots — track staging rides along so
/// Backend-ImportExport needn't build a second sweeper.
/// </summary>
public sealed class StagingSweepService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly TrackImportStaging _tracks;
    private readonly Photos.PhotoImportStaging _photos;
    private readonly ILogger<StagingSweepService> _logger;

    public StagingSweepService(
        TrackImportStaging tracks,
        Photos.PhotoImportStaging photos,
        ILogger<StagingSweepService> logger)
    {
        _tracks = tracks;
        _photos = photos;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Sweep();   // immediate first pass replaces the startup one-shots
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            Sweep();
    }

    private void Sweep()
    {
        // Never let a sweep throw kill the host — CleanupOrphans already swallows per-dir IO errors, this
        // is the belt-and-braces around anything else (e.g. root enumeration).
        try
        {
            _tracks.CleanupOrphans(MaxAge);
            _photos.CleanupOrphans(MaxAge);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Staging sweep failed");
        }
    }
}
