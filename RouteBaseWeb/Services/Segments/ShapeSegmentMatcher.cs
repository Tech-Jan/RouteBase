using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Segments.Rules;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// Matches activities against rule-document segments and writes the resulting efforts
/// to <c>GPSinSegment</c>, so the existing statistics / replay / charts page works unchanged.
///
/// Two-stage, mirroring the polyline pipeline's performance shape:
///   Stage 1 (cheap)   — keep only activities whose Activity_Bbox could satisfy the rules
///                       (<see cref="SegmentRuleEvaluator.CouldMatch"/>, never prunes a real match).
///   Stage 2 (precise) — scan those activities' GPS points, extract <see cref="TrackEvidence"/>,
///                       and let the evaluator produce the surviving effort spans.
/// </summary>
public sealed class ShapeSegmentMatcher : ISegmentMatchStrategy
{
    private const int CandidateGpsChunk = 400;   // candidate batch size (preserves prior read order)

    // SB-01: preview-exact runs synchronously on the request thread OUTSIDE the OperationQueue's
    // admission control, and its candidate pool spans the whole site's public-track corpus. Cap the
    // number of tracks it will decode so one broad-shape request can't force full-resolution decode of
    // a large fraction of the corpus. The save path (MatchAsync) stays uncapped — it runs on the queue.
    internal const int MaxExactPreviewCandidates = 2000;

    private readonly RouteBaseDbContext _context;
    private readonly ILogger<ShapeSegmentMatcher> _logger;
    private readonly ITrackReader _trackReader;

    // ITrackReader is required (BlobStore: the row-store decode fallback is gone). Production DI injects
    // the shared cached singleton; GPS point data lives in the blob and never travels through EF entities.
    public ShapeSegmentMatcher(
        RouteBaseDbContext context, ILogger<ShapeSegmentMatcher> logger, ITrackReader trackReader)
    {
        _context = context;
        _logger = logger;
        _trackReader = trackReader;
    }

    private Task<DecodedTrack?> ReadTrackAsync(TrackId trackId, CancellationToken ct) =>
        _trackReader.GetTrackAsync(trackId, ct);

