using System.Globalization;
using System.Text;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Sql;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Matching;

/// <summary>
/// I/O shell around <see cref="MatchEngineCore"/>: the match-stage adapter (stages 1–6 + the INSERT
/// gate). Reads candidates + segment geometry on the caller's pinned connection, decodes each candidate
/// track via <see cref="ITrackReader"/>, runs the pure core, and funnels the survivors through one
/// raw-SQL INSERT with the exact S7 column set/order (invariant 2).
///
/// Candidate selection: Activity_Bbox ⋈ own Activity, ±0.002° segment pad, inclusive overlap, with a
/// GPS-scan fallback for tracks lacking a bbox row. The core recomputes the 9-neighbour spatial set in
/// C# (GeoCellHash, TE-08). Never opens/closes the pinned connection (S0.4).
/// </summary>
public sealed class MatchEngineService : IMatchEngine
{
    private readonly RouteBaseDbContext _context;
    private readonly ITrackReader _trackReader;
    private readonly ILogger<MatchEngineService> _logger;

    public MatchEngineService(
        RouteBaseDbContext context,
        ITrackReader trackReader,
        ILogger<MatchEngineService> logger)
    {
        _context = context;
        _trackReader = trackReader;
        _logger = logger;
    }

    public async Task RunAsync(AnalysisScope scope, int matchThresholdPercent, CancellationToken ct = default)
    {
        // Pinned connection (already open by AnalyzeSegmentMatchesAsync). No Open/Close here (S0.4).
        var conn = (SqliteConnection)_context.Database.GetDbConnection();

        // (b) Segment geometry (S2/S3 input) — visibility already enforced by the caller.
        var seg = await LoadSegmentGeometryAsync(conn, scope.SegmentId, ct);
        if (seg is null) return;   // no Segment_GPS → SQL Stage 1 produces no candidates either.

        var segBbox = SegmentBbox(seg);
        double? segmentDistance = await LoadSegmentDistanceAsync(conn, scope.SegmentId, ct);

        // (a) Candidate own tracks whose bbox overlaps the padded segment bbox (S1).
        var candidateTrackIds = await LoadCandidateTracksAsync(conn, scope.UserId, segBbox, ct);

        // (c) Match + gate each candidate track.
        var passing = new List<QualifiedCrossing>();
        foreach (var trackId in candidateTrackIds.Select(i => new TrackId(i)))
        {
            ct.ThrowIfCancellationRequested();
            var track = await _trackReader.GetTrackAsync(trackId, ct);
            if (track is null) continue;   // deleted concurrently → SQL join yields no rows either.

            foreach (var qc in MatchEngineCore.Match(seg, track, trackId))
                if (MatchEngineCore.PassesGate(qc, segmentDistance, matchThresholdPercent))
                    passing.Add(qc);
        }

        // (d) INSERT (S7) — segment constant this run, so ORDER BY track_id, gps_id_entry (S7.3).
        passing.Sort(static (x, y) =>
            x.TrackId != y.TrackId ? x.TrackId.CompareTo(y.TrackId)
                                   : x.GpsIdEntry.CompareTo(y.GpsIdEntry));
        await InsertCrossingsAsync(conn, passing, ct);

        // (e) Diagnostics (parity NOT required — counts only).
        _logger.LogInformation(
            "MatchEngine segment {SegmentId}: {Candidates} candidate tracks → {Inserted} efforts",
            scope.SegmentId, candidateTrackIds.Count, passing.Count);
    }

