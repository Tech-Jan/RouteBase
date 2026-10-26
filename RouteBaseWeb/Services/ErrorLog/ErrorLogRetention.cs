namespace RouteBaseWeb.Services.ErrorLog;

/// <summary>
/// Retention policy for Error_Log (user decision 2026-07-11, overrides the A1-4 30d/5000 rec).
/// Single source so the nightly <see cref="DatabaseMaintenanceService"/> prune and the admin
/// "Prune now" button can't drift apart.
/// </summary>
public static class ErrorLogRetention
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);
    public const int MaxRows = 20000;
}
