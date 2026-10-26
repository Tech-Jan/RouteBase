using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Sql;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sql;

/// <summary>
/// Pins that <see cref="SqliteBusyTimeoutInterceptor"/> actually reaches the connection — the exact failure
/// the "pragma set on one path only is worse than none" warning is about. Reads the pragma back off the SAME
/// physical handle EF hands to the raw-ADO stores (<c>GetDbConnection()</c>), which is how every non-EF-query
/// path in the app touches SQLite.
/// </summary>
public class SqliteBusyTimeoutInterceptorTests
{
    [Fact]
    public async Task Interceptor_PinsBusyTimeout_OnEveryConnectionEfOpens()
    {
        // Without the interceptor SQLite's own busy handler is off (0): Microsoft.Data.Sqlite absorbs
        // SQLITE_BUSY in managed code via CommandTimeout and never calls sqlite3_busy_timeout — the silent
        // dependency this fix removes.
        Assert.NotEqual(SqliteBusyTimeoutInterceptor.Milliseconds, await ReadBusyTimeoutAsync(withInterceptor: false));

        // With it, the connection carries the explicit timeout — and because the raw-ADO stores borrow this
        // very handle via GetDbConnection(), reading it here proves those paths are covered too.
        Assert.Equal(SqliteBusyTimeoutInterceptor.Milliseconds, await ReadBusyTimeoutAsync(withInterceptor: true));
    }

    // Opens a fresh, non-pooled in-memory DB (so no pooled handle carries busy_timeout in from another call),
    // opens the EF connection (firing the ConnectionOpened interceptor), and reads PRAGMA busy_timeout off the
    // handle EF exposes via GetDbConnection.
    private static async Task<long> ReadBusyTimeoutAsync(bool withInterceptor)
    {
        var connStr = $"Data Source=file:bt_{Guid.NewGuid():N}?mode=memory&cache=shared;Pooling=False";
        await using var hold = new SqliteConnection(connStr);
        await hold.OpenAsync(); // keep the in-memory DB alive for the duration of the read

        var builder = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connStr);
        if (withInterceptor) builder.AddInterceptors(new SqliteBusyTimeoutInterceptor());
        await using var ctx = new RouteBaseDbContext(builder.Options);

        await ctx.Database.OpenConnectionAsync(); // fires ConnectionOpenedAsync
        try
        {
            var conn = (SqliteConnection)ctx.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout;";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        finally
        {
            await ctx.Database.CloseConnectionAsync();
        }
    }
}