    // Track-restricted match+insert for the incremental per-import path. A near-copy of RunAsync's inner
    // loop with the candidate list collapsed to one track — RunAsync itself is left byte-for-byte
    // unchanged (parity beats DRY; every I/O helper below is shared with it). MatchEngineCore is UNCHANGED.
    public async Task RunForTrackAsync(AnalysisScope scope, TrackId trackId, int matchThresholdPercent, CancellationToken ct = default)
    {
        // Pinned connection (already open by MatchNewTrackAsync). No Open/Close here (S0.4).
        var conn = (SqliteConnection)_context.Database.GetDbConnection();

        var seg = await LoadSegmentGeometryAsync(conn, scope.SegmentId, ct);
        if (seg is null) return;   // no Segment_GPS → no candidates (same as RunAsync).

        var segBbox = SegmentBbox(seg);
        double? segmentDistance = await LoadSegmentDistanceAsync(conn, scope.SegmentId, ct);

        var track = await _trackReader.GetTrackAsync(trackId, ct);
        if (track is null) return;   // deleted concurrently / no GPS → no rows.

        // Belt-and-suspenders overlap guard: same padded predicate as the candidate prefilter (cand-06),
        // so the two filters can never diverge. Costs one bbox scan; never drops a real candidate.
        if (!TryTrackBbox(track, out var tb) || !SegmentBboxGeometry.Overlaps(tb, segBbox)) return;

        var passing = new List<QualifiedCrossing>();
        foreach (var qc in MatchEngineCore.Match(seg, track, trackId))
            if (MatchEngineCore.PassesGate(qc, segmentDistance, matchThresholdPercent))
                passing.Add(qc);

        // Single track ⇒ track_id constant; order by gps_id_entry (RunAsync's secondary key, S7.3).
        passing.Sort(static (x, y) => x.GpsIdEntry.CompareTo(y.GpsIdEntry));
        await InsertCrossingsAsync(conn, passing, ct);

        _logger.LogInformation(
            "MatchEngine segment {SegmentId} track {TrackId}: {Inserted} efforts",
            scope.SegmentId, trackId, passing.Count);
    }