    public async Task MatchAsync(
        IReadOnlyList<SegmentId> segmentIds, string userId, IReadOnlyList<string> friendIds, CancellationToken ct = default)
    {
        if (segmentIds.Count == 0) return;

        foreach (var segmentId in segmentIds)
        {
            ct.ThrowIfCancellationRequested();   // SB-03: cancel between segments (batch callers).

            // SC-01: compute the full match set BEFORE touching stored rows. Several inputs pass
            // RuleDocument.Validate() yet throw when the candidate query executes (NaN param, >32k SQL
            // params, date overflow). Deleting first — as this method used to — would leave the segment
            // permanently effort-less because every re-run repeats the same wipe-then-throw.
            if (await ComputeAsync(segmentId, userId, friendIds, restrictToTrackId: null, ct) is not { } computed)
                continue;
            var (evaluator, candidates, matches) = computed;

            // Rebuild from scratch: shape efforts are produced ONLY here, and the candidate scope
            // spans every visible owner (own + public + friends). So we must clear ALL of this
            // segment's efforts — not just the current user's — otherwise other users' efforts
            // are re-inserted on top of the existing rows and accumulate on every re-analyse (there
            // is no unique constraint on GPSinSegment). The DELETE runs unconditionally (even when
            // nothing matches now) so the segment always reflects the latest match result. Delete +
            // insert are wrapped in one transaction so a crash between them can't half-apply.
            await using (var tx = await _context.Database.BeginTransactionAsync(ct))
            {
                await _context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM GPSinSegment WHERE segment_id = {0};", new object[] { segmentId.Value }, ct);

                if (matches.Count > 0)
                {
                    _context.GpsInSegments.AddRange(matches.Select(m => ToEffort(m, segmentId, evaluator)));
                    await _context.SaveChangesAsync(ct);
                }

                await tx.CommitAsync(ct);
            }

            _logger.LogInformation(
                "Shape segment {SegmentId}: {Candidates} candidates → {Matches} efforts",
                segmentId, candidates, matches.Count);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SegmentId>> MatchTrackAsync(
        TrackId trackId, IReadOnlyList<SegmentId> segmentIds, string userId, IReadOnlyList<string> friendIds,
        CancellationToken ct = default)
    {
        if (segmentIds.Count == 0) return [];

        var changed = new List<SegmentId>();
        foreach (var segmentId in segmentIds)
        {
            ct.ThrowIfCancellationRequested();

            if (await ComputeAsync(segmentId, userId, friendIds, trackId.Value, ct) is not { } computed)
                continue;
            var (evaluator, candidates, matches) = computed;

            // Only THIS (track, segment) pair — never the segment-wide DELETE MatchAsync uses, which would
            // drop every other track's efforts. Idempotent: a re-run deletes what it is about to rewrite,
            // so a queue redelivery cannot duplicate rows (there is no unique constraint on GPSinSegment).
            int deleted = matches.Count > 0
                ? await DeleteThenInsertAsync(trackId, segmentId, matches, evaluator, ct)
                // Nothing to write, so nothing to make atomic WITH — a lone DELETE is already one statement.
                // The common incremental case is a ride that joins none of the user's shape segments, and
                // wrapping that in a transaction per (track, segment) is pure overhead.
                : await DeleteTrackRowsAsync(trackId, segmentId, ct);

            if (deleted > 0 || matches.Count > 0) changed.Add(segmentId);

            _logger.LogInformation(
                "Shape segment {SegmentId} track {TrackId}: {Candidates} candidates → {Matches} efforts",
                segmentId, trackId, candidates, matches.Count);
        }

        return changed;
    }

    // Delete + insert as one unit so a crash between them can't half-apply this pair's rows.
    private async Task<int> DeleteThenInsertAsync(
        TrackId trackId, SegmentId segmentId, List<MatchRow> matches, SegmentRuleEvaluator evaluator,
        CancellationToken ct)
    {
        await using var tx = await _context.Database.BeginTransactionAsync(ct);
        int deleted = await DeleteTrackRowsAsync(trackId, segmentId, ct);
        _context.GpsInSegments.AddRange(matches.Select(m => ToEffort(m, segmentId, evaluator)));
        await _context.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return deleted;
    }

    private Task<int> DeleteTrackRowsAsync(TrackId trackId, SegmentId segmentId, CancellationToken ct) =>
        _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM GPSinSegment WHERE segment_id = {0} AND track_id = {1};",
            new object[] { segmentId.Value, trackId.Value }, ct);

    // Rules + owner scope are per-segment, so the candidate set is loaded per segment. Null = the segment
    // is gone, or its rule document is missing/corrupt (pre-rework segments land there) — caller skips it.
    // Stage1 comes back as a COUNT, not the list: both callers only report it, and the list would invite a
    // caller to re-derive the match set from it rather than from Matches.
    private async Task<(SegmentRuleEvaluator Evaluator, int Candidates, List<MatchRow> Matches)?> ComputeAsync(
        SegmentId segmentId, string userId, IReadOnlyList<string> friendIds, int? restrictToTrackId,
        CancellationToken ct)
    {
        var meta = await _context.Segments
            .Where(s => s.SegmentId == segmentId)
            .Select(s => new { s.MatchRulesJson, s.MatchOwnerIds })
            .FirstOrDefaultAsync(ct);
        if (meta == null) return null;

        var evaluator = SegmentRuleEvaluator.TryCompile(meta.MatchRulesJson, out var errors);
        if (evaluator == null)
        {
            _logger.LogWarning("Shape segment {SegmentId} has no usable rule document ({Errors}); skipping",
                segmentId, string.Join("; ", errors));
            return null;
        }

        // Non-spatial filters (owner precedence + date/type) → candidate-loading SQL predicates.
        var filters    = CandidateSqlFilters.Build(evaluator.TrackFilters, meta.MatchOwnerIds, userId);
        var candidates = await LoadCandidateBboxesAsync(filters, userId, friendIds, ct, restrictToTrackId);
        var stage1     = candidates.Count == 0 ? new List<int>() : Stage1Candidates(evaluator, candidates);
        var matches    = stage1.Count == 0 ? new List<MatchRow>() : await Stage2ConfirmAsync(evaluator, stage1, ct);
        return (evaluator, stage1.Count, matches);
    }

    private static GpsInSegment ToEffort(MatchRow m, SegmentId segmentId, SegmentRuleEvaluator evaluator) =>
        new()
        {
            TrackId            = new TrackId(m.TrackId),
            SegmentId          = segmentId,
            SegPerc            = 100,                  // matched per rule document
            SegGpsCount        = m.InsidePoints,
            SegTotalCount      = evaluator.ShapeCount,
            GpsIdEntryGuessed  = m.EntryGpsId,
            GpsIdExitGuessed   = m.ExitGpsId,
            GpsIdCount         = m.ExitGpsId - m.EntryGpsId,
        };

    /// <summary>
    /// Stage-1-only candidate ids for an unsaved rule document — drives the instant,
    /// approximate live-preview count while the user is still painting. Over-counts:
    /// it only asks whether each activity's whole-track bbox could satisfy the rules, not
    /// whether the track actually does. <see cref="PreviewExactTrackIdsAsync"/>
    /// refines this in the background.
    /// </summary>
    public async Task<List<int>> PreviewCandidateTrackIdsAsync(
        RuleDocument doc, string userId, IReadOnlyList<string> friendIds, CancellationToken ct = default)
    {
        var evaluator = SegmentRuleEvaluator.Compile(doc);
        // The owner filter is already folded into the doc server-side, so there is no legacy CSV here.
        var filters = CandidateSqlFilters.Build(doc.TrackFilters, legacyOwnerCsv: null, userId);
        var candidates = await LoadCandidateBboxesAsync(filters, userId, friendIds, ct);
        return Stage1Candidates(evaluator, candidates);
    }

    /// <summary>
    /// Precise matching track ids for an unsaved rule document — the same evidence + rule
    /// evaluation the save path runs, but without writing anything. Runs the cheap bbox
    /// pre-filter first, then confirms each candidate against the actual GPS points, so the
    /// count equals what saving would produce. Heavier than the bbox preview; the caller
    /// runs it in the background and should pass a cancellation token tied to the request.
    /// </summary>
    public async Task<PreviewExactResult> PreviewExactTrackIdsAsync(
        RuleDocument doc, string userId, IReadOnlyList<string> friendIds, CancellationToken ct = default)
    {
        var evaluator = SegmentRuleEvaluator.Compile(doc);
        var filters = CandidateSqlFilters.Build(doc.TrackFilters, legacyOwnerCsv: null, userId);
        var candidates = await LoadCandidateBboxesAsync(filters, userId, friendIds, ct);
        var stage1 = Stage1Candidates(evaluator, candidates);

        // SB-01: bound the GPS-decode work. Over the cap, confirm only the first N candidates and flag
        // the result truncated so the UI can prompt the user to refine the shape (the exact preview is a
        // refinement, not the source of truth — saving runs the uncapped matcher through the queue).
        bool truncated = stage1.Count > MaxExactPreviewCandidates;
        if (truncated) stage1 = stage1.GetRange(0, MaxExactPreviewCandidates);

        var matches = await Stage2ConfirmAsync(evaluator, stage1, ct);
        // A track can yield several efforts (laps) — trackIds is distinct, effortCount is every row.
        return new PreviewExactResult(
            matches.Select(m => m.TrackId).Distinct().ToList(), matches.Count, truncated);
    }

    /// <summary>Precise preview totals: matching tracks, total efforts (≥ track count under gates), and
    /// whether the candidate set was capped (SB-01) before confirmation.</summary>
    public readonly record struct PreviewExactResult(List<int> TrackIds, int EffortCount, bool Truncated);

    private static List<int> Stage1Candidates(SegmentRuleEvaluator evaluator, List<CandidateBbox> candidates) =>
        candidates
            .Where(c => evaluator.CouldMatch(c.Bbox))
            .Select(c => c.TrackId)
            .ToList();

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Candidate activity bboxes restricted by the non-spatial <paramref name="filters"/> (owner scope,
    /// date range, activity type) AND re-authorised against the current visibility rules (mirrors
    /// <c>VisibilityService.VisibleActivities</c>): the current user's own tracks, any public track, and
    /// friends' tracks whose visibility is "friends". The visibility clause is re-checked every run, so a
    /// revoked friendship / changed visibility drops those tracks without touching the stored filters.
    ///
    /// The cross-user (public/friends) branch additionally requires <c>lifecycle = 'released'</c> — the same
    /// gate VisibleActivities applies to every read path (invariant 2: only a released activity is a real
    /// one). Without it, a friend's trashed / awaiting-review track kept its friends/public visibility value
    /// and leaked its existence + geo-correlation through the shape preview. The owner branch is deliberately
    /// exempt: a caller matching their OWN tracks stays owner-scoped, exactly as the polyline pipeline treats
    /// them (MatchEngineService is own-only with no lifecycle filter), and those efforts are hidden on read
    /// anyway.
    /// </summary>
    /// <param name="restrictToTrackId">Incremental path: narrow the candidate set to this one track. The
    /// filters and the visibility clause below still apply unchanged — a rule that excludes the track (wrong
    /// activity type, outside the date window, owner not in scope) must exclude it here too.</param>
    private async Task<List<CandidateBbox>> LoadCandidateBboxesAsync(
        IReadOnlyList<ICandidateSqlFilter> filters, string currentUserId,
        IReadOnlyList<string> friendIds, CancellationToken ct, int? restrictToTrackId = null)
    {
        // Build positional placeholders ({0},{1},…) so every value is parameterised, never interpolated.
        var args = new List<object>();
        string P(object v) { var p = "{" + args.Count + "}"; args.Add(v); return p; }

        // Filters always include exactly one owner filter, so the AND chain is never empty.
        string filterSql = string.Join(" AND ", filters.Select(f => f.ToSql(P)));
        string curUser  = P(currentUserId);
        string released = P(LifecycleStates.Released);
        string friendClause = friendIds.Count > 0
            ? $"OR (a.visibility = 'friends' AND a.user_id IN ({string.Join(",", friendIds.Select(P))}))"
            : "";

        string trackClause = restrictToTrackId is int only ? $"AND ab.track_ID = {P(only)}" : "";

        var sql = $@"
            SELECT ab.track_ID AS TrackId,
                   ab.min_lat  AS MinLat,
                   ab.max_lat  AS MaxLat,
                   ab.min_long AS MinLong,
                   ab.max_long AS MaxLong
            FROM   Activity_Bbox ab
            INNER JOIN Activity a ON a.track_ID = ab.track_ID
            WHERE  {filterSql}
              {trackClause}
              AND  ( a.user_id = {curUser}
                     OR ( a.lifecycle = {released}
                          AND ( a.visibility = 'public'
                                {friendClause} ) ) )";

        var rows = await _context.Database
            .SqlQueryRaw<ActivityBboxRow>(sql, args.ToArray())
            .ToListAsync(ct);

        return rows
            .Select(r => new CandidateBbox(r.TrackId, new GeoBoundingBox(r.MinLat, r.MaxLat, r.MinLong, r.MaxLong)))
            .ToList();
    }

    private async Task<List<MatchRow>> Stage2ConfirmAsync(
        SegmentRuleEvaluator evaluator, List<int> candidateTrackIds, CancellationToken ct)
    {
        var results = new List<MatchRow>();

        // Ascending TrackId within each chunk reproduces the prior "ORDER BY TrackId" + GroupBy
        // first-appearance order → identical results order (the fingerprint is order-insensitive
        // anyway). Every candidate track is decoded once from the shared cache; the same tracks
        // are no longer re-materialized per segment.
        foreach (var chunk in Chunk(candidateTrackIds, CandidateGpsChunk))
        foreach (var trackId in chunk.OrderBy(id => id))
        {
            ct.ThrowIfCancellationRequested();   // SB-03: ReadTrackAsync is a no-op cancel point on a
                                                 // warm cache hit; needed so an aborted preview-exact
                                                 // request (SB-01) actually stops decoding.
            var track = await ReadTrackAsync(new TrackId(trackId), ct);
            if (track == null) continue;                                   // was: absent from GroupBy

            var samples = new List<TrackSample>(track.Count);
            for (int i = 0; i < track.Count; i++)
            {
                double la = track.Lat[i], ln = track.Lng[i];
                if (double.IsNaN(la) || double.IsNaN(ln)) continue;   // was: SQL IS NOT NULL
                double t = double.IsNaN(track.TimeEpochS[i]) ? 0 : track.TimeEpochS[i];    // was: epoch secs, 0 when NULL
                samples.Add(new TrackSample(i, la, ln, t));
            }
            if (samples.Count == 0) continue;                              // was: no group emitted

            var evidence = TrackEvidenceExtractor.Extract(
                samples, evaluator.Zones, evaluator.Gates, evaluator.GateDebounceSeconds);

            foreach (var effort in evaluator.Evaluate(evidence))
            {
                results.Add(new MatchRow(
                    trackId,
                    samples[effort.Span.EntryIdx].GpsId,
                    samples[effort.Span.ExitIdx].GpsId,
                    effort.InsidePoints));
            }
        }

        return results;
    }

    private static IEnumerable<List<int>> Chunk(List<int> items, int size)
    {
        for (int i = 0; i < items.Count; i += size)
            yield return items.GetRange(i, Math.Min(size, items.Count - i));
    }

    private readonly record struct CandidateBbox(int TrackId, GeoBoundingBox Bbox);
    private readonly record struct MatchRow(int TrackId, int EntryGpsId, int ExitGpsId, int InsidePoints);
}

// Projection type for the raw Activity_Bbox query.
// Internal (not file-scoped) so EF Core's SqlQueryRaw<T> reflection can instantiate it.
internal sealed class ActivityBboxRow
{
    public int    TrackId { get; set; }
    public double MinLat  { get; set; }
    public double MaxLat  { get; set; }
    public double MinLong { get; set; }
    public double MaxLong { get; set; }
}
