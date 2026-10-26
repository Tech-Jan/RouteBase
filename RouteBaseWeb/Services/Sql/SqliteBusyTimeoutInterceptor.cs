using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// Sets <c>PRAGMA busy_timeout</c> on every SQLite connection EF opens.
///
/// Why an interceptor and not a boot-time statement: busy_timeout is CONNECTION-scoped (like
/// <c>foreign_keys</c>, unlike file-scoped <c>journal_mode</c> which persists in the db file), so it dies
/// with each pooled handle and cannot be pinned once at boot. It must be
/// re-applied on every open. Microsoft.Data.Sqlite has no <c>busy_timeout</c> connection-string keyword (it
/// has <c>Foreign Keys</c>, which is how that pragma reaches every handle), so this hook is the equivalent
/// for busy_timeout.
///
/// Why this reaches EVERY path: every physical handle in this app is opened by EF Core from the single
/// <see cref="RouteBaseDbContext"/> connection string — request + the nine background writers, the raw-ADO
/// stores that borrow the handle via <c>GetDbConnection()</c> (TrackObjectStore, RecordingStagingStore),
/// and the VACUUM / <c>VACUUM INTO</c> backup path (both run <c>ExecuteSql*</c> on the EF connection). None
/// call <c>new SqliteConnection</c>. Registering this on the <c>DbContextFactory</c> options therefore
/// covers all of them; a raw connection added later would need its own set (and should be flagged in review).
///
/// What it protects against: Microsoft.Data.Sqlite absorbs <c>SQLITE_BUSY</c> with a MANAGED retry loop
/// bounded by <c>CommandTimeout</c> (30 s) — it never calls <c>sqlite3_busy_timeout</c>, so SQLite's own
/// busy handler is 0 (off). Nine background writers plus request writers lean on that implicit retry. A
/// provider/bundle swap, or a lowered CommandTimeout, would silently turn lock contention into thrown
/// errors. Setting the pragma to the SAME 30 s makes the wait SQLite's explicit busy handler instead — no
/// behaviour change today, but the dependency on a provider-internal default is gone. This repo has been
/// bitten by exactly that class before (FK enforcement, temp-table drops).
/// </summary>
public sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// Milliseconds SQLite's busy handler waits for a lock before returning <c>SQLITE_BUSY</c>. Matched to
    /// the provider's 30 s <c>CommandTimeout</c> so contention behaviour is unchanged; see the class remarks.
    /// </summary>
    public const int Milliseconds = 30_000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA busy_timeout = {Milliseconds};";
            cmd.ExecuteNonQuery();
        }
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA busy_timeout = {Milliseconds};";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
