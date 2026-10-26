using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services.Queue;

/// <summary>
/// Shared scope bootstrap for operation handlers: queue-worker code has no HttpContext, so
/// scoped services (StravaAuthService, VisibilityService, …) resolve the acting user through an
/// explicit <see cref="ICurrentUserService.SetUserIdOverride"/>. One scope per operation run is
/// load-bearing (per-run token cache, DbContext lifetime — SV-01/ID-06); handlers own it.
/// </summary>
internal static class OperationScope
{
    /// <summary>
    /// Creates a DI scope impersonating <paramref name="userId"/>. An empty id (anonymous
    /// enqueue — no production enqueuer does this) leaves the scope un-impersonated.
    /// </summary>
    public static IServiceScope Create(IServiceScopeFactory scopeFactory, string userId)
    {
        var scope = scopeFactory.CreateScope();
        if (!string.IsNullOrEmpty(userId))
            scope.ServiceProvider.GetRequiredService<ICurrentUserService>().SetUserIdOverride(userId);
        return scope;
    }
}
