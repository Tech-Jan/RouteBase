using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Sql;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>Revert/commit-summary result: an error (with its HTTP kind) or the success payload.</summary>
public sealed record RevertResult(TrackEditErrorKind? ErrorKind, TrackEditErrorBody? Error, RevertResponse? Response)
{
    public static RevertResult Fail(TrackEditErrorKind kind, string code) =>
        new(kind, new TrackEditErrorBody(code), null);
}

/// <summary>
/// Undo a save commit (A2 §6 + §14.4 stats mirror). A commit's effect on content IS its version rows, so
/// reverting is a HEAD MOVE: point each activity the commit versioned back at that version's parent. No blob
/// is rewritten and no snapshot is needed — the pre-edit version still exists under its own hash.
///
/// All preconditions are verified before any write (atomic per commit group): each versioned activity still
/// exists and still has that version as its head (a newer non-reverted commit moved it on → chained revert
/// must go newest→oldest, G20), the version has a parent to go back to, and no active recording. Phase 1 (one
/// tx): move each head + recompute its scalar stats/bbox/start-time from the restored content, un-consume
/// trashed sources, retarget the edited badge, mark reverted. Phase 2: delete the commit's created activities.
/// </summary>
public sealed class TrackEditRevertService
{
    private readonly TrackEditCommitStore _commits;
    private readonly TrackContentStore _content;
    private readonly RecordingStagingStore _staging;
    private readonly IActivityCommandService _command;
    private readonly ISegmentAnalysis _segments;
    private readonly IActivityTimezoneResolver _resolver;
    private readonly IGpsNormalizationService _normalization;
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;
    private readonly UserCacheInvalidator _invalidator;
    private readonly OperationQueue _queue;

    private readonly TimeProvider _clock;

    public TrackEditRevertService(
        TrackEditCommitStore commits, TrackContentStore content,
        RecordingStagingStore staging, IActivityCommandService command,
        ISegmentAnalysis segments, IActivityTimezoneResolver resolver, IGpsNormalizationService normalization,
        RouteBaseDbContext context, IVisibilityService visibility, UserCacheInvalidator invalidator, OperationQueue queue,
        TimeProvider clock)
    {
        _clock = clock;
        _commits = commits;
        _content = content;
        _staging = staging;
        _command = command;
        _segments = segments;
        _resolver = resolver;
        _normalization = normalization;
        _context = context;
        _visibility = visibility;
        _invalidator = invalidator;
        _queue = queue;
    }

    public async Task<RevertResult> RevertAsync(CommitId commitId, CancellationToken ct = default)
    {
        string userId = _visibility.GetCurrentUserId();
        var commit = await _commits.GetAsync(commitId, ct);
        if (commit is null || commit.UserId != userId) return new RevertResult(TrackEditErrorKind.NotFound, null, null);

        // The commit's versions ARE its effect on content — no snapshot table to consult. Only the ones it
        // REPLACED get their head moved back; the ones it CREATED (seq 1) are deleted whole in phase 2.
        var allVersions = await _content.VersionsOfCommitAsync(commitId, ct);
        var replaced = ReplacedVersions(allVersions);
        var (revertible, blockedBy, parentOf) = await EvaluateAsync(commit, allVersions, ct);
        if (!revertible)
            return RevertResult.Fail(TrackEditErrorKind.Conflict,
                blockedBy ?? throw new InvalidOperationException("EvaluateAsync said not-revertible without a code"));

        var now = _clock.GetUtcNow().UtcDateTime;
        int untrashed = 0;

        // Phase 1 — one tx: move each head back to the version this commit superseded, recompute that
        // track's stats from the restored content, un-consume sources, retarget badge, mark reverted.
        await using (var tx = await _context.Database.BeginTransactionAsync(ct))
        {
            foreach (var version in replaced)
            {
                var id = version.ActivityId;
                await _content.SetHeadAsync(id, parentOf[version.VersionId], ct);

                var restored = await _content.GetHeadDataAsync(new TrackId(id.Value), ct)
                    ?? throw new InvalidOperationException($"activity {id} version {parentOf[version.VersionId]} has no object");
                var track = TrackBlobCodec.Decode(restored);
                await TrackEditScalarStats.WriteAsync(_context, _normalization, _resolver, new TrackId(id.Value), track, userId, ct);

                CommitId? badgeCommit = await _commits.NewestOtherCommitReplacingAsync(new TrackId(id.Value), commitId, userId, ct);
                if (badgeCommit is { } bc)
                    await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.SetEditedCommitSql, bc.Value, id.Value, userId);
                else
                    await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.ClearEditedCommitSql, id.Value, userId);
            }

