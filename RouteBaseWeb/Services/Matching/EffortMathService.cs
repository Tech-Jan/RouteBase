using System.Globalization;
using System.Text;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Sql;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Matching;

/// <summary>
/// I/O shell around <see cref="EffortMathCore"/>: the post-match effort math — the pair-coherence
/// offset search + the per-effort aggregates. Reads effort windows + segment pins on the caller's
/// pinned connection, decodes each track via <see cref="ITrackReader"/>, runs the pure core, and
/// funnels results through chunked raw-SQL UPDATEs. The two quality-guard DELETEs (S8.13) run verbatim.
///
/// Scope (invariant 2): offsets scope own efforts (S8.1); aggregates fill ALL owners (S9.1, friends
/// included); a decode-null track still emits the guessed-id fallback with NULL offsets (S8.11/12) and
/// skips aggregates (S9.10). Raw SqliteCommand + DBNull for NULLs (I2a lesson: not EF
/// ExecuteSqlRawAsync). Never opens/closes the pinned connection (S0.4).
/// </summary>
public sealed class EffortMathService : IEffortMath
{
    private readonly RouteBaseDbContext _context;
    private readonly ITrackReader _trackReader;
    private readonly ILogger<EffortMathService> _logger;

    // Rows per multi-statement UPDATE command. 5 params/offset row, 8/aggregate row → well under the
    // SQLite variable cap either way; keeps the funnel index-seek cheap (TE-16 cost model).
    private const int WriteChunk = 200;

    public EffortMathService(
        RouteBaseDbContext context,
        ITrackReader trackReader,
        ILogger<EffortMathService> logger)
    {
        _context = context;
        _trackReader = trackReader;
        _logger = logger;
    }

    public async Task CalculateOffsetsAsync(AnalysisScope scope, int? onlyTrackId = null, CancellationToken ct = default)
    {
        // Pinned connection (already open by AnalyzeSegmentMatchesAsync). No Open/Close here (S0.4).
        var conn = (SqliteConnection)_context.Database.GetDbConnection();

        // S8.2 pins: first + last Segment_GPS point in ascending seg_GPS_ID order. No geometry → the SQL
        // pins table is empty and no own efforts could have been inserted → nothing to do.
        var pins = await LoadPinsAsync(conn, scope.SegmentId, ct);
        if (pins is null) return;
        var (pinStartLat, pinStartLng, pinEndLat, pinEndLng) = pins.Value;

        double? segmentDistance = await LoadSegmentDistanceAsync(conn, scope.SegmentId, ct);

        // S8.1: own efforts on the segment with both guessed ids (own-effort scope), optionally one track.
        var windows = await LoadOwnEffortWindowsAsync(conn, scope, onlyTrackId, ct);

        var results = new List<OffsetResult>(windows.Count);
        var decoded = new Dictionary<int, DecodedTrack?>();
        foreach (var w in windows)
        {
            ct.ThrowIfCancellationRequested();
            var track = await GetTrackMemoAsync(decoded, new TrackId(w.TrackId), ct);
            // Decode null does NOT skip: the SQL fallback still sets calc = guessed with NULL offsets.
            results.Add(track is null
                ? new OffsetResult(w.GpsInSegmentId, w.EntryGuessed, null, w.ExitGuessed, null)
                : EffortMathCore.ComputeOffsets(
                    w, track, pinStartLat, pinStartLng, pinEndLat, pinEndLng, segmentDistance));
        }

        await WriteOffsetsAsync(conn, results, ct);
        await RunQualityGuardAsync(conn, scope, onlyTrackId, ct);   // S8.13 verbatim DELETEs (exit → entry).
    }

    public async Task PopulateAggregatesAsync(AnalysisScope scope, int? onlyTrackId = null, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)_context.Database.GetDbConnection();

        // S9.1: ALL efforts on the segment (any owner) with both COALESCE(calc,guess) bounds present,
        // optionally restricted to one track (incremental per-import path).
        var windows = await LoadAggregateWindowsAsync(conn, scope.SegmentId, onlyTrackId, ct);

        var results = new List<AggregateResult>(windows.Count);
        var decoded = new Dictionary<int, DecodedTrack?>();
        foreach (var w in windows)
        {
            ct.ThrowIfCancellationRequested();
            var track = await GetTrackMemoAsync(decoded, new TrackId(w.TrackId), ct);
            if (track is null) continue;                     // SQL INNER JOIN GPS → no row (S9.10).
            var r = EffortMathCore.ComputeAggregates(w, track);
            if (r is not null) results.Add(r);               // empty window → no write (S9.10).
        }

