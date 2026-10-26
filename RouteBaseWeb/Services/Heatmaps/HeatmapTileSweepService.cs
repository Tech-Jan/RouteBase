using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Once a day (off-peak) deletes orphaned heatmap tile files — a committed <c>.htiles</c> whose
/// <see cref="Heatmap"/> row is gone. The row-then-file delete at the segment-delete / reset seams is
/// best-effort and non-transactional (a crash between the DB commit and <c>File.Delete</c>, or an IO
/// error, leaves the file behind), and nothing else reclaims it — so disk would slowly creep. Ids never
/// repeat (AUTOINCREMENT), so a file with no row can never regain one: deleting it is always safe.
/// The reconcile core is static + pure for unit tests; the service only wires the store, DB and clock.
/// </summary>
public sealed class HeatmapTileSweepService : BackgroundService
{
    private const int RunAtHour = 4; // after DatabaseMaintenanceService's 3am VACUUM

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HeatmapTilePackStore _tileStore;
    private readonly ILogger<HeatmapTileSweepService> _logger;

    public HeatmapTileSweepService(
        IServiceScopeFactory scopeFactory,
        HeatmapTilePackStore tileStore,
        ILogger<HeatmapTileSweepService> logger)
    {
        _scopeFactory = scopeFactory;
        _tileStore = tileStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // HM-11: the operation queue is in-memory, so a restart mid-render strands Heatmap rows in
        // `pending`/`running` forever — and Attach's pending/running guard then 409s every retry.
        // Hosted services start before Kestrel accepts requests and the queue is empty at boot, so any
        // such row here is provably interrupted work: demote it so the user can re-attach.
        try
        {
            await ReconcileInterruptedAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Heatmap interrupted-row reconcile failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(DailyRunSchedule.DelayUntilNextLocalHour(RunAtHour), stoppingToken);

            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "HeatmapTileSweepService encountered an error");
            }
        }
    }


    /// <summary>Flip interrupted `pending`/`running` rows to `failed` (HM-11). Internal for tests;
    /// only ever called at startup, before any request can create a legitimate pending row.</summary>
    internal async Task<int> ReconcileInterruptedAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
        int n = await context.Heatmaps
            .Where(h => h.Status == HeatmapStatuses.Pending || h.Status == HeatmapStatuses.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.Status, HeatmapStatuses.Failed)
                .SetProperty(h => h.Error, "Interrupted by app restart — attach again to regenerate."), ct);
        if (n > 0)
            _logger.LogWarning("HeatmapTileSweep: reconciled {Count} interrupted heatmap row(s) to failed", n);
        return n;
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();

        // Snapshot disk BEFORE reading live rows: a heatmap created between the two snapshots then has its
        // file appear after the disk scan (not considered) with its row already in liveIds — never a false
        // orphan. The reverse order could delete a freshly-committed file whose row post-dates the query.
        var storedIds = _tileStore.EnumerateStoredHeatmapIds().ToList();
        var liveIds = (await context.Heatmaps.Select(h => h.HeatmapId).ToListAsync(ct)).ToHashSet();
        var deleted = SweepOrphans(storedIds, liveIds, _tileStore.Delete);

        // HM-13: also reclaim crash-abandoned .tmp packs (dispose cleanup never ran); the 1-day age
        // guard can never hit an in-flight writer (a generation runs minutes, not days).
        var tmpDeleted = _tileStore.DeleteAbandonedTempFiles(TimeSpan.FromDays(1));

        if (deleted > 0 || tmpDeleted > 0)
            _logger.LogInformation(
                "HeatmapTileSweep: deleted {Count} orphaned tile file(s), {TmpCount} abandoned temp file(s)",
                deleted, tmpDeleted);
    }

    /// <summary>Delete every stored id with no live row. Pure reconcile core — inject the id sources and the
    /// delete action so it's unit-testable without a filesystem or DB. Returns the count deleted.</summary>
    internal static int SweepOrphans(IEnumerable<int> storedIds, ISet<int> liveIds, Action<int> delete)
    {
        int count = 0;
        foreach (var id in storedIds)
        {
            if (liveIds.Contains(id)) continue;
            delete(id);
            count++;
        }
        return count;
    }
}
