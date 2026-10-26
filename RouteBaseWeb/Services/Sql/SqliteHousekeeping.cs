using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace RouteBaseWeb.Services.Sql;

/// <summary>
/// Shared SQLite housekeeping. <c>VACUUM</c> rebuilds the file, reclaiming pages freed by deletes.
///
/// Runs entirely in WAL: a pinned connection issues <c>wal_checkpoint(TRUNCATE)</c> then
/// <c>VACUUM</c>. VACUUM in WAL trades a temporarily DB-sized WAL file for avoiding an exclusive
/// journal-mode switch — <c>journal_mode=DELETE</c> requires no other open connection, which fails
/// intermittently under EF connection pooling and can strand the file in rollback mode if the
/// process dies before WAL is restored. VACUUM cannot run inside a transaction, so callers must not
/// wrap this call in one. If another connection holds an open transaction the VACUUM throws — the
/// caller is expected to log-and-continue.
/// </summary>
public static class SqliteHousekeeping
{
    public static async Task VacuumAsync(DatabaseFacade db, ILogger log, CancellationToken ct = default)
    {
        // Pin one physical connection so the checkpoint and VACUUM land on the same handle;
        // EF's per-command open/close would otherwise swap it under pooling.
        await db.OpenConnectionAsync(ct);
        try
        {
            await db.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
            await db.ExecuteSqlRawAsync("VACUUM;", ct);
            log.LogDebug("SqliteHousekeeping: checkpoint + VACUUM done (WAL retained)");
        }
        finally
        {
            await db.CloseConnectionAsync();
        }
    }
}
