using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.ErrorLog;
using RouteBaseWeb.Services.Sql;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <summary>
/// Runs SQLite housekeeping once a day at a quiet hour:
/// - Error_Log prune (OBS-02/C3): age + row-cap retention, before optimize so VACUUM reclaims the freed pages.
/// - PRAGMA optimize: refreshes query-planner statistics (ANALYZE where needed).
/// - VACUUM: rebuilds the file, reclaiming free pages left by deletes.
///
/// VACUUM cannot run inside a transaction, but runs fine in WAL mode — VACUUM in WAL trades a
/// temporarily large WAL file for avoiding an exclusive journal-mode switch. Delegates to the
/// shared <see cref="SqliteHousekeeping.VacuumAsync"/>, also used by DatabaseResetService.
/// </summary>
public class DatabaseMaintenanceService : BackgroundService
{
    // Local hour to run at (off-peak). VACUUM takes an exclusive lock for its duration.
    private const int RunAtHour = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseMaintenanceService> _logger;

    private readonly TimeProvider _clock;

    public DatabaseMaintenanceService(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseMaintenanceService> logger,
        TimeProvider clock)
    {
        _clock = clock;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(DailyRunSchedule.DelayUntilNextLocalHour(RunAtHour), stoppingToken);

            try
            {
                await RunMaintenanceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "DatabaseMaintenanceService encountered an error");
            }
        }
    }


    // Internal (not private) so DatabaseMaintenanceServiceTests can drive it directly without
    // waiting for the daily timer (InternalsVisibleTo RouteBaseWeb.Tests).
    internal async Task RunMaintenanceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
        var errorLogStore = scope.ServiceProvider.GetRequiredService<ErrorLogStore>();

        var startedAt = _clock.GetUtcNow().UtcDateTime;
        _logger.LogInformation("DatabaseMaintenance: starting daily VACUUM + optimize");

        // Prune Error_Log before optimize/VACUUM so the same run reclaims the freed pages (C3).
        await errorLogStore.PruneAsync(ErrorLogRetention.MaxAge, ErrorLogRetention.MaxRows, ct);

        // Refresh planner stats first (cheap, safe in any journal mode).
        await context.Database.ExecuteSqlRawAsync("PRAGMA optimize;", ct);

        await SqliteHousekeeping.VacuumAsync(context.Database, _logger, ct);

        var elapsed = _clock.GetUtcNow().UtcDateTime - startedAt;
        _logger.LogInformation("DatabaseMaintenance: completed in {ElapsedMillis} ms", (long)elapsed.TotalMilliseconds);
    }
}
