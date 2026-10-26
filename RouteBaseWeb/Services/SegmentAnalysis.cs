using System.Data;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Sql;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RouteBaseWeb.Services;

/// <summary>
/// Analysis orchestrator — coordinates a single segment's analyze run over the in-memory engine, plus
/// the analyze gate and the "Analyze All" clean-slate wipe. Cache eviction routes through the shared
/// <see cref="SegmentCache"/>. Post-insert direction validation and offset diagnostics are internal
/// helpers of this service.
///
/// Polyline flow:
///   1. Delete own existing matches on the segment
///   2. MatchEngine.RunAsync — stages 1–6 match + INSERT validated crossings (own activities)
///   3. Remove reverse-direction matches (C#)
///   4. EffortMath.CalculateOffsetsAsync — precise entry/exit offsets, scoped to own efforts
///   5. EffortMath.PopulateAggregatesAsync — per-effort aggregates (all owners)
///
/// Shape-defined segments have no Segment_GPS; they are matched by ShapeSegmentMatcher and only run
/// the aggregate step. "Analyze All" is this method looped over every own segment by the queue worker.
/// </summary>
public class SegmentAnalysis : ISegmentAnalysis
{
    private readonly RouteBaseDbContext _context;
    private readonly ILogger<SegmentAnalysis> _logger;
    private readonly IVisibilityService _visibility;
    private readonly SegmentCache _segCache;
    private readonly Segments.ISegmentMatchStrategy _shapeMatcher;

    // In-memory match engine + effort math (TrackEngine): the only analyze path since Cutover. Required
    // — analysis of any polyline segment is impossible without them.
    private readonly Matching.IMatchEngine _matchEngine;
    private readonly Matching.IEffortMath _effortMath;

    // Decoded-track reader for the direction-validation GPS load (TrackEngine TE-18). Required since
    // BlobStore — the row-store decode fallback is gone; production DI injects the cached singleton.
    private readonly ITrackReader _trackReader;

    // Owner of the heatmap-row stale transition (B2): re-shaping efforts flips attached done heatmaps stale.
    private readonly Heatmaps.IHeatmapLifecycle _heatmaps;

    // Owner of the effective-match-threshold ProfileSettings lookup (CR5-04 threshold seam).
    private readonly IProfileSettingsService _settings;

    // Match-threshold constants — the INSERT gate default + API validation bounds. Consumed by the API
    // controller, worker, parity harness, ProfileSettings pages and ISegmentAnalysis's default parameter.
    public const int DefaultMatchThresholdPercent = 40;
    public const int MinMatchThresholdPercent     = 0;
    public const int MaxMatchThresholdPercent     = 100;

    public SegmentAnalysis(
        RouteBaseDbContext context,
        ILogger<SegmentAnalysis> logger,
        IVisibilityService visibility,
        SegmentCache segCache,
        Segments.ISegmentMatchStrategy shapeMatcher,
        Matching.IMatchEngine matchEngine,
        Matching.IEffortMath effortMath,
        ITrackReader trackReader,
        Heatmaps.IHeatmapLifecycle heatmaps,
        IProfileSettingsService settings)
    {
        _context      = context;
        _logger       = logger;
        _visibility   = visibility;
        _segCache     = segCache;
        _shapeMatcher = shapeMatcher;
        _matchEngine  = matchEngine;
        _effortMath   = effortMath;
        _trackReader  = trackReader;
        _heatmaps     = heatmaps;
        _settings     = settings;
    }

    /// <inheritdoc/>
    public async Task<int> GetEffectiveMatchThresholdAsync(string userId, int? explicitPercent)
    {
        if (explicitPercent is { } t)
            return Math.Clamp(t, MinMatchThresholdPercent, MaxMatchThresholdPercent);

        var value = await _settings.GetSettingAsync<int>(
            SettingCategories.Segments, SettingKeys.DefaultMatchThreshold, userId,
            defaultValue: DefaultMatchThresholdPercent);
        return Math.Clamp(value, MinMatchThresholdPercent, MaxMatchThresholdPercent);
    }

    public async Task<bool> CanAnalyzeSegmentAsync(SegmentId segmentId)
    {
        var seg = await _visibility.VisibleSegments(_context.Segments)
            .Where(s => s.SegmentId == segmentId)
            .Select(s => new { s.SegmentKind, s.UserId })
            .FirstOrDefaultAsync();
        if (seg == null) return false;
        // Shape re-analysis deletes ALL users' efforts and rebuilds from the invoker's perspective,
        // so only the owner may trigger it. Polyline analysis is self-scoping → any viewer may run it.
        return seg.SegmentKind != SegmentKinds.Shapes || seg.UserId == _visibility.GetCurrentUserId();
    }

