using RouteBaseWeb.Services;

namespace RouteBaseWeb.Startup;

/// <summary>
/// Hosted background workers, extracted verbatim from Program.cs (2026-07-31). The nine
/// AddHostedService registrations are kept in EXACT original order — hosted services start in
/// registration order, so this order is behaviour, not style. No reflection or assembly scanning.
/// </summary>
public static class BackgroundWorkerServiceRegistration
{
    public static IServiceCollection AddBackgroundWorkerServices(this IServiceCollection services)
    {
        services.AddHostedService<OperationQueueWorker>();
        services.AddHostedService<RecordingSessionCleanupService>();
        services.AddHostedService<DatabaseMaintenanceService>();
        services.AddHostedService<DatabaseBackupHostedService>();
        services.AddHostedService<RouteBaseWeb.Services.Heatmaps.HeatmapTileSweepService>();
        services.AddHostedService<RouteBaseWeb.Services.StagingSweepService>();
        services.AddHostedService<RouteBaseWeb.Services.Photos.PhotoOrphanSweepService>();
        // Deletes finished GPX export ZIPs past their retention window — the file is the only record of
        // them, so nothing else would ever reclaim the space.
        services.AddHostedService<RouteBaseWeb.Services.ActivityExportSweepService>();
        // Reclaims expired/revoked share links after a grace period. Hygiene only — expiry and revocation are
        // enforced per-request in ShareLinkAccess.ResolveAsync, not here.
        services.AddHostedService<RouteBaseWeb.Services.Sharing.ShareLinkSweepService>();
        // Discards ACTIVE edit sessions idle > 14 days, releasing their ride checkouts (B6).
        services.AddHostedService<RouteBaseWeb.Services.TrackEdit.EditSessionAgeOutService>();
        return services;
    }
}
