using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Health;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// H1: the /healthz DB probe. Green when SELECT 1 succeeds against a real SQLite file; false (→ 503)
/// when the context factory can't hand out a usable context. Pure of ASP.NET types, so no host needed.
/// </summary>
public class HealthProbeTests
{
    // Constructs a fresh context per call from fixed options (mirrors IDbContextFactory semantics).
    private sealed class FileDbFactory : IDbContextFactory<RouteBaseDbContext>
    {
        private readonly DbContextOptions<RouteBaseDbContext> _options;
        public FileDbFactory(string connString) =>
            _options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connString).Options;
        public RouteBaseDbContext CreateDbContext() => new(_options);
    }

    private sealed class ThrowingFactory : IDbContextFactory<RouteBaseDbContext>
    {
        public RouteBaseDbContext CreateDbContext() =>
            throw new InvalidOperationException("simulated DB unavailable");
    }

    [Fact]
    public async Task IsHealthyAsync_ReturnsTrue_WhenDbReachable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"health_{Guid.NewGuid():N}.db");
        var connString = $"Data Source={path};Pooling=False";
        try
        {
            await using (var ctx = new RouteBaseDbContext(
                new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connString).Options))
                await ctx.Database.EnsureCreatedAsync();

            var healthy = await HealthProbe.IsHealthyAsync(new FileDbFactory(connString), CancellationToken.None);

            Assert.True(healthy);
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task IsHealthyAsync_ReturnsFalse_WhenProbeThrows()
    {
        var healthy = await HealthProbe.IsHealthyAsync(new ThrowingFactory(), CancellationToken.None);

        Assert.False(healthy);
    }
}