    // S2/S3 input: seg_GPS_ID ascending, coordinates. Null when the segment has no points.
    private static async Task<SegmentGeometry?> LoadSegmentGeometryAsync(
        SqliteConnection conn, SegmentId segmentId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT seg_GPS_ID, GPS_lat, GPS_long FROM Segment_GPS WHERE segment_ID = $s ORDER BY seg_GPS_ID";
        cmd.Parameters.Add(new SqliteParameter("$s", segmentId.Value));

        var ids = new List<int>();
        var lat = new List<double>();
        var lng = new List<double>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetInt32(0));
                lat.Add(reader.IsDBNull(1) ? double.NaN : reader.GetDouble(1));
                lng.Add(reader.IsDBNull(2) ? double.NaN : reader.GetDouble(2));
            }
        }

        if (ids.Count == 0) return null;
        return SegmentGeometry.Create(segmentId, ids.ToArray(), lat.ToArray(), lng.ToArray());
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

    // S1: own tracks whose activity bbox overlaps the padded segment bbox. Uses the Activity_Bbox
    // helper table; tracks lacking a bbox row fall back to a decode-based min/max (0 such live).
    private async Task<List<int>> LoadCandidateTracksAsync(
        SqliteConnection conn, string userId, Bbox seg, CancellationToken ct)
    {
        var candidates = new List<int>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT ab.track_ID, ab.min_lat, ab.max_lat, ab.min_long, ab.max_long
                FROM Activity_Bbox ab
                INNER JOIN Activity a ON a.track_ID = ab.track_ID AND a.user_id = $userId";
            cmd.Parameters.Add(new SqliteParameter("$userId", userId));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var ab = new Bbox(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4));
                if (SegmentBboxGeometry.Overlaps(ab, seg)) candidates.Add(reader.GetInt32(0));
            }
        }

        // Fallback (Stage 1 UNION ALL): own tracks with no bbox row → derive bbox from the decode.
        var fallbackTrackIds = new List<int>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT a.track_ID FROM Activity a
                WHERE a.user_id = $userId
                  AND NOT EXISTS (SELECT 1 FROM Activity_Bbox ab WHERE ab.track_ID = a.track_ID)";
            cmd.Parameters.Add(new SqliteParameter("$userId", userId));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                fallbackTrackIds.Add(reader.GetInt32(0));
        }

        foreach (var trackId in fallbackTrackIds.Select(i => new TrackId(i)))
        {
            ct.ThrowIfCancellationRequested();   // SD-05: GetTrackAsync is a no-op cancellation point on
                                                 // an LRU hit; a bbox-table wipe would otherwise make this
                                                 // fallback decode the whole corpus uncancellably.
            var track = await _trackReader.GetTrackAsync(trackId, ct);
            if (track is null) continue;                    // no GPS rows → no Stage 1 GROUP BY row.
            if (!TryTrackBbox(track, out var ab)) continue; // all-NULL coords → NULL bounds → excluded.
            if (SegmentBboxGeometry.Overlaps(ab, seg)) candidates.Add(trackId.Value);
        }

        return candidates;
    }

    // S1.2: segment bbox from Segment_GPS, padded via the shared SegmentBboxGeometry (±0.002° on lat AND
    // lng). NaN coords skipped, mirroring SQL MIN/MAX NULL-skip.
    private static Bbox SegmentBbox(SegmentGeometry seg)
    {
        double minLat = double.PositiveInfinity, maxLat = double.NegativeInfinity;
        double minLng = double.PositiveInfinity, maxLng = double.NegativeInfinity;
        for (int i = 0; i < seg.Lat.Length; i++)
        {
            double la = seg.Lat[i], lo = seg.Lng[i];
            if (double.IsNaN(la) || double.IsNaN(lo)) continue;
            if (la < minLat) minLat = la;
            if (la > maxLat) maxLat = la;
            if (lo < minLng) minLng = lo;
            if (lo > maxLng) maxLng = lo;
        }
        return SegmentBboxGeometry.PadSegment(new Bbox(minLat, maxLat, minLng, maxLng));
    }

    private static bool TryTrackBbox(DecodedTrack track, out Bbox bbox)
    {
        double minLat = double.PositiveInfinity, maxLat = double.NegativeInfinity;
        double minLng = double.PositiveInfinity, maxLng = double.NegativeInfinity;
        for (int i = 0; i < track.Count; i++)
        {
            double la = track.Lat[i], lo = track.Lng[i];
            if (double.IsNaN(la) || double.IsNaN(lo)) continue;
            if (la < minLat) minLat = la;
            if (la > maxLat) maxLat = la;
            if (lo < minLng) minLng = lo;
            if (lo > maxLng) maxLng = lo;
        }
        bbox = new Bbox(minLat, maxLat, minLng, maxLng);
        return maxLat >= minLat && maxLng >= minLng;   // false when no non-NaN point on an axis.
    }

    // S7.2: write exactly the 8 guessed columns; gps_id_count = exit − entry (a span). One
    // batched raw-SQL INSERT — no EF entities (S7.4), so the direction step's EF reads stay identical.
    private static async Task InsertCrossingsAsync(
        SqliteConnection conn, List<QualifiedCrossing> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;   // SQL inserts 0 rows on no survivors — same effect.

        const int chunk = 256;         // stay well under SQLite's parameter cap (8 params/row).
        for (int start = 0; start < rows.Count; start += chunk)
        {
            int end = Math.Min(start + chunk, rows.Count);
            var sb = new StringBuilder(
                "INSERT INTO GPSinSegment (track_id, segment_id, seg_gps_count, seg_total_count, " +
                "seg_perc, gps_id_entry_guessed, gps_id_exit_guessed, gps_id_count) VALUES ");

            await using var cmd = conn.CreateCommand();
            for (int i = start; i < end; i++)
            {
                var r = rows[i];
                if (i > start) sb.Append(',');
                sb.Append(CultureInfo.InvariantCulture, $"(@t{i},@s{i},@sg{i},@st{i},@sp{i},@ge{i},@gx{i},@gc{i})");
                cmd.Parameters.Add(new SqliteParameter($"@t{i}", r.TrackId));
                cmd.Parameters.Add(new SqliteParameter($"@s{i}", r.SegmentId));
                cmd.Parameters.Add(new SqliteParameter($"@sg{i}", r.MatchedSegPoints));
                cmd.Parameters.Add(new SqliteParameter($"@st{i}", r.TotalPoints));
                cmd.Parameters.Add(new SqliteParameter($"@sp{i}", r.MatchPerc));
                cmd.Parameters.Add(new SqliteParameter($"@ge{i}", r.GpsIdEntry));
                cmd.Parameters.Add(new SqliteParameter($"@gx{i}", r.GpsIdExit));
                cmd.Parameters.Add(new SqliteParameter($"@gc{i}", r.GpsIdExit - r.GpsIdEntry));
            }
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
