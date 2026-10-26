using System.Globalization;
using RouteBaseWeb.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Elevation;

/// <summary>
/// Raw ADO access to Dem_Tiles: the persistent raw-PNG terrarium tile cache (one blob per z/x/y).
/// Same raw-ADO pin pattern as <see cref="Tracks.TrackBlobStore"/>; no EF entity (no relations, cascades
/// or EF queries — persistence ignorance). DEM is static + user-independent, so there is no expiry and
/// the per-user reset leaves the cache intact.
/// </summary>
public sealed class DemTileStore(RouteBaseDbContext context, TimeProvider clock)
{
    private readonly RouteBaseDbContext _context = context;

    /// <summary>Raw PNG bytes for one tile, or null if {z, x, y} isn't cached.</summary>
    public async Task<byte[]?> ReadTileAsync(int z, int x, int y, CancellationToken ct = default)
    {
        // Pin via the EF facade (ref-counted Open/Close); GetDbConnection() is valid only inside it.
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT png FROM Dem_Tiles WHERE z = $z AND x = $x AND y = $y LIMIT 1;";
            Param(cmd, "$z").Value = z;
            Param(cmd, "$x").Value = x;
            Param(cmd, "$y").Value = y;

            var result = await cmd.ExecuteScalarAsync(ct);
            return result as byte[];
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Cache one tile's PNG. INSERT OR IGNORE — concurrent refreshes may race on the same tile with an
    /// identical payload (DEM is static), so the first write wins and the rest no-op.
    /// </summary>
    public async Task InsertTileAsync(int z, int x, int y, byte[] png, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT OR IGNORE INTO Dem_Tiles (z, x, y, png, fetched_at) VALUES ($z, $x, $y, $p, $f);";
            Param(cmd, "$z").Value = z;
            Param(cmd, "$x").Value = x;
            Param(cmd, "$y").Value = y;
            Param(cmd, "$p").Value = png;
            Param(cmd, "$f").Value =
                clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Purge one tile — used when a cached PNG turns out to be undecodable (poisoned entry, e.g. a
    /// truncated body stored by a pre-validation build), so the next operation can refetch it.
    /// </summary>
    public async Task DeleteTileAsync(int z, int x, int y, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Dem_Tiles WHERE z = $z AND x = $x AND y = $y;";
            Param(cmd, "$z").Value = z;
            Param(cmd, "$x").Value = x;
            Param(cmd, "$y").Value = y;

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private static SqliteParameter Param(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
}
