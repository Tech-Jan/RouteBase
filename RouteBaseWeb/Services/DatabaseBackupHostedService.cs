using RouteBaseWeb.Helpers;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <summary>
/// Nightly trigger for <see cref="IDatabaseBackupService"/> (D3a). Runs at a quiet hour, just before
/// the 03:00 <see cref="DatabaseMaintenanceService"/> VACUUM, so the snapshot captures the pre-VACUUM
/// state. A separate hosted service (not folded into maintenance) keeps the backup a self-contained
/// vertical slice: adding/removing it is one registration line and touches no other timer. Failures
/// are logged (the Error+ mirror picks them up) and swallowed — a backup must never crash the host.
/// </summary>
public class DatabaseBackupHostedService : BackgroundService
{
    // One hour before DatabaseMaintenanceService (RunAtHour = 3), same off-peak window.
    private const int RunAtHour = 2;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseBackupHostedService> _logger;

    public DatabaseBackupHostedService(
        IServiceScopeFactory scopeFactory, ILogger<DatabaseBackupHostedService> logger)
    {
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
                using var scope = _scopeFactory.CreateScope();
                var backup = scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>();
                await backup.BackupAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "DatabaseBackupHostedService: nightly backup failed");
            }
        }
    }
}
