using System.Globalization;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// Raw ADO access to Recording_Staging: the uncompacted tail of a live recording session, one slim row
/// per captured point (lat/lng/alt/speed/distance/epoch — no sensor channels; live capture has none).
/// The single staging-read/write funnel, shared by the recording append, the live-view poll, the finalize
/// compaction, and <see cref="BlobTrackReader"/>'s spanning-session merge. No EF entity — Dem_Tiles /
/// TrackBlobStore precedent (persistence ignorance). Pins the physical handle via the EF facade's
/// ref-counted Open/Close so GetDbConnection() is valid only inside each call (never a raw conn.OpenAsync).
/// </summary>
public sealed class RecordingStagingStore(RouteBaseDbContext context)
{
    private readonly RouteBaseDbContext _context = context;

    /// <summary>Append a batch starting at <paramref name="startSeq"/> (server-assigned dense ordinal =
    /// current row count). One prepared row-INSERT reused across the batch, inside a transaction;
    /// timestamps stored as epoch seconds.</summary>
    public async Task AppendAsync(
        TrackId trackId, long startSeq, IReadOnlyList<ExternalGpsPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return;

        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            var ambient = AmbientTx();
            // One statement per row must be wrapped or SQLite commits each row separately (a per-row
            // fsync on a file DB). The caller's ambient transaction already is that wrapper; outside one
            // we own a local transaction, so the batch stays as atomic as the single INSERT it replaced.
            // Dispose without Commit rolls back, which is the throw path — no explicit rollback needed.
            var own = ambient is null ? (SqliteTransaction)await conn.BeginTransactionAsync(ct) : null;
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = ambient ?? own;
                cmd.CommandText =
                    "INSERT INTO Recording_Staging (track_ID, seq, lat, lng, altitude, speed, distance, ts_utc, pressure) " +
                    "VALUES ($t,$s,$la,$lo,$al,$sp,$di,$ts,$pr)";
                var pTrack = Param(cmd, "$t");
                var pSeq   = Param(cmd, "$s");
                var pLat   = Param(cmd, "$la");
                var pLng   = Param(cmd, "$lo");
                var pAlt   = Param(cmd, "$al");
                var pSpd   = Param(cmd, "$sp");
                var pDist  = Param(cmd, "$di");
                var pTs    = Param(cmd, "$ts");
                var pPress = Param(cmd, "$pr");
                // Compile once, bind 9 slots per row. The previous shape emitted 8 params PER POINT into
                // one giant statement, and SQLite resolves parameter names by linear scan — so binding
                // cost grew with the SQUARE of the batch (10k points: 13 s at 4k/batch, 65 ms this way).
                // That quadratic is also why a >4000 cap had to exist here; it no longer does.
                cmd.Prepare();
                pTrack.Value = trackId.Value;

                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    pSeq.Value   = startSeq + i;
                    pLat.Value   = p.Lat;
                    pLng.Value   = p.Lng;
                    pAlt.Value   = (object?)p.Altitude ?? DBNull.Value;
                    pSpd.Value   = (object?)p.Speed ?? DBNull.Value;
                    pDist.Value  = (object?)p.Distance ?? DBNull.Value;
                    pTs.Value    = (object?)ToEpoch(p.TimestampUtc) ?? DBNull.Value;
                    pPress.Value = (object?)p.Pressure ?? DBNull.Value;
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                if (own is not null) await own.CommitAsync(ct);
            }
            finally
            {
                if (own is not null) await own.DisposeAsync();
            }
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>All staging points for the track, ordered by seq (= insertion order ≡ GPS_ID order).</summary>
    public Task<List<StagingPoint>> GetPointsAsync(TrackId trackId, CancellationToken ct = default) =>
        ReadAsync(trackId, afterSeq: null, ct);

    /// <summary>Staging points with seq strictly greater than <paramref name="afterSeq"/> (the live-view
    /// catch-up cursor). Seq, not the timestamp: ts_utc is truncated to whole seconds (see
    /// <see cref="ToEpoch"/>), so a ts cursor silently skipped every remaining point of the cursor's
    /// second — LV-01. Pass -1 for "everything".</summary>
    public Task<List<StagingPoint>> GetPointsAfterSeqAsync(TrackId trackId, long afterSeq, CancellationToken ct = default) =>
        ReadAsync(trackId, afterSeq, ct);

    private const string SelectColumns = "SELECT seq, lat, lng, altitude, speed, distance, ts_utc, pressure FROM Recording_Staging ";

    private async Task<List<StagingPoint>> ReadAsync(TrackId trackId, long? afterSeq, CancellationToken ct)
    {
        var points = new List<StagingPoint>();
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            EnlistAmbientTx(cmd);
            cmd.CommandText = afterSeq is null
                ? SelectColumns + "WHERE track_ID = $t ORDER BY seq"
                : SelectColumns + "WHERE track_ID = $t AND seq > $e ORDER BY seq";
            Param(cmd, "$t").Value = trackId.Value;
            if (afterSeq is not null) Param(cmd, "$e").Value = afterSeq.Value;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                points.Add(ReadPoint(reader));
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
        return points;
    }