            foreach (var id in commit.TrashedIds)
            {
                // Guarded UPDATE + audit twin in one tx (ActivityLifecycleSql) — the un-consume is a lifecycle
                // transition like any other, and must leave the same trace.
                int n = await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.RestoreConsumedSql, id.Value, userId);
                if (n == 1)
                    await _context.Database.ExecuteSqlRawAsync(ActivityLifecycleSql.InsertLogSql,
                        id.Value, userId, LifecycleStates.Trashed, LifecycleStates.Released, LifecycleReasons.User, commitId.Value);
                untrashed += n;
            }

            // Send the merge's photos home BEFORE phase 2 deletes the ride they were moved onto — a cascade
            // would otherwise take them with it. Runs in this transaction, so a photo is never left on a row
            // that is about to vanish.
            foreach (var id in commit.CreatedIds)
                await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.RestorePhotosFromMergeSql, id.Value, userId);

            await _commits.MarkRevertedAsync(commitId, now, ct);
            await tx.CommitAsync(ct);
        }

        // No decode-cache invalidation: the restored head is an object we may already hold under its own
        // hash, and that decode is still exactly right.
        _invalidator.InvalidateUser(userId);

        // Phase 2 — delete created activities (each internally atomic; stale-mark heatmaps before rows vanish).
        int deleted = 0;
        foreach (var id in commit.CreatedIds)
        {
            await _segments.RemoveTrackFromSegmentsAsync(new TrackId(id.Value), userId, ct);
            if (await _command.DeleteActivityCoreAsync(new TrackId(id.Value), userId)) deleted++;
        }

        bool deriveQueued = _queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type = OperationQueue.OperationType.TrackEditDerive,
            UserId = userId,
            Payload = new TrackEditDerivePayload(userId, commitId.Value,
                commit.ReplacedIds.Select(t => t.Value).ToList(), commit.TrashedIds.Select(t => t.Value).ToList(), []),
        });

        return new RevertResult(null, null,
            new RevertResponse(commitId.Value, commit.ReplacedIds.Count, untrashed, deleted, deriveQueued));
    }

    /// <summary>GET commit summary: the audit row + name lookups (missing activity → "(deleted)") + the dry
    /// revert eligibility. Null → 404 (missing/foreign).</summary>
    public async Task<CommitSummaryResponse?> GetSummaryAsync(CommitId commitId, CancellationToken ct = default)
    {
        string userId = _visibility.GetCurrentUserId();
        var commit = await _commits.GetAsync(commitId, ct);
        if (commit is null || commit.UserId != userId) return null;

        var names = await ActivityMetaQueries.NamesAsync(
            _context, commit.ReplacedIds.Concat(commit.CreatedIds).Concat(commit.TrashedIds), ct);
        ActivityRef Ref(TrackId id) => new(id.Value, names.GetValueOrDefault(id) ?? "(deleted)");

        var (revertible, blockedBy, _) = await EvaluateAsync(
            commit, await _content.VersionsOfCommitAsync(commitId, ct), ct);

        return new CommitSummaryResponse(
            commit.CommitId.Value, commit.SaveMode, commit.CommittedAt.ToUtcOffset(),
            commit.ReplacedIds.Select(Ref).ToList(),
            commit.TrashedIds.Select(Ref).ToList(),
            commit.CreatedIds.Select(Ref).ToList(),
            commit.RevertedAt.ToUtcOffset(), revertible, blockedBy);
    }

    // ---- eligibility (dry; shared by revert + summary) ----

    /// <summary>
    /// Derived entirely from the version rows — there is no snapshot table and no "revertible" column to keep
    /// in sync. Also returns the head each activity moves back to, so the caller never re-walks the lineage.
    /// (<c>snapshotMissing</c> keeps its wire name: it still means "the state this revert would restore is gone".)
    /// </summary>
    private async Task<(bool Revertible, string? BlockedBy, Dictionary<VersionId, VersionId> ParentOf)> EvaluateAsync(
        TrackEditCommitEntry commit, IReadOnlyList<TrackVersionRow> allVersions, CancellationToken ct)
    {
        var parentOf = new Dictionary<VersionId, VersionId>();
        if (commit.RevertedAt is not null) return (false, TrackEditApiErrors.AlreadyReverted, parentOf);

        var versionByActivity = ReplacedVersions(allVersions).ToDictionary(v => v.ActivityId);
        foreach (var id in commit.ReplacedIds)
        {
            // The version this commit wrote must still be on record (its content is what we are undoing).
            if (!versionByActivity.TryGetValue(id, out var version))
                return (false, TrackEditApiErrors.SnapshotMissing, parentOf);

            // The activity must still exist + be owned (a permanent purge takes its versions with it).
            bool exists = await _visibility.OwnedActivities(_context.Activities).AnyAsync(a => a.TrackId == id, ct);
            if (!exists) return (false, TrackEditApiErrors.SnapshotMissing, parentOf);

            // It must still BE the version this commit wrote: a newer commit already moved the head on, so a
            // chained revert has to go newest→oldest (G20). The head IS the ground truth — no commit-date math.
            if (await _content.GetHeadVersionIdAsync(new TrackId(id.Value), ct) != version.VersionId)
                return (false, TrackEditApiErrors.SupersededCommit, parentOf);

            // …and the version to go back to must exist (exactly one — an edit supersedes exactly one) and
            // still be on record: a retention sweep that reclaimed it leaves nothing to restore.
            var parents = await _content.ParentsOfAsync(version.VersionId, ct);
            if (parents.Count != 1) return (false, TrackEditApiErrors.SnapshotMissing, parentOf);
            if (await _content.GetVersionAsync(parents[0], ct) is null)
                return (false, TrackEditApiErrors.SnapshotMissing, parentOf);
            parentOf[version.VersionId] = parents[0];

            if (await _staging.ExistsAsync(new TrackId(id.Value), ct))
                return (false, TrackEditApiErrors.StillRecording, parentOf);

            // An open editor stamped this ride's CURRENT head and will replay its script against those bytes.
            // Moving the head back under it is the same class of hazard as a live recording's staged tail —
            // refuse, and say which state the user has to resolve first (the resume banner's Discard).
            if (await ActivityMetaQueries.IsCheckedOutAsync(_context, new TrackId(id.Value), ct))
                return (false, TrackEditApiErrors.AlreadyCheckedOut, parentOf);
        }

        // The rides this commit CREATED are deleted by the revert — a split product someone is editing must not
        // vanish under them.
        foreach (var id in commit.CreatedIds)
            if (await ActivityMetaQueries.IsCheckedOutAsync(_context, id, ct))
                return (false, TrackEditApiErrors.AlreadyCheckedOut, parentOf);

        // Invariant 4: un-merging means putting the swallowed rides BACK. Once one has been purged — allowed,
        // behind the "this merge can never be undone" confirmation — there is nothing to put back, and a revert
        // would silently deliver a partial merge.
        //
        // The check cannot ask commit.TrashedIds: that list is DERIVED from live rows, so a purged input simply
        // vanishes from it (batch 1, by design). The lineage DAG is the only thing that still remembers the
        // input existed — a merged version records its inputs' heads as parents, and parent_version_id
        // deliberately carries no FK precisely so that purging a version leaves the link DANGLING rather than
        // cascading it away. A dangling parent IS the evidence.
        foreach (var created in allVersions.Where(v => v.Seq == 1))
            foreach (var parentId in await _content.ParentsOfAsync(created.VersionId, ct))
                if (await _content.GetVersionAsync(parentId, ct) is null)
                    return (false, TrackEditApiErrors.InputsPurged, parentOf);

        return (true, null, parentOf);
    }

    /// <summary>A commit's versions split by what they mean: seq 1 = it CREATED that track (revert deletes the
    /// whole activity), seq > 1 = it REPLACED an existing one (revert moves that head back).</summary>
    private static IReadOnlyList<TrackVersionRow> ReplacedVersions(IReadOnlyList<TrackVersionRow> versions) =>
        versions.Where(v => v.Seq > 1).ToList();
}
