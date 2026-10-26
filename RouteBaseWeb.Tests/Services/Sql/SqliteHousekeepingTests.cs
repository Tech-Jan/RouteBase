using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sql;

/// <summary>
/// RM-06: the shared VACUUM helper stays in WAL (no journal-mode switch), and reset only runs it
/// when a bulk group was deleted (credentials/settings-only resets skip the full-file VACUUM).
/// </summary>
public class SqliteHousekeepingTests
{
    private const string VacuumLogFragment = "checkpoint + VACUUM";

    [Fact]
    public async Task Vacuum_LeavesJournalModeWal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hk_{Guid.NewGuid():N}.db");
        // Pooling=False so the file is released on dispose without ClearAllPools() — a global pool
        // flush would destroy the shared-cache in-memory DBs of tests running in parallel.
        var connString = $"Data Source={path};Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connString).Options;
            await using (var context = new RouteBaseDbContext(options))
            {
                await context.Database.EnsureCreatedAsync();
                await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;");

                // Create free pages: insert then delete rows so VACUUM has something to reclaim.
                context.Users.Add(new AppUser { UserId = "u", DisplayName = "u", Email = "u@t.com", IsAdmin = false });
                await context.SaveChangesAsync();
                context.Users.Remove(await context.Users.FirstAsync());
                await context.SaveChangesAsync();

                await SqliteHousekeeping.VacuumAsync(context.Database, NullLogger.Instance);

                await context.Database.OpenConnectionAsync();
                var conn = context.Database.GetDbConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode;";
                var mode = (string)(await cmd.ExecuteScalarAsync())!;
                await context.Database.CloseConnectionAsync();

                Assert.Equal("wal", mode, ignoreCase: true);
            }
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task ResetCredentialsOnly_SkipsVacuum()
    {
        var (context, service, log) = await NewResetServiceAsync();
        await using var _ = context;

        context.AppCredentials.Add(new AppCredentials { Id = 1, Type = CredentialTypes.StravaToken, UserId = "me" });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await service.ResetDatabaseAsync(new DeleteOptions { StravaToken = true });

        Assert.DoesNotContain(log.Messages, m => m.Contains(VacuumLogFragment, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetActivities_RunsVacuum()
    {
        var (context, service, log) = await NewResetServiceAsync();
        await using var _ = context;

        context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = "me", Visibility = "private" });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await service.ResetDatabaseAsync(new DeleteOptions { Activities = true });

        Assert.Contains(log.Messages, m => m.Contains(VacuumLogFragment, StringComparison.Ordinal));
    }

    private static async Task<(RouteBaseDbContext, DatabaseResetService, CapturingLogger)> NewResetServiceAsync()
    {
        var connString = $"Data Source=file:hk_reset_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connString).Options;
        var context = new RouteBaseDbContext(options);
        await context.Database.EnsureCreatedAsync();
        // The reset deletes from the non-EF track point stores; they must exist.
        var schema = new DbSchemaManager(context);
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureRecordingStagingTableAsync();
        await schema.EnsureTrackEditTablesAsync(); // reset also wipes Track_Blob_History/Activity_Edit_Session/Track_Edit_Commit
        await schema.EnsureErrorLogTableAsync(); // B8: every reset also wipes the caller's Error_Log rows
        context.Users.Add(new AppUser { UserId = "me", DisplayName = "me", Email = "me@t.com", IsAdmin = false });
        await context.SaveChangesAsync();

        var log = new CapturingLogger();
        var service = new DatabaseResetService(context, log, new TestCurrentUserService("me"));
        return (context, service, log);
    }

    /// <summary>Captures formatted log messages so a test can assert VacuumAsync ran (or didn't).</summary>
    private sealed class CapturingLogger : ILogger<DatabaseResetService>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