        await WriteAggregatesAsync(conn, results, ct);
    }

    // -------------------------------------------------------------------------
    // Reads (pinned connection, no Open/Close)
    // -------------------------------------------------------------------------

    // S8.2: start pin = Segment_GPS row at MIN(seg_GPS_ID), end pin = row at MAX. Ascending order →
    // first/last row. NULL coords → NaN (bands empty → fallback; ComputeOffsets excludes NaN). Null
    // when the segment has no points.
    private static async Task<(double startLat, double startLng, double endLat, double endLng)?> LoadPinsAsync(
        SqliteConnection conn, SegmentId segmentId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GPS_lat, GPS_long FROM Segment_GPS WHERE segment_ID = $s ORDER BY seg_GPS_ID";
        cmd.Parameters.Add(new SqliteParameter("$s", segmentId.Value));

        double firstLat = double.NaN, firstLng = double.NaN, lastLat = double.NaN, lastLng = double.NaN;
        bool any = false;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            double la = r.IsDBNull(0) ? double.NaN : r.GetDouble(0);
            double lo = r.IsDBNull(1) ? double.NaN : r.GetDouble(1);
            if (!any) { firstLat = la; firstLng = lo; any = true; }
            lastLat = la; lastLng = lo;
        }
        return any ? (firstLat, firstLng, lastLat, lastLng) : null;
    }

    private static async Task<double?> LoadSegmentDistanceAsync(
        SqliteConnection conn, SegmentId segmentId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT segment_distance FROM Segment WHERE segment_ID = $s";
        cmd.Parameters.Add(new SqliteParameter("$s", segmentId.Value));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToDouble(result, CultureInfo.InvariantCulture);
    }

    // S8.1: own efforts on the segment with both guessed ids present (optionally one track).
    private static async Task<List<EffortWindow>> LoadOwnEffortWindowsAsync(
        SqliteConnection conn, AnalysisScope scope, int? onlyTrackId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT GPSinSegment_ID, track_id, gps_id_entry_guessed, gps_id_exit_guessed
              FROM GPSinSegment
              WHERE segment_id = $s
                AND track_id IN (SELECT track_ID FROM Activity WHERE user_id = $u)
                AND gps_id_entry_guessed IS NOT NULL
                AND gps_id_exit_guessed  IS NOT NULL"
            + (onlyTrackId is int ? " AND track_id = $t" : "");
        cmd.Parameters.Add(new SqliteParameter("$s", scope.SegmentId.Value));
        cmd.Parameters.Add(new SqliteParameter("$u", scope.UserId));
        if (onlyTrackId is int t) cmd.Parameters.Add(new SqliteParameter("$t", t));

        var list = new List<EffortWindow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new EffortWindow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    // S9.1: all owners on the segment with both COALESCE(calc,guess) bounds; lo/hi swap-normalized.
    // onlyTrackId (incremental path) restricts to one track — its rows' per-effort values are identical.
    private static async Task<List<AggregateWindow>> LoadAggregateWindowsAsync(
        SqliteConnection conn, SegmentId segmentId, int? onlyTrackId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT GPSinSegment_ID, track_id,
                     COALESCE(gps_id_entry_calculated, gps_id_entry_guessed) AS ce,
                     COALESCE(gps_id_exit_calculated,  gps_id_exit_guessed)  AS cx
              FROM GPSinSegment
              WHERE segment_id = $s
                AND COALESCE(gps_id_entry_calculated, gps_id_entry_guessed) IS NOT NULL
                AND COALESCE(gps_id_exit_calculated,  gps_id_exit_guessed)  IS NOT NULL"
            + (onlyTrackId is int ? " AND track_id = $t" : "");
        cmd.Parameters.Add(new SqliteParameter("$s", segmentId.Value));
        if (onlyTrackId is int t) cmd.Parameters.Add(new SqliteParameter("$t", t));

        var list = new List<AggregateWindow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            int ce = r.GetInt32(2), cx = r.GetInt32(3);
            int lo = Math.Min(ce, cx), hi = Math.Max(ce, cx);   // S9.1 reversed-window normalization.
            list.Add(new AggregateWindow(r.GetInt32(0), r.GetInt32(1), lo, hi));
        }
        return list;
    }

    // SD-04: memo NULL decodes only. Holding a strong ref to every distinct decoded track for the whole
    // segment run silently bypasses TrackReaderCache's byte budget (256 MB), doubling worst-case resident
    // set next to the LRU. Positive-hit dedup is already the cache's job (same-track windows arrive
    // adjacent → ~1 miss per track); null lookups cost nothing to hold and are worth deduping.
    private async Task<DecodedTrack?> GetTrackMemoAsync(
        Dictionary<int, DecodedTrack?> memo, TrackId trackId, CancellationToken ct)
    {
        if (memo.TryGetValue(trackId.Value, out var hit)) return hit;
        var track = await _trackReader.GetTrackAsync(trackId, ct);
        if (track is null) memo[trackId.Value] = null;   // only cache the (cheap, repeatable) null lookups.
        return track;
    }

    // -------------------------------------------------------------------------
    // Writes (chunked multi-statement raw SQL; DBNull for NULLs — I2a lesson)
    // -------------------------------------------------------------------------

    // S8.10/S8.11/S8.12: all four offset columns per row, atomically (calc ids always set; offsets NULL
    // only on a missing/NaN guessed point).
    private static async Task WriteOffsetsAsync(SqliteConnection conn, List<OffsetResult> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        for (int start = 0; start < rows.Count; start += WriteChunk)
        {
            int end = Math.Min(start + WriteChunk, rows.Count);
            var sb = new StringBuilder();
            await using var cmd = conn.CreateCommand();
            for (int i = start; i < end; i++)
            {
                var r = rows[i];
                sb.Append(CultureInfo.InvariantCulture,
                    $"UPDATE GPSinSegment SET gps_id_entry_calculated=@e{i}, Offset_entry=@oe{i}, " +
                    $"gps_id_exit_calculated=@x{i}, Offset_exit=@ox{i} WHERE GPSinSegment_ID=@id{i};");
                cmd.Parameters.Add(new SqliteParameter($"@e{i}", r.EntryCalc));
                cmd.Parameters.Add(new SqliteParameter($"@oe{i}", (object?)r.OffsetEntry ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter($"@x{i}", r.ExitCalc));
                cmd.Parameters.Add(new SqliteParameter($"@ox{i}", (object?)r.OffsetExit ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter($"@id{i}", r.GpsInSegmentId));
            }
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // S9.3–S9.9: the seven effort_* columns for each computed row (only rows that produced a result).
    private static async Task WriteAggregatesAsync(SqliteConnection conn, List<AggregateResult> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        for (int start = 0; start < rows.Count; start += WriteChunk)
        {
            int end = Math.Min(start + WriteChunk, rows.Count);
            var sb = new StringBuilder();
            await using var cmd = conn.CreateCommand();
            for (int i = start; i < end; i++)
            {
                var r = rows[i];
                sb.Append(CultureInfo.InvariantCulture,
                    $"UPDATE GPSinSegment SET effort_distance=@d{i}, effort_elevation=@el{i}, " +
                    $"effort_time=@tm{i}, effort_speed_avg=@sp{i}, effort_hr_avg=@hr{i}, " +
                    $"effort_power_avg=@pw{i}, effort_cadence_avg=@cd{i} WHERE GPSinSegment_ID=@id{i};");
                cmd.Parameters.Add(new SqliteParameter($"@d{i}", r.Dist));
                cmd.Parameters.Add(new SqliteParameter($"@el{i}", r.Elev));
                cmd.Parameters.Add(new SqliteParameter($"@tm{i}", r.TimeS));
                cmd.Parameters.Add(new SqliteParameter($"@sp{i}", r.SpeedAvg));
                cmd.Parameters.Add(new SqliteParameter($"@hr{i}", (object?)r.HrAvg ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter($"@pw{i}", (object?)r.PowerAvg ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter($"@cd{i}", (object?)r.CadenceAvg ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter($"@id{i}", r.GpsInSegmentId));
            }
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // S8.13: the two verbatim quality-guard DELETEs — Offset_exit first, then Offset_entry; strict >,
    // own+segment scope, NULL offsets survive. These read no GPS (pure GPSinSegment DELETEs).
    private async Task RunQualityGuardAsync(SqliteConnection conn, AnalysisScope scope, int? onlyTrackId, CancellationToken ct)
    {
        int removedExit = await ExecuteGuardAsync(conn, scope, OffsetColumn.Exit, onlyTrackId, ct);
        if (removedExit > 0)
            _logger.LogInformation("[Offsets] Removed {Count} effort(s) where Offset_exit > 0.005", removedExit);

        int removedEntry = await ExecuteGuardAsync(conn, scope, OffsetColumn.Entry, onlyTrackId, ct);
        if (removedEntry > 0)
            _logger.LogInformation("[Offsets] Removed {Count} effort(s) where Offset_entry > 0.005", removedEntry);
    }

    /// <summary>
    /// The two quality-guard columns. An enum rather than a string because <see cref="ExecuteGuardAsync"/>
    /// interpolates it into SQL — the only column-name interpolation in the codebase. A closed type makes
    /// "what if someone passes a crafted identifier" unrepresentable instead of merely guarded by a comment.
    /// </summary>
    private enum OffsetColumn { Entry, Exit }

    private static string SqlName(OffsetColumn column) => column switch
    {
        OffsetColumn.Entry => "Offset_entry",
        OffsetColumn.Exit => "Offset_exit",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "unknown offset column"),
    };

    private static async Task<int> ExecuteGuardAsync(
        SqliteConnection conn, AnalysisScope scope, OffsetColumn column, int? onlyTrackId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"DELETE FROM GPSinSegment WHERE {SqlName(column)} > 0.005 " +
            "AND GPSinSegment.segment_id = $s " +
            "AND GPSinSegment.track_id IN (SELECT track_ID FROM Activity WHERE user_id = $u)"
            + (onlyTrackId is int ? " AND GPSinSegment.track_id = $t" : "");
        cmd.Parameters.Add(new SqliteParameter("$s", scope.SegmentId.Value));
        cmd.Parameters.Add(new SqliteParameter("$u", scope.UserId));
        if (onlyTrackId is int t) cmd.Parameters.Add(new SqliteParameter("$t", t));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