    /// <summary>
    /// Wipes ALL of the user's own efforts across ALL of their own segments in one statement.
    /// Used by the "Analyze All" worker as a clean-slate step before it loops
    /// <see cref="AnalyzeSegmentMatchesAsync"/> over each own segment — so own segments that end up
    /// with no matches (or whose per-segment analysis is skipped) don't keep stale efforts.
    /// It is largely redundant with the per-segment own-effort delete inside
    /// <see cref="AnalyzeSegmentMatchesAsync"/>, which is why a single Re-analyze (no clean-slate)
    /// correctly resets only its own segment. Never touches other users' efforts.
    /// </summary>
    public async Task ClearAllSegmentMatchesAsync(string? userId = null, CancellationToken ct = default)
    {
        userId ??= _visibility.GetCurrentUserId();
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM GPSinSegment WHERE segment_id IN (SELECT segment_ID FROM Segment WHERE user_id = {0})",
            new object[] { userId }, ct);
    }

    /// <summary>
    /// SB-02 safety net: after a clean "Analyze All" pass, delete own-segment efforts for segments the
    /// loop did NOT process (e.g. a segment deleted between the segment-list fetch and its turn) — the
    /// narrow case the old upfront wipe targeted, without the wipe-then-partial-rebuild window that let a
    /// cancel/crash zero every not-yet-reached segment.
    /// </summary>
    public async Task ClearOrphanedSegmentMatchesAsync(string userId, IReadOnlyList<SegmentId> processedSegmentIds, CancellationToken ct = default)
    {
        // Positional placeholders so ids are parameterised, never interpolated ({0} = userId).
        var args = new List<object> { userId };
        string notIn = "";
        if (processedSegmentIds.Count > 0)
        {
            var placeholders = processedSegmentIds.Select(id => { var p = "{" + args.Count + "}"; args.Add(id.Value); return p; });
            notIn = $" AND segment_id NOT IN ({string.Join(",", placeholders)})";
        }
        // EF1002: `notIn` interpolates only synthesized {1}/{2}/... placeholder tokens (built from
        // args.Count above) — every actual value, including each processed-segment id, travels through
        // the params array below.
#pragma warning disable EF1002
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM GPSinSegment WHERE segment_id IN (SELECT segment_ID FROM Segment WHERE user_id = {{0}}){notIn}",
            args.ToArray(), ct);