    /// <summary>Materialize one row of <see cref="SelectColumns"/>.</summary>
    private static StagingPoint ReadPoint(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetDouble(1),
        reader.GetDouble(2),
        reader.IsDBNull(3) ? null : reader.GetDouble(3),
        reader.IsDBNull(4) ? null : reader.GetDouble(4),
        reader.IsDBNull(5) ? null : reader.GetDouble(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetDouble(7));

    /// <summary>Last staged point by seq, or null when the track has none staged. Seeds the append-time
    /// cumulative-distance and previous-point so the leg between batches is accumulated (RS-01/INC-01).</summary>
    public async Task<StagingPoint?> GetLastPointAsync(TrackId trackId, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            EnlistAmbientTx(cmd);
            cmd.CommandText = SelectColumns + "WHERE track_ID = $t ORDER BY seq DESC LIMIT 1;";
            Param(cmd, "$t").Value = trackId.Value;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return ReadPoint(reader);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Max cumulative distance across staging rows, or null when the track has none staged.</summary>
    public async Task<double?> GetMaxDistanceAsync(TrackId trackId, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            EnlistAmbientTx(cmd);
            cmd.CommandText = "SELECT MAX(distance) FROM Recording_Staging WHERE track_ID = $t;";
            Param(cmd, "$t").Value = trackId.Value;
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is null or DBNull ? null : Convert.ToDouble(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Whether the track has ANY staged rows — the "still recording" guard. Stops at the first row
    /// (LIMIT 1), unlike <see cref="CountAsync"/>, which scans the whole index range to count.</summary>
    public async Task<bool> ExistsAsync(TrackId trackId, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            EnlistAmbientTx(cmd);
            cmd.CommandText = "SELECT 1 FROM Recording_Staging WHERE track_ID = $t LIMIT 1;";
            Param(cmd, "$t").Value = trackId.Value;
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Number of staged rows = the next dense seq.</summary>
    public async Task<long> CountAsync(TrackId trackId, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            EnlistAmbientTx(cmd);
            cmd.CommandText = "SELECT COUNT(*) FROM Recording_Staging WHERE track_ID = $t;";
            Param(cmd, "$t").Value = trackId.Value;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>The next append ordinal: MAX(seq)+1, or 0 when the track has no staged rows. One B-tree
    /// descent on the (track_ID, seq) PK, unlike <see cref="CountAsync"/>'s full index-range scan —
    /// O(log n) instead of O(session length) per append batch. Equal to the row count in every
    /// append-reachable state (appends require status 'recording'; the only partial delete,
    /// <see cref="DeleteUpToSeqAsync"/>, runs from finalize paths that require the session to have left
    /// 'recording', so an appendable track's seqs are dense from 0) — and unlike COUNT it still
    /// continues correctly past any orphan rows a partial compaction delete left behind.</summary>
    public async Task<long> NextSeqAsync(TrackId trackId, CancellationToken ct = default)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            EnlistAmbientTx(cmd);
            cmd.CommandText = "SELECT COALESCE(MAX(seq), -1) + 1 FROM Recording_Staging WHERE track_ID = $t;";
            Param(cmd, "$t").Value = trackId.Value;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Drop staged rows with seq ≤ <paramref name="uptoSeq"/> for a track — bounds the finalize
    /// compaction delete to the caller's read snapshot (SVC-02): a batch appended after the snapshot was
    /// taken lands at a higher seq and survives to the next finalize/cleanup cycle instead of being
    /// destroyed. Null means no rows existed at snapshot time, so anything present now is a later arrival
    /// — no-op. No-op when nothing matches.</summary>
    public async Task DeleteUpToSeqAsync(TrackId trackId, long? uptoSeq, CancellationToken ct = default)
    {
        if (uptoSeq is null) return;
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM Recording_Staging WHERE track_ID = {0} AND seq <= {1};", [trackId.Value, uptoSeq.Value], ct);
    }

    // Join the ambient EF transaction if one is active (RS-05): Microsoft.Data.Sqlite throws when a command
    // runs on a connection with an open transaction unless cmd.Transaction points at it. Null-safe — outside
    // a transaction the command behaves exactly as before.
    private void EnlistAmbientTx(SqliteCommand cmd) => cmd.Transaction = AmbientTx();

    private SqliteTransaction? AmbientTx() =>
        (SqliteTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

    // Whole epoch SECONDS — lossy by design (the column is display/finalize input, never a cursor: the
    // catch-up cursor is `seq`, LV-01). Kind is normalized at the DTO edge (CC2-04, RecordingGpsPoint),
    // so ToUniversalTime() is a no-op for API-sourced points; kept as defence in depth for the rest.
    private static long? ToEpoch(DateTime? dt) =>
        dt.HasValue ? (long)(dt.Value.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds : null;

    private static SqliteParameter Param(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }
}

/// <summary>One live-recording staging point: its dense server-assigned ordinal, coords + optional
/// alt/speed/distance, epoch-second timestamp, and raw barometric pressure (hPa) when the phone has a
/// barometer. <c>Seq</c> is the only monotone, lossless ordering key the row carries — <c>TsEpoch</c> is
/// truncated to whole seconds, so two fixes inside one second share it (LV-01).</summary>
public readonly record struct StagingPoint(
    long Seq, double Lat, double Lng, double? Altitude, double? Speed, double? Distance, long? TsEpoch, double? Pressure);
