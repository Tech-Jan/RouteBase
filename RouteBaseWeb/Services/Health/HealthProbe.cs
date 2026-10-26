using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Health;

/// <summary>
/// Cheap liveness/readiness probe for <c>GET /healthz</c> (H1). Opens a short-lived context off the
/// factory and runs <c>SELECT 1</c> — proving the app can actually reach its DB, not just that Kestrel
/// answered. Any failure is swallowed into a <c>false</c> so the endpoint can return 503 without ever
/// throwing into the request pipeline. Pure of ASP.NET types → unit-testable without a host.
/// </summary>
public static class HealthProbe
{
    public static async Task<bool> IsHealthyAsync(
        IDbContextFactory<RouteBaseDbContext> contextFactory, CancellationToken ct = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT 1;", ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
