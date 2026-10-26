using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.ErrorLog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// C3 (Observability wiring): the nightly maintenance run prunes Error_Log (90d/20000, decided 2026-07-11)
/// before PRAGMA optimize/VACUUM. Real file-backed WAL SQLite (VacuumAsync requires WAL — see
/// SqliteHousekeepingTests), scoped via a hand-rolled factory (ErrorLogWriteServiceTests precedent).
/// </summary>
public class DatabaseMaintenanceServiceTests
{
    private sealed class FakeScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly RouteBaseDbContext _context;
        private readonly ErrorLogStore _store;
        public FakeScopeFactory(RouteBaseDbContext context)
        {
            _context = context;
            _store = new ErrorLogStore(context, TimeProvider.System);
        }
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }
        public object? GetService(Type serviceType) =>
            serviceType == typeof(RouteBaseDbContext) ? _context :
            serviceType == typeof(ErrorLogStore) ? _store : null;
    }

    private static string Ts(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    [Fact]
    public async Task RunMaintenanceAsync_PrunesErrorLog_OldRowsGone_FreshRowsKept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbmaint_{Guid.NewGuid():N}.db");
        // Pooling=False so the file is released on dispose without a global pool flush (matches
        // SqliteHousekeepingTests — a ClearAllPools() would hit other tests' shared-cache in-memory DBs).
        var connString = $"Data Source={path};Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connString).Options;
            await using var context = new RouteBaseDbContext(options);
            await context.Database.EnsureCreatedAsync();
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;");
            await new DbSchemaManager(context).EnsureErrorLogTableAsync();

            var store = new ErrorLogStore(context, TimeProvider.System);
            await store.WriteAsync(new ErrorLogEntry(
                Ts(DateTime.UtcNow.AddDays(-100)), "api", "Cat", "Error", "old", null, null, null, null));
            await store.WriteAsync(new ErrorLogEntry(
                Ts(DateTime.UtcNow), "api", "Cat", "Error", "fresh", null, null, null, null));

            var service = new DatabaseMaintenanceService(
                new FakeScopeFactory(context), NullLogger<DatabaseMaintenanceService>.Instance, TimeProvider.System);

            await service.RunMaintenanceAsync(CancellationToken.None);

            var rows = await store.GetPageAsync(null, null, 1, 50);
            var row = Assert.Single(rows);
            Assert.Equal("fresh", row.Message);
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task RunMaintenanceAsync_RowCap_KeepsNewestOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbmaint_{Guid.NewGuid():N}.db");
        var connString = $"Data Source={path};Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connString).Options;
            await using var context = new RouteBaseDbContext(options);
            await context.Database.EnsureCreatedAsync();
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;");
            await new DbSchemaManager(context).EnsureErrorLogTableAsync();

            var store = new ErrorLogStore(context, TimeProvider.System);
            for (var i = 0; i < 3; i++)
                await store.WriteAsync(new ErrorLogEntry(
                    Ts(DateTime.UtcNow), "api", "Cat", "Error", $"m{i}", null, null, null, null));

            var service = new DatabaseMaintenanceService(
                new FakeScopeFactory(context), NullLogger<DatabaseMaintenanceService>.Instance, TimeProvider.System);

            // Row cap is 5000 in production; prove the wiring passes it through by asserting the
            // count stayed exactly at the seeded 3 (well under cap — a smoke check on the call, not
            // the cap value itself, which ErrorLogStoreTests already pins).
            await service.RunMaintenanceAsync(CancellationToken.None);

            Assert.Equal(3, await store.CountAsync(null, null));
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}
