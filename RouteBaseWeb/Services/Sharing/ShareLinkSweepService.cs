using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// Reclaims dead share links that were never opened. Hygiene, NOT enforcement.
///
/// Expiry and revocation are authoritative in <c>ShareLinkAccess.ResolveAsync</c>, which re-reads the row on
/// every request — a link stops working the instant it expires or is revoked, whether or not this ever runs.
///
/// A dead link that WAS opened is kept forever: its <c>open_count</c> is the resource's view history, and
/// the badge sums every row (see <c>ShareLinkService.ShareOpensAsync</c>). Deleting it would silently reset
/// a ride's view count a month after it stopped being shared, which is the whole thing this sweep must not
/// do. So the table is no longer strictly bounded — a kept row is ~200 bytes, and only links someone
/// actually opened qualify.
///
/// The grace window is deliberate: a row stays listed for a while after it dies so the owner's management
/// UI can still say "expired 3 days ago" rather than silently forgetting a link they remember sharing.
/// </summary>
public sealed class ShareLinkSweepService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    /// <summary>How long a dead link stays visible in the owner's list before being reclaimed.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromDays(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ShareLinkSweepService> _logger;
    private readonly TimeProvider _clock;

    public ShareLinkSweepService(IServiceScopeFactory scopeFactory, ILogger<ShareLinkSweepService> logger, TimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock;
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
            var removed = await SweepOnceAsync(db, _clock.GetUtcNow().UtcDateTime, Grace, ct);
            if (removed > 0)
                _logger.LogInformation("ShareLinkSweep: reclaimed {Count} dead share link(s)", removed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let a sweep failure take the host down; the next tick retries.
            _logger.LogWarning(ex, "Share link sweep failed");
        }
    }

    /// <summary>
    /// Deletes NEVER-OPENED links that expired or were revoked longer than <paramref name="grace"/> ago.
    /// Returns the number of rows removed. Static + internal so it is unit-testable without a hosted
    /// service, matching <c>PhotoOrphanSweepService.SweepOnceAsync</c>.
    /// </summary>
    internal static async Task<int> SweepOnceAsync(
        RouteBaseDbContext db, DateTime now, TimeSpan grace, CancellationToken ct)
    {
        var cutoff = now - grace;

        // One DELETE statement — a full scan of Share_Link (EXPLAIN-verified: the OR's revoked_at arm has
        // no index, which defeats IX_ShareLink_ExpiresAt for the whole predicate). Fine for a daily sweep
        // at this table's size; index revoked_at or split the arms if Share_Link ever grows to matter.
        // Revoked rows are matched on their own timestamp, so revoking a long-lived link still reclaims it
        // a grace period later rather than waiting for its original expiry.
        //
        // OpenCount == 0 first: an opened link is this resource's view history and is never reclaimed —
        // see the type remarks.
        return await db.ShareLinks
            .Where(s => s.OpenCount == 0
                     && (s.ExpiresAt < cutoff || (s.RevokedAt != null && s.RevokedAt < cutoff)))
            .ExecuteDeleteAsync(ct);
    }
}
