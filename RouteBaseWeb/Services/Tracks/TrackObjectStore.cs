using RouteBaseWeb.Models;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// SQLite-backed <see cref="ITrackObjectStore"/>: Track_Object rows keyed by sha256. Raw ADO, no EF entity
/// (Dem_Tiles precedent — persistence ignorance; no relations or queries beyond a PK lookup). Pins the
/// physical handle via the EF facade's ref-counted Open/Close, so GetDbConnection() is only valid inside
/// each call (never a raw conn.OpenAsync — that bypasses EF's ref-count and hides the real lifetime).
/// </summary>
public sealed class TrackObjectStore(RouteBaseDbContext context, TimeProvider clock) : ITrackObjectStore
{
    private readonly RouteBaseDbContext _context = context;

    /// <inheritdoc />
    public async Task<string> PutAsync(byte[] data, int codecVersion, int pointCount, CancellationToken ct = default)
    {
        string sha = ITrackObjectStore.ShaOf(data);

        // INSERT OR IGNORE, not REPLACE: the row for a sha can only ever hold these exact bytes, so a
        // second write of the same content is a no-op rather than a rewrite of a blob others reference.
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO Track_Object (sha256, codec_version, point_count, byte_len, data, created_at) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5});",
            [sha, codecVersion, pointCount, data.Length, data, SqliteAdo.Fmt(clock.GetUtcNow().UtcDateTime)], ct);

        return sha;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string sha256, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            SqliteAdo.EnlistAmbientTx(_context, cmd);
            cmd.CommandText = "SELECT data FROM Track_Object WHERE sha256 = $s LIMIT 1;";
            SqliteAdo.Param(cmd, "$s").Value = sha256;
            return await cmd.ExecuteScalarAsync(ct) as byte[];
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <inheritdoc />
    public async Task<int> SweepUnreferencedAsync(
        IReadOnlyCollection<string>? candidates = null, CancellationToken ct = default)
    {
        const string unreferenced = "sha256 NOT IN (SELECT object_sha FROM Activity_Version)";

        if (candidates is null)
            return await _context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM Track_Object WHERE {unreferenced};", ct);

        if (candidates.Count == 0) return 0;

        var placeholders = string.Join(", ", candidates.Select((_, i) => $"{{{i}}}"));
        // EF1002: interpolated segments are synthesized {0}/{1}/... placeholder tokens and a compile-time
        // constant, never data — the candidate values themselves go through the params array below.
#pragma warning disable EF1002
        return await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM Track_Object WHERE sha256 IN ({placeholders}) AND {unreferenced};",
            candidates.Cast<object>().ToArray(), ct);
#pragma warning restore EF1002
    }
}
