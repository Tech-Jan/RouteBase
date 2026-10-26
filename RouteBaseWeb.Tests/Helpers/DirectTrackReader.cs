using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// Test <see cref="ITrackReader"/> that reads a track's CURRENT content (head version → object) on the pinned
/// analysis context and decodes it via the production codec. The ref-counted Open/Close nests safely inside
/// the analysis window (S0.4). Shared by the engine-wired pipeline / gate / MatchEngine fixtures so each
/// constructs the engine over one reader. No cache — every call re-reads, so a test that edits a track sees
/// the new head immediately.
/// </summary>
public sealed class DirectTrackReader : ITrackReader
{
    private readonly RouteBaseDbContext _db;
    public DirectTrackReader(RouteBaseDbContext db) => _db = db;

    public async Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default)
    {
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_db.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT o.data FROM Activity_Version v " +
                "JOIN Track_Object o ON o.sha256 = v.object_sha " +
                "WHERE v.track_id = $t AND v.is_head = 1 LIMIT 1;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$t";
            p.Value = trackId.Value;
            cmd.Parameters.Add(p);
            var blob = await cmd.ExecuteScalarAsync(ct) as byte[];
            return blob == null ? null : TrackBlobCodec.Decode(blob);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}