#pragma warning restore EF1002
    }

    /// <summary>
    /// Analyze and store GPS matches for one segment.
    ///
    /// <paramref name="userId"/> is accepted explicitly so the background worker can pass it
    /// without relying on HttpContext. When null, falls back to the current HTTP user.
    /// </summary>
    public async Task AnalyzeSegmentMatchesAsync(SegmentId segmentId, string? userId = null, int matchThresholdPercent = DefaultMatchThresholdPercent, CancellationToken ct = default)
    {
        userId ??= _visibility.GetCurrentUserId();

        // Friend IDs let own activities match friends' visible segments, and let shape matching span
        // own + public + friends' activities.
        var friendIds = (await _visibility.GetVisibleUserIdsAsync())
            .Where(id => id != userId)
            .ToList();

        var scope = new AnalysisScope(segmentId, userId, friendIds);

        _segCache.InvalidateSegment(userId, segmentId);

        // Shape-defined segments are matched by ShapeSegmentMatcher and have no Segment_GPS, so they
        // skip the polyline pipeline entirely.
        // Defense in depth (SG-01): visibility-scope this read so the destructive shape rebuild below
        // is unreachable for a non-owner from any entry path, independent of the controller gate.
        var seg = await _visibility.VisibleSegments(_context.Segments)
            .Where(s => s.SegmentId == segmentId)
            .Select(s => new { s.SegmentKind, s.UserId })
            .FirstOrDefaultAsync(ct);
        if (seg == null)
        {
            _logger.LogWarning("Skipping analysis for segment {SegmentId}: not visible to user {UserId}", segmentId, userId);
            return;
        }
        bool isShape = seg.SegmentKind == SegmentKinds.Shapes;

        // Pin one physical SQLite connection for the whole run: the match + effort-math engine adapters
        // (and the shape matcher) issue raw ADO on this pinned handle across awaits (S0.4 / rule 4).
        // With pooling, EF's per-command open/close could otherwise swap the handle mid-run.
        // Constraint: EF ref-counts Open/Close — no helper called inside this block may issue its own
        // OpenConnection/CloseConnection pair, or the pin is released early.
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            // --- Delete only OWN efforts (preserve other users' records) ---
            _logger.LogInformation("Analyzing segment {SegmentId}", segmentId);

            var delBatch = new SqlBatch(@"DELETE FROM GPSinSegment
/*{filter}*/");
            delBatch.Token("filter", scope.DeleteFilter(delBatch));
            await delBatch.ExecuteAsync(_context.Database, ct);

            // --- Shape segment: matched entirely by the shape strategy; skip the polyline pipeline ---
            if (isShape)
            {
                // ShapeSegmentMatcher.MatchAsync deletes ALL users' efforts on the segment and rebuilds
                // from this user's perspective — only the owner may do that (SG-01 defense in depth).
                if (seg.UserId != userId)
                {
                    _logger.LogWarning("Skipping shape re-analysis for segment {SegmentId}: user {UserId} is not the owner", segmentId, userId);
                    return;
                }

                await _shapeMatcher.MatchAsync(new List<SegmentId> { segmentId }, userId, friendIds, ct);
                await _effortMath.PopulateAggregatesAsync(scope, ct: ct);

                // Spans just changed → any attached heatmap is now stale.
                await _heatmaps.MarkStaleAsync(segmentId, ct);

                // SE-02: the rebuild replaced this segment's efforts (and, under SQLite rowid reuse,
                // their ids), so per-effort perf-cache entries keyed by the old ids are now stale.
                // InvalidateSegment can't target that key family; trip the owner token (invoker IS the
                // owner) to evict every viewer's per-effort entries. Runs AFTER the rebuild so no
                // in-flight read can re-cache the doomed data (unlike the pre-work InvalidateSegment).
                _segCache.InvalidateUser(userId);

                _logger.LogInformation("Completed shape analysis for segment {SegmentId}", segmentId);
                return;
            }

            // --- Stage 1–6 match + INSERT: in-memory match engine ---
            await _matchEngine.RunAsync(scope, matchThresholdPercent, ct);

            // --- Post-insert: direction validation ---
            await ValidateSegmentDirectionAsync(segmentId, ct: ct);

            // --- Post-validation: precise offset calculation (scoped to own efforts) ---
            await _effortMath.CalculateOffsetsAsync(scope, ct: ct);
            await LogOffsetResultsAsync(segmentId, ct);

            // Precompute per-effort aggregates over the now-final windows.
            await _effortMath.PopulateAggregatesAsync(scope, ct: ct);

            // SD-01: the polyline re-analysis just re-shaped this segment's effort spans, so any
            // attached heatmap (painted per GPSinSegment span) now serves tiles built from stale
            // spans. The shape branch already does this; the polyline branch previously did not.
            await _heatmaps.MarkStaleAsync(segmentId, ct);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }

        // SE-02: evict stale per-effort perf-cache entries after the polyline rebuild (see shape branch).
        _segCache.InvalidateUser(userId);

        _logger.LogInformation("Completed analysis for segment {SegmentId}", segmentId);
    }

    /// <summary>
    /// Incremental per-import match: the track-outer / segment-inner mirror of
    /// <see cref="AnalyzeSegmentMatchesAsync"/>. Matches one newly-imported <paramref name="trackId"/>
    /// against every own polyline segment whose padded bbox overlaps the track, touching ONLY that
    /// track's rows. Reuses the frozen match/effort cores via the track-restricted adapters.
    ///
    /// Equivalence (cand-01): because one of (track, segment) is always created last, and creating-last
    /// matches that entity against all pre-existing counterparts, the incremental sequence produces a
    /// GPSinSegment set identical to a full analyze-all (modulo PK + row order). Per-effort results are
    /// pure functions of the immutable track blob + segment geometry, so computing only the new track's
    /// rows leaves every pre-existing row correct.
    ///
    /// Seam-2 invariant: the caller must have upserted Activity_Bbox for <paramref name="trackId"/>
    /// before invoking this (candidate selection reads Activity_Bbox).
    /// </summary>
    public async Task MatchNewTrackAsync(TrackId trackId, string? userId = null,
        int matchThresholdPercent = DefaultMatchThresholdPercent, CancellationToken ct = default)
    {
        userId ??= _visibility.GetCurrentUserId();
        var friendIds = await LoadFriendIdsAsync(userId);
        var segments = await LoadOwnSegmentCandidatesAsync(userId, ct);
        if (segments.IsEmpty) return;

        // Single-track entry point (equivalence oracle + direct callers): exceptions propagate.
        await MatchOneTrackAllKindsAsync(trackId, userId, friendIds, matchThresholdPercent, segments, ct);
    }

    /// <summary>
    /// Batch incremental match for a whole import drain (inval-06). Aggregates every candidate segment's
    /// bbox ONCE for the batch (was once per track), then matches each track against it. Per-track
    /// try/catch so one bad track can't fail the import or starve its siblings; cancellation honoured
    /// between tracks. Threshold parity with analyze-all is the caller's responsibility.
    /// </summary>
    public async Task MatchNewTracksAsync(IReadOnlyList<int> trackIds, string? userId = null,
        int matchThresholdPercent = DefaultMatchThresholdPercent, CancellationToken ct = default)
    {
        if (trackIds.Count == 0) return;
        userId ??= _visibility.GetCurrentUserId();
        var friendIds = await LoadFriendIdsAsync(userId);

        var segments = await LoadOwnSegmentCandidatesAsync(userId, ct);
        if (segments.IsEmpty) return;

        foreach (var trackId in trackIds.Select(i => new TrackId(i)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await MatchOneTrackAllKindsAsync(trackId, userId, friendIds, matchThresholdPercent, segments, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Incremental match failed for imported track {TrackId}", trackId);
            }
        }
    }

    /// <summary>
    /// The user's own segments as the two candidate sets an incremental match needs. The sets PARTITION
    /// those segments — <see cref="LoadSegmentBboxesAsync"/> excludes <c>shapes</c> and
    /// <see cref="LoadOwnShapeSegmentIdsAsync"/> selects exactly them — so no segment can be matched twice
    /// or missed. Loaded once and reused across every track in a batch (inval-06).
    /// </summary>
    private sealed record OwnSegmentCandidates(
        IReadOnlyList<(SegmentId segmentId, Bbox raw)> PolylineBboxes, IReadOnlyList<SegmentId> ShapeIds)
    {
        public bool IsEmpty => PolylineBboxes.Count == 0 && ShapeIds.Count == 0;
    }

    private async Task<OwnSegmentCandidates> LoadOwnSegmentCandidatesAsync(string userId, CancellationToken ct) =>
        new(await LoadSegmentBboxesAsync(userId, ct), await LoadOwnShapeSegmentIdsAsync(userId, ct));

    /// <summary>
    /// One track against BOTH segment kinds — the whole incremental match for that track, and the only
    /// place that knows there are two kinds. The halves are separate pipelines rather than a flag on one:
    /// a polyline segment picks candidates by padded-bbox overlap and runs the match/effort engines, a
    /// shape segment picks them by its rule document. Both entry points call this so neither can grow a
    /// kind the other forgets.
    /// </summary>
    private async Task MatchOneTrackAllKindsAsync(
        TrackId trackId, string userId, List<string> friendIds, int matchThresholdPercent,
        OwnSegmentCandidates segments, CancellationToken ct)
    {
        if (segments.PolylineBboxes.Count > 0)
            await MatchOneTrackAsync(trackId, userId, friendIds, matchThresholdPercent, segments.PolylineBboxes, ct);
        await MatchOneTrackShapesAsync(trackId, userId, friendIds, segments.ShapeIds, ct);
    }

    /// <summary>
    /// Shape-segment half of the incremental match, the sibling of <see cref="MatchOneTrackAsync"/>.
    ///
    /// Shape segments carry a rule document instead of a polyline, so the polyline candidate predicate
    /// (which reads Segment_GPS) can never select them — before this existed they were reachable only by a
    /// full "Analyze All", and an imported or newly-approved ride silently never joined them.
    ///
    /// Runs OUTSIDE <see cref="MatchOneTrackAsync"/>'s pinned-connection window on purpose: the shape
    /// matcher works through EF (including its own transaction), and the pin's constraint (S0.4) is that
    /// nothing inside it may open/close its own connection.
    /// </summary>
    private async Task MatchOneTrackShapesAsync(
        TrackId trackId, string userId, List<string> friendIds, IReadOnlyList<SegmentId> shapeSegmentIds,
        CancellationToken ct)
    {
        if (shapeSegmentIds.Count == 0) return;

        // SD-01: only the segments this track actually joined (or was removed from) get follow-up work —
        // the same per-segment gate MatchOneTrackAsync applies via `rowsChanged`. Gating on "did ANY
        // segment change" instead would mark every shape segment the user owns stale for one match.
        var changed = await _shapeMatcher.MatchTrackAsync(trackId, shapeSegmentIds, userId, friendIds, ct);
        if (changed.Count == 0) return;

        // Aggregates over the freshly-written spans, restricted to this track — the same subset the full
        // shape branch runs (offsets are polyline-only).
        //
        // The pin is REQUIRED, not tidiness: PopulateAggregatesAsync issues raw ADO on
        // GetDbConnection() and assumes the caller opened it, exactly as AnalyzeSegmentMatchesAsync and
        // MatchOneTrackAsync both do. Without it the aggregates throw "ExecuteReader can only be called
        // when the connection is open" — and this path swallows that into a log line, leaving shape
        // efforts inserted but permanently NULL-statted.
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            foreach (SegmentId segmentId in changed)
            {
                _segCache.InvalidateSegment(userId, segmentId);
                var scope = new AnalysisScope(segmentId, userId, friendIds);
                await _effortMath.PopulateAggregatesAsync(scope, trackId.Value, ct);
                await _heatmaps.MarkStaleAsync(segmentId, ct);
            }
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }

        // SE-02: per-effort perf-cache keys InvalidateSegment can't target. AFTER the rebuild, not before
        // it — a pre-work invalidate lets an in-flight read re-cache the rows we are replacing.
        _segCache.InvalidateUser(userId);
    }

    // Own shape segments — the rule-document twin of LoadSegmentBboxesAsync. No bbox prefilter here: the
    // shape matcher runs its own Stage-1 (CouldMatch over the track's Activity_Bbox) per segment.
    private async Task<List<SegmentId>> LoadOwnShapeSegmentIdsAsync(string userId, CancellationToken ct) =>
        await _context.Segments.AsNoTracking()
            .Where(s => s.UserId == userId && s.SegmentKind == SegmentKinds.Shapes)
            .Select(s => s.SegmentId)
            .ToListAsync(ct);

    // FriendIds for AnalysisScope construction parity; the polyline path never reads them (cand-08).
    private async Task<List<string>> LoadFriendIdsAsync(string userId) =>
        (await _visibility.GetVisibleUserIdsAsync()).Where(id => id != userId).ToList();

    /// <summary>
    /// Incremental per-import match for one track: the track-outer / segment-inner mirror of
    /// <see cref="AnalyzeSegmentMatchesAsync"/>. Matches <paramref name="trackId"/> against the pre-aggregated
    /// candidate segments whose padded bbox overlaps the track, touching ONLY that track's rows via the
    /// frozen match/effort cores' track-restricted adapters. One pinned SQLite connection for the whole
    /// track (S0.4): the track-bbox read and every adapter issue raw ADO on it. Seam-2 invariant: the caller
    /// must have upserted Activity_Bbox for the track before this (candidate selection reads it, with a
    /// decode fallback).
    ///
    /// Equivalence (cand-01): because one of (track, segment) is always created last, and creating-last
    /// matches that entity against all pre-existing counterparts, the incremental sequence produces a
    /// GPSinSegment set identical to a full analyze-all (modulo PK + row order).
    /// </summary>
    private async Task MatchOneTrackAsync(TrackId trackId, string userId, List<string> friendIds,
        int matchThresholdPercent, IReadOnlyList<(SegmentId segmentId, Bbox raw)> segmentBboxes, CancellationToken ct)
    {
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = (SqliteConnection)_context.Database.GetDbConnection();
            var trackBbox = await LoadTrackBboxAsync(conn, trackId, ct);
            if (trackBbox is null) return;   // no bbox row and no decodable GPS → no candidates.

            var candidateSegmentIds = SelectCandidateSegments(trackBbox.Value, segmentBboxes);
            if (candidateSegmentIds.Count == 0) return;

            foreach (SegmentId segmentId in candidateSegmentIds)
            {
                ct.ThrowIfCancellationRequested();
                _segCache.InvalidateSegment(userId, segmentId);

                var scope = new AnalysisScope(segmentId, userId, friendIds);

                // Idempotent delete (cand-07): only THIS (track, segment) pair. NEVER scope.DeleteFilter —
                // that wipes all the user's tracks on the segment. Fresh trackId ⇒ 0 rows deleted.
                int deleted = await DeleteTrackSegmentRowsAsync(trackId, segmentId, ct);

                await _matchEngine.RunForTrackAsync(scope, trackId, matchThresholdPercent, ct);
                await ValidateSegmentDirectionAsync(segmentId, trackId.Value, ct);
                await _effortMath.CalculateOffsetsAsync(scope, trackId.Value, ct);
                await _effortMath.PopulateAggregatesAsync(scope, trackId.Value, ct);

                // SD-01: only flip attached heatmaps stale when this import actually changed the
                // segment's efforts — rows deleted (moved/removed) OR rows now present (inserted).
                // A non-matching import touches nothing, so its heatmaps stay `done` (no badge thrash).
                bool rowsChanged = deleted > 0
                    || await _context.GpsInSegments.AnyAsync(g => g.SegmentId == segmentId && g.TrackId == trackId, ct);
                if (rowsChanged)
                    await _heatmaps.MarkStaleAsync(segmentId, ct);
            }

            // SE-02: one owner-token trip after the per-track rebuild evicts every viewer's per-effort
            // perf-cache entries for the candidate segments (InvalidateSegment above can't target that
            // key family). Invoker is the token owner for own-segment entries.
            _segCache.InvalidateUser(userId);

            _logger.LogInformation(
                "Completed incremental match for track {TrackId} over {Count} candidate segment(s)",
                trackId, candidateSegmentIds.Count);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    // cand-07 idempotency: delete exactly this (track, segment) pair's rows. Runs on the pinned
    // connection. Returns the affected-row count (SD-01 uses it to gate the heatmap-stale flip).
    private async Task<int> DeleteTrackSegmentRowsAsync(TrackId trackId, SegmentId segmentId, CancellationToken ct)
    {
        return await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM GPSinSegment WHERE segment_id = {0} AND track_id = {1}",
            new object[] { segmentId.Value, trackId.Value }, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SegmentId>> RemoveTrackFromSegmentsAsync(
        TrackId trackId, string? userId = null, CancellationToken ct = default)
    {
        userId ??= _visibility.GetCurrentUserId();

        // Capture the affected segments BEFORE the delete (the stale-flip needs them once the rows are gone).
        var affected = await _context.GpsInSegments
            .Where(g => g.TrackId == trackId).Select(g => g.SegmentId).Distinct().ToListAsync(ct);

        // Whole-track effort wipe (NOT the exclusion rows — pair-level user intent survives an edit, §5.4).
        await _context.Database.ExecuteSqlRawAsync(ChildTableRegistry.EffortsByTrack.DeleteByKeySql, [trackId.Value], ct);

        foreach (SegmentId segmentId in affected)
        {
            _segCache.InvalidateSegment(userId, segmentId);
            await _heatmaps.MarkStaleAsync(segmentId, ct); // attached done-heatmaps painted the now-gone spans
        }
        _segCache.InvalidateUser(userId); // per-effort perf-cache keys InvalidateSegment can't target (SE-02)
        return affected;
    }

    // -------------------------------------------------------------------------
    // Candidate-segment selection (cand-06). Aggregation and overlap are split: raw segment bboxes are read
    // ONCE per batch (SQL, no padding), then the shared SegmentBboxGeometry predicate — the same pad +
    // overlap MatchEngineService uses — selects per track in C#. One source of the geometry, no drift.
    // -------------------------------------------------------------------------

    // Raw (unpadded) bbox of every own polyline segment with GPS. segment_kind NULL or non-'shapes' =
    // polyline; segments with no Segment_GPS or all-NULL coords are excluded (mirrors SQL MIN/MAX skip).
    private async Task<List<(SegmentId segmentId, Bbox raw)>> LoadSegmentBboxesAsync(string userId, CancellationToken ct)
    {
        var conn = (SqliteConnection)_context.Database.GetDbConnection();
        bool opened = conn.State != ConnectionState.Open;
        if (opened) await _context.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT s.segment_ID, MIN(g.GPS_lat), MAX(g.GPS_lat), MIN(g.GPS_long), MAX(g.GPS_long)
                FROM Segment s
                JOIN Segment_GPS g ON g.segment_ID = s.segment_ID
                WHERE s.user_id = $u
                  AND (s.segment_kind IS NULL OR s.segment_kind <> 'shapes')
                GROUP BY s.segment_ID";
            cmd.Parameters.Add(new SqliteParameter("$u", userId));

            var result = new List<(SegmentId, Bbox)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4))
                    continue;   // all-NULL coords on an axis → NULL bounds → excluded (mirrors SQL).
                var raw = new Bbox(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4));
                result.Add((new SegmentId(reader.GetInt32(0)), raw));
            }
            return result;
        }
        finally
        {
            if (opened) await _context.Database.CloseConnectionAsync();
        }
    }

    // Track bbox (padded segment) overlap in C#, via the shared geometry — identical selection to the old
    // padded-SQL predicate and to MatchEngineService's candidate filter.
    private static List<SegmentId> SelectCandidateSegments(
        Bbox trackBbox, IReadOnlyList<(SegmentId segmentId, Bbox raw)> segmentBboxes)
    {
        var candidates = new List<SegmentId>();
        foreach (var (segmentId, raw) in segmentBboxes)
            if (SegmentBboxGeometry.Overlaps(trackBbox, SegmentBboxGeometry.PadSegment(raw)))
                candidates.Add(segmentId);
        return candidates;
    }

    // Track bbox: Activity_Bbox row if present, else decode-derived min/max (mirror of MatchEngineService's
    // TryTrackBbox fallback). Null when neither a bbox row nor any non-NaN GPS point exists.
    private async Task<Bbox?> LoadTrackBboxAsync(SqliteConnection conn, TrackId trackId, CancellationToken ct)
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT min_lat, max_lat, min_long, max_long FROM Activity_Bbox WHERE track_ID = $t";
            cmd.Parameters.Add(new SqliteParameter("$t", trackId.Value));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return new Bbox(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
        }

        // Fallback: derive from the decode (all-non-NaN min/max), mirroring MatchEngineService.TryTrackBbox.
        var track = await _trackReader.GetTrackAsync(trackId, ct);
        if (track is null) return null;

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
        if (maxLat < minLat || maxLng < minLng) return null;   // no non-NaN point on an axis.
        return new Bbox(minLat, maxLat, minLng, maxLng);
    }

    // -------------------------------------------------------------------------
    // Post-pipeline direction validation
    //
    // After crossings are inserted into GPSinSegment, load the actual GPS coordinates and check whether
    // the activity traversed the segment in the correct direction (start → end). Reverse-direction
    // matches are removed. Algorithm: map each activity GPS point to the nearest segment point by
    // rounded coordinate lookup, collect the segment-index sequence, then count forward vs backward
    // transitions. If backward > forward the activity ran the segment in reverse.
    // -------------------------------------------------------------------------

    // onlyTrackId (incremental per-import path) restricts re-validation to the new track's matches; each
    // match's reverse/keep decision is independent (per-match decode + segment geometry only), so the
    // outcome for its rows is identical and other tracks' rows are untouched. null = whole segment.
    private async Task ValidateSegmentDirectionAsync(SegmentId segmentId, int? onlyTrackId = null, CancellationToken ct = default)
    {
        // Read-only loads (no mutation flows through these entities) → AsNoTracking, so a large import
        // batch on one shared context doesn't accumulate tracked rows / slow every SaveChanges.
        var segmentPoints = await _context.SegmentGps
            .AsNoTracking()
            .Where(sg => sg.SegmentId == segmentId)
            .OrderBy(sg => sg.SegGpsId)
            .ToListAsync(ct);

        if (segmentPoints.Count < 2) return;

        var matchesQuery = _context.GpsInSegments.AsNoTracking().Where(g => g.SegmentId == segmentId);
        if (onlyTrackId is { } t) matchesQuery = matchesQuery.Where(g => g.TrackId == new TrackId(t));
        var matches = await matchesQuery.ToListAsync(ct);

        if (matches.Count == 0) return;

        var decodedTracks = await LoadDecodedTracksForMatchesAsync(matches, ct);

        var segIndexByCoord = BuildSegmentIndexLookup(segmentPoints);
        var toRemove = FindReverseDirectionMatchIds(matches, segIndexByCoord, decodedTracks);

        if (toRemove.Count > 0)
        {
            await RemoveMatchesAsync(toRemove, ct);
            _logger.LogInformation(
                "Removed {Count} reverse-direction matches for segment {SegmentId}",
                toRemove.Count, segmentId);
        }
    }

    /// <summary>
    /// Decodes each distinct track referenced by a match with both guessed IDs, keyed by TrackId.
    /// Tracks that decode to null (no loadable GPS) are absent — the caller removes those matches
    /// (S10.2a). Replaces the former global-range EF GPS load: the per-match [entry..exit] slices are
    /// identical to the old superset read (S10.2), so behaviour is unchanged in both engine and SQL mode.
    /// </summary>
    private async Task<Dictionary<int, DecodedTrack>> LoadDecodedTracksForMatchesAsync(
        List<GpsInSegment> matches, CancellationToken ct = default)
    {
        var trackIds = matches
            .Where(m => m.GpsIdEntryGuessed.HasValue && m.GpsIdExitGuessed.HasValue)
            .Select(m => m.TrackId)
            .Distinct();

        var decoded = new Dictionary<int, DecodedTrack>();
        foreach (var trackId in trackIds.Select(i => new TrackId(i.Value)))
        {
            var track = await DecodeTrackAsync(trackId, ct);
            if (track is not null) decoded[trackId.Value] = track;   // decode null → no entry (S10.2a).
        }
        return decoded;
    }

    /// <summary>Decodes one track via the injected reader (LRU-cached, shared with the match stage).</summary>
    private Task<DecodedTrack?> DecodeTrackAsync(TrackId trackId, CancellationToken ct) =>
        _trackReader.GetTrackAsync(trackId, ct);

    /// <summary>
    /// Builds a rounded-coordinate → segment-index lookup from segment GPS points.
    /// </summary>
    private static Dictionary<(double lat, double lng), int> BuildSegmentIndexLookup(List<SegmentGps> segmentPoints)
    {
        var lookup = new Dictionary<(double lat, double lng), int>(segmentPoints.Count);
        for (int i = 0; i < segmentPoints.Count; i++)
        {
            var sp = segmentPoints[i];
            if (sp.GpsLat == null || sp.GpsLong == null) continue;
            var key = (Math.Round(sp.GpsLat.Value, 4), Math.Round(sp.GpsLong.Value, 4));
            lookup.TryAdd(key, i);
        }
        return lookup;
    }

    /// <summary>
    /// Checks each match against the segment direction and returns match IDs that
    /// are traversed in reverse (backward transitions > forward transitions).
    /// </summary>
    private static List<GpsInSegmentId> FindReverseDirectionMatchIds(
        List<GpsInSegment> matches,
        Dictionary<(double lat, double lng), int> segIndexByCoord,
        Dictionary<int, DecodedTrack> decodedTracks)
    {
        var reverseIds = new List<GpsInSegmentId>();

        foreach (var match in matches)
        {
            if (!match.GpsIdEntryGuessed.HasValue || !match.GpsIdExitGuessed.HasValue)
                continue;

            int entry = Math.Min(match.GpsIdEntryGuessed.Value, match.GpsIdExitGuessed.Value);
            int exit  = Math.Max(match.GpsIdEntryGuessed.Value, match.GpsIdExitGuessed.Value);

            if (!decodedTracks.TryGetValue(match.TrackId.Value, out var track))
            {
                reverseIds.Add(match.GpsInSegmentId);   // S10.2a: no loadable GPS → remove
                continue;
            }

            // Window [entry..exit] in GPS_ID-ascending ordinal order (≡ the old ORDER BY GPS_ID slice).
            var (lo, hi) = track.OrdinalRange(entry, exit);

            // Map in-window points to segment indices, skipping NaN coords (≡ the old lat/lng != null
            // filter). validPts counts the non-NaN points the SQL query would have returned (S10.2b).
            var indices = new List<int>(Math.Max(0, hi - lo + 1));
            int validPts = 0;
            for (int i = lo; i <= hi; i++)
            {
                double lat = track.Lat[i], lng = track.Lng[i];
                if (double.IsNaN(lat) || double.IsNaN(lng)) continue;
                validPts++;
                var key = (Math.Round(lat, 4), Math.Round(lng, 4));
                if (segIndexByCoord.TryGetValue(key, out int segIdx))
                    indices.Add(segIdx);
            }

            if (validPts < 2) { reverseIds.Add(match.GpsInSegmentId); continue; }   // S10.2b
            if (indices.Count < 2) continue;                                        // mapped < 2 → keep

            // Count forward vs backward transitions
            int up = 0, down = 0;
            for (int i = 1; i < indices.Count; i++)
            {
                if (indices[i] > indices[i - 1]) up++;
                else if (indices[i] < indices[i - 1]) down++;
            }

            if (down > up) reverseIds.Add(match.GpsInSegmentId);   // S10.2c
        }

        return reverseIds;
    }

    /// <summary>
    /// Removes GpsInSegment rows by their IDs.
    /// </summary>
    private async Task RemoveMatchesAsync(List<GpsInSegmentId> matchIds, CancellationToken ct = default)
    {
        // Set-based delete: one DELETE ... WHERE id IN (...), no load + no change tracking (the rows are
        // never needed as entities). Runs on the pinned connection like the other raw-SQL steps.
        await _context.GpsInSegments
            .Where(g => matchIds.Contains(g.GpsInSegmentId))
            .ExecuteDeleteAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Diagnostics — final GPSinSegment offset-result logging. Purely observational; never affects data.
    // -------------------------------------------------------------------------

    private async Task LogOffsetResultsAsync(SegmentId segmentId, CancellationToken ct = default)
    {
        // SB-04: purely diagnostic. Guard the whole method (incl. its GPSinSegment re-read) behind the
        // Debug level so it's skipped at the app's default Information floor — on a popular segment or an
        // Analyze-All loop this otherwise adds a SELECT + O(rows) log writes per segment for no value.
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        var rows = await _context.GpsInSegments.AsNoTracking()
            .Where(r => r.SegmentId == segmentId)
            .ToListAsync(ct);
        _logger.LogDebug("[Offsets] Final GPSinSegment state — {Count} rows", rows.Count);
        foreach (var r in rows)
        {
            int spanG = (r.GpsIdExitGuessed    ?? 0) - (r.GpsIdEntryGuessed    ?? 0);
            int spanC = (r.GpsIdExitCalculated ?? 0) - (r.GpsIdEntryCalculated ?? 0);
            _logger.LogDebug(
                "[Offsets] seg={SegId} track={TrackId} " +
                "guessed=[{EntryG},{ExitG}](span={SpanG}) " +
                "calculated=[{EntryC},{ExitC}](span={SpanC}) " +
                "offsets=[{OE:F5},{OX:F5}]",
                r.SegmentId, r.TrackId,
                r.GpsIdEntryGuessed, r.GpsIdExitGuessed, spanG,
                r.GpsIdEntryCalculated, r.GpsIdExitCalculated, spanC,
                r.OffsetEntry ?? -1, r.OffsetExit ?? -1);
        }
    }
}
