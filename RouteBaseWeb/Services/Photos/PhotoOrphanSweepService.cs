using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// Reconciles stored rendition files against the Photos table (AS-02). Every delete path is row-commit →
/// best-effort file purge, so a crash between the two, or a raw FK cascade, can strand rendition files
/// forever. This sweep is the promised backstop: on startup (after a short delay) and every 24 h it removes
/// files whose photo row is gone. A 1-hour mtime grace + a second row re-check protects an in-flight import
/// whose file lands before its row is queryable in this scope.
/// </summary>
public sealed class PhotoOrphanSweepService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);
    // A file younger than this may belong to an import whose row this scope can't yet see — leave it.
    private static readonly TimeSpan MinOrphanAge = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PhotoOrphanSweepService> _logger;

    private readonly TimeProvider _clock;

    public PhotoOrphanSweepService(IServiceScopeFactory scopeFactory, ILogger<PhotoOrphanSweepService> logger, TimeProvider clock)
    {
        _clock = clock;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        await SweepAsync(stoppingToken);
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SweepAsync(stoppingToken);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IPhotoImageStore>();
            var purged = await SweepOnceAsync(db, store, MinOrphanAge, _logger, _clock.GetUtcNow().UtcDateTime, ct);
            if (purged > 0)
                _logger.LogInformation("PhotoOrphanSweep: purged {Count} orphan rendition(s)", purged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Photo orphan sweep failed");
        }
    }

    /// <summary>
    /// Delete every stored rendition whose photo row is absent and whose files are all older than
    /// <paramref name="minAge"/> (re-checking the row once more just before deletion). Returns the count
    /// of photo ids purged. Internal + static so it is unit-testable without a hosted service — same shape
    /// as <c>RecordingSessionCleanupService.PurgeAbandonedOlderThanAsync</c>.
    /// </summary>
    internal static async Task<int> SweepOnceAsync(
        RouteBaseDbContext db, IPhotoImageStore store, TimeSpan minAge,
        ILogger? logger, DateTime now, CancellationToken ct)
    {
        var live = (await db.Photos.AsNoTracking().Select(p => p.PhotoId).ToListAsync(ct)).ToHashSet();
        var tiers = new[] { PhotoTier.Thumb, PhotoTier.Preview };
        int purged = 0;

        foreach (var id in store.EnumerateStoredIds().Select(v => new PhotoId(v)))
        {
            if (ct.IsCancellationRequested) break;
            if (live.Contains(id)) continue;

            var paths = tiers.Select(t => store.GetPhysicalPath(id, t)).Where(File.Exists).ToList();
            if (paths.Count == 0) continue; // already gone
            // Grace window: a fresh file may be an import whose row this snapshot missed — skip it.
            if (!paths.All(p => now - File.GetLastWriteTimeUtc(p) > minAge)) continue;
            // Final row re-check closes the snapshot→now gap (a row inserted after the snapshot).
            if (await db.Photos.AsNoTracking().AnyAsync(p => p.PhotoId == id, ct)) continue;

            try
            {
                await store.DeleteAsync(id, CancellationToken.None);
                purged++;
            }
            catch (Exception ex)
            {
                // Best-effort: a per-id IO error is logged and retried on the next run.
                logger?.LogWarning(ex, "PhotoOrphanSweep: failed to delete orphan renditions for photo {Id}", id);
            }
        }
        return purged;
    }
}
