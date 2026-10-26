using System.Globalization;
using RouteBaseWeb.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.ErrorLog;

/// <summary>
/// Raw ADO access to Error_Log: the diagnostic error store (one row per captured Error/Critical log,
/// OBS-02). Same raw-ADO pin pattern as <see cref="Elevation.DemTileStore"/> / TrackBlobStore; no EF entity
/// (no relations/cascades/queries — persistence ignorance). No FK on user_id: a store write must never fail
/// on a missing Users row. Truncation is applied here so every producer is safe.
/// Not sealed and <see cref="WriteAsync"/> is virtual so the drain-service tests can substitute an
/// instrumented fake store (the write path is the only fail-silent seam worth pinning).
/// </summary>
public class ErrorLogStore(RouteBaseDbContext context, TimeProvider clock)
{
    private const int MessageMax = 2000;
    private const int StackMax = 8000;

    private readonly RouteBaseDbContext _context = context;

    /// <summary>Insert one entry. Message/stack are truncated here so all producers stay within the caps.</summary>
    public virtual async Task WriteAsync(ErrorLogEntry e, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Error_Log
                    (timestamp_utc, source, category, level, message, exception_type, stack_trace, path, user_id)
                VALUES ($ts, $src, $cat, $lvl, $msg, $etype, $stack, $path, $user);";
            Param(cmd, "$ts").Value = e.TimestampUtc;
            Param(cmd, "$src").Value = e.Source;
            Param(cmd, "$cat").Value = e.Category;
            Param(cmd, "$lvl").Value = e.Level;
            Param(cmd, "$msg").Value = Truncate(e.Message, MessageMax) ?? string.Empty;
            Param(cmd, "$etype").Value = (object?)e.ExceptionType ?? DBNull.Value;
            Param(cmd, "$stack").Value = (object?)Truncate(e.StackTrace, StackMax) ?? DBNull.Value;
            Param(cmd, "$path").Value = (object?)e.Path ?? DBNull.Value;
            Param(cmd, "$user").Value = (object?)e.UserId ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Newest-first page (error_id DESC), optionally filtered by level/source ("all" or null = any).</summary>
    public async Task<IReadOnlyList<ErrorLogEntry>> GetPageAsync(
        string? level, string? source, int page, int pageSize, CancellationToken ct = default)
    {
        var (where, filters) = BuildFilter(level, source);
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT timestamp_utc, source, category, level, message, exception_type, stack_trace, path, user_id
                FROM Error_Log {where}
                ORDER BY error_id DESC LIMIT $ps OFFSET $off;";
            foreach (var (name, value) in filters) Param(cmd, name).Value = value;
            Param(cmd, "$ps").Value = pageSize;
            Param(cmd, "$off").Value = Math.Max(0, (page - 1) * pageSize);

            var rows = new List<ErrorLogEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new ErrorLogEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
            return rows;
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Total rows matching the same level/source filter — for the pager.</summary>
    public async Task<int> CountAsync(string? level, string? source, CancellationToken ct = default)
    {
        var (where, filters) = BuildFilter(level, source);
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM Error_Log {where};";
            foreach (var (name, value) in filters) Param(cmd, name).Value = value;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Retention prune: drop rows older than <paramref name="maxAge"/>, then trim to the newest
    /// <paramref name="maxRows"/>. Two independent caps (age OR count) so a burst can't outrun the age window.</summary>
    public async Task PruneAsync(TimeSpan maxAge, int maxRows, CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime.Subtract(maxAge)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using (var byAge = conn.CreateCommand())
            {
                byAge.CommandText = "DELETE FROM Error_Log WHERE timestamp_utc < $cutoff;";
                Param(byAge, "$cutoff").Value = cutoff;
                await byAge.ExecuteNonQueryAsync(ct);
            }
            await using (var byCap = conn.CreateCommand())
            {
                byCap.CommandText =
                    "DELETE FROM Error_Log WHERE error_id NOT IN " +
                    "(SELECT error_id FROM Error_Log ORDER BY error_id DESC LIMIT $max);";
                Param(byCap, "$max").Value = maxRows;
                await byCap.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Empty the store (admin "Clear all").</summary>
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Error_Log;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private static (string Where, List<(string Name, object Value)> Params) BuildFilter(
        string? level, string? source)
    {
        var clauses = new List<string>();
        var ps = new List<(string, object)>();
        if (!string.IsNullOrEmpty(level) && level != "all") { clauses.Add("level = $lvl"); ps.Add(("$lvl", level)); }
        if (!string.IsNullOrEmpty(source) && source != "all") { clauses.Add("source = $src"); ps.Add(("$src", source)); }
        var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
        return (where, ps);
    }

    private static string? Truncate(string? s, int max) =>
        s is not null && s.Length > max ? s[..max] : s;

    private static SqliteParameter Param(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
}
