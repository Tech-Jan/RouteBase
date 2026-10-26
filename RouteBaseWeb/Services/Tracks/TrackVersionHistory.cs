using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sql;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RouteBaseWeb.Services.Tracks;

/// <summary>One row of a track's history, as the UI reads it.</summary>
/// <param name="SameAsParent">
/// This version points at content a parent already had — it introduced nothing new (a restore does exactly
/// this: it re-heads an existing object). The UI folds these away by default, because a long history is mostly
/// made of them and they are the rows that carry the least information.
/// </param>
/// <param name="CommitId">
/// The save that wrote this version, when one did (null = an import, a recording, a restore or a recompute).
/// It is what carries the ride's un-merge/un-split door: a merge product's seq-1 version was written by the
/// merge commit, so the row can offer to undo it. That door used to hang off <c>Activity.edited_commit_id</c>,
/// which both reset and revert clear — leaving a merged ride revertible in the data with no way to reach it.
/// Reading it here costs nothing: it is already on the version row.
/// </param>
public sealed record TrackVersionView(
    int VersionId, int Seq, bool IsHead, DateTimeOffset CreatedAt,
    int PointCount, double? DistanceMeters, double? AscentMeters, double? DurationSeconds,
    string Provenance, IReadOnlyList<string> ParentNames, bool SameAsParent, int? CommitId);

/// <summary>The version list as its callers depend on it — the abstraction the API controller takes.</summary>
public interface ITrackVersionHistory
{
    Task<bool> OwnsAsync(TrackId trackId, CancellationToken ct = default);
    /// <summary>The two facts the panel needs to decide whether to fetch the list at all (one indexed seek).</summary>
    Task<TrackHistoryFacts> FactsAsync(TrackId trackId, CancellationToken ct = default);
    Task<IReadOnlyList<TrackVersionView>> ListAsync(TrackId trackId, CancellationToken ct = default);
    Task<string?> ResetToOriginalAsync(TrackId trackId, CancellationToken ct = default);
    Task<string?> RestoreAsync(TrackId trackId, VersionId versionId, CancellationToken ct = default);
}

/// <summary>
/// A track's revision list, rendered. Read-only and owner-scoped; the writes (restore a version) go through
/// <see cref="TrackContentStore"/>, which is the only thing that may move a head.
///
/// This is what the append-only version list is FOR: every state a ride was ever in is still on record, so
/// "what did this look like before I cropped it?" is a question with an answer — and, because a version points
/// at an immutable object, restoring one costs zero new bytes.
/// </summary>
public sealed class TrackVersionHistory(
    RouteBaseDbContext context, TrackContentStore content, IVisibilityService visibility,
    IGpsNormalizationService normalization, IActivityTimezoneResolver resolver,
    RecordingStagingStore staging, ITrackDeriveQueue derives, ITrackObjectStore objects,
    UserCacheInvalidator invalidator, ILogger<TrackVersionHistory> logger) : ITrackVersionHistory
{
    private readonly RouteBaseDbContext _context = context;
    private readonly TrackContentStore _content = content;
    private readonly IVisibilityService _visibility = visibility;
    private readonly IGpsNormalizationService _normalization = normalization;
    private readonly IActivityTimezoneResolver _resolver = resolver;
    private readonly RecordingStagingStore _staging = staging;
    private readonly ITrackDeriveQueue _derives = derives;
    private readonly ITrackObjectStore _objects = objects;
    private readonly UserCacheInvalidator _invalidator = invalidator;
    private readonly ILogger<TrackVersionHistory> _logger = logger;

    public async Task<bool> OwnsAsync(TrackId trackId, CancellationToken ct = default) =>
        await _visibility.OwnedActivities(_context.Activities).AnyAsync(a => a.TrackId == trackId, ct);

    /// <inheritdoc />
    public Task<TrackHistoryFacts> FactsAsync(TrackId trackId, CancellationToken ct = default) =>
        _content.HistoryFactsAsync(trackId, ct);

    /// <summary>
    /// The track's versions, newest first (the head is what you are looking at now).
    ///
    /// FIVE queries, whatever the history's size: the lineage edges and the parents they point at are read in
    /// one batch each, and a parent on THIS track is already in the version list — re-fetching it per edge was
    /// an N+1 over the DAG (3 + V×(1+P) roundtrips for a list the reader mostly already held).
    /// </summary>
    public async Task<IReadOnlyList<TrackVersionView>> ListAsync(TrackId trackId, CancellationToken ct = default)
    {
        var versions = await _content.ListVersionsAsync(trackId, ct);
        VersionId? headId = await _content.GetHeadVersionIdAsync(trackId, ct);
        string? source = await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == trackId).Select(a => a.Source).FirstOrDefaultAsync(ct);

        var parentsBy = await _content.ParentsOfManyAsync(versions.Select(v => v.VersionId).ToList(), ct);
        var byId = versions.ToDictionary(v => v.VersionId);
        var foreign = parentsBy.Values.SelectMany(p => p).Distinct().Where(id => !byId.ContainsKey(id)).ToList();
        var foreignBy = (await _content.GetVersionsAsync(foreign, ct)).ToDictionary(v => v.VersionId);

        var names = await NamesByIdAsync(foreignBy.Values.Select(v => v.ActivityId).Distinct().ToList(), ct);

        var views = new List<TrackVersionView>(versions.Count);
        foreach (var v in versions)
        {
            var parentIds = parentsBy.TryGetValue(v.VersionId, out var ids) ? ids : [];

            // A parent that no longer resolves was purged — say so rather than pretending the lineage is intact.
            var parentTracks = new List<TrackId>();
            int missing = 0;
            bool sameObjectAsParent = false;
            foreach (var pid in parentIds)
            {
                if (!byId.TryGetValue(pid, out var parent) && !foreignBy.TryGetValue(pid, out parent)) missing++;
                else
                {
                    if (parent.ActivityId != trackId) parentTracks.Add(parent.ActivityId);
                    if (parent.ObjectSha == v.ObjectSha) sameObjectAsParent = true;
                }
            }

            views.Add(new TrackVersionView(
                v.VersionId.Value, v.Seq, v.VersionId == headId, v.CreatedAt.ToUtcOffset(),
                v.PointCount, v.Distance, v.Ascent, v.DurationS,
                Provenance(v, parentIds.Count, parentTracks.Count, missing, sameObjectAsParent, source),
                parentTracks.Distinct()   // one name per ride, as the per-version name query returned
                    .Select(id => names.TryGetValue(id, out var n) ? n : $"Activity {id}").ToList(),
                sameObjectAsParent, v.CommitId?.Value));
        }

        views.Reverse();   // newest first
        return views;
    }

    /// <summary>
    /// Put the ride back the way it started, and forget it was ever otherwise: the head moves to the FIRST
    /// version ever written for it, every later version is deleted, and the edit history is emptied.
    ///
    /// "Original" means the ride's first version — for an imported or recorded ride, the points it arrived
    /// with. For a ride the EDITOR created (a split, a merge product) it means how that ride was created: this
    /// resets its own edits, it does not un-merge it. Undoing a merge is a commit revert, which is a different
    /// button and says so.
    ///
    /// This is the one irreversible operation in a system that is otherwise append-only, so be exact about the
    /// cost: every intermediate state is gone for good, and the saves that produced them can no longer be
    /// undone (their revert target no longer exists) — so the badge that offers that undo is cleared rather
    /// than left lying. A ride SPLIT from this one keeps its lineage edge, now dangling, which the reader
    /// already reports honestly as "the rides it came from have been deleted"; that is the truth, so it stands.
    ///
    /// Returns null on success, else an error code.
    /// </summary>
    public async Task<string?> ResetToOriginalAsync(TrackId trackId, CancellationToken ct = default)
    {
        // Same guard as restore/save: a live recording's staged tail rides on the current head — moving the
        // head under it would splice the tail onto different geometry at finalize. An open editor holds the
        // head the same way (it stamped those bytes), and a reset would also delete the versions it saves onto.
        if (await _staging.ExistsAsync(trackId, ct)) return TrackEditApiErrors.StillRecording;
        if (await ActivityMetaQueries.IsCheckedOutAsync(_context, trackId, ct))
            return TrackEditApiErrors.AlreadyCheckedOut;

        var versions = await _content.ListVersionsAsync(trackId, ct);
        if (versions.Count == 0) return "notFound";
        if (await _content.GetHeadVersionIdAsync(trackId, ct) is not { } headId) return "notFound";

        var original = versions.OrderBy(v => v.Seq).First();
        var head = versions.First(v => v.VersionId == headId);
        var doomed = versions.Where(v => v.VersionId != original.VersionId).Select(v => v.VersionId).ToList();
        if (doomed.Count == 0 && headId == original.VersionId) return null;   // already original — idempotent

        // Content is addressed by hash, so "did the ride actually change?" is a sha comparison, not a version-id
        // one: the head may be a RESTORE of the original, a different row holding the very same points.
        bool pointsMove = head.ObjectSha != original.ObjectSha;
        string userId = _visibility.GetCurrentUserId();

        IReadOnlyList<string> candidates;
        await using (var tx = await _context.Database.BeginTransactionAsync(ct))
        {
            await _content.SetHeadAsync(trackId, original.VersionId, ct);
            if (pointsMove) await RebuildScalarsFromHeadAsync(trackId, userId, ct);

            // The surviving version's edges into the chain we are deleting must go with it, or the reader sees a
            // parent that no longer resolves and calls a never-merged ride "merged from rides that have been
            // deleted". Its CROSS-TRACK parents (a real merge/split) point at versions that still exist — those
            // are real provenance and are left alone.
            var doomedSet = doomed.ToHashSet();
            var parents = await _content.ParentsOfAsync(original.VersionId, ct);
            await _content.DropParentEdgesAsync(original.VersionId, parents.Where(doomedSet.Contains).ToList(), ct);

            candidates = await _content.DeleteVersionsAsync(trackId, doomed, ct);
            await _context.Database.ExecuteSqlRawAsync(ActivityEditSql.ClearEditedCommitSql, trackId.Value, userId);
            await tx.CommitAsync(ct);
        }

        // Swept after the versions are gone, so an object a surviving version still points at is never a
        // candidate. Content is shared by hash — an object only dies when NO version of ANY track wants it.
        await _objects.SweepUnreferencedAsync(candidates, ct);

        // The head moved, so the owner's cached slices/coords describe points this ride no longer has. The
        // write invalidates itself (as ActivityLifecycleService does) — a second caller must not have to know.
        _invalidator.InvalidateUser(userId);

        if (pointsMove) EnqueueDerive(trackId, userId);
        return null;
    }

    /// <summary>
    /// Make an earlier version current again. Append-only: this does not rewrite history, it adds to it —
    /// "restored v2" is itself a point in the record — and it costs zero new bytes, because the object it
    /// points at is already there. The denormalized Activity scalars are rebuilt from the restored content.
    /// Returns null on success, else an error code (<c>notFound</c> / <c>stillRecording</c>).
    /// </summary>
    public async Task<string?> RestoreAsync(TrackId trackId, VersionId versionId, CancellationToken ct = default)
    {
        var target = await _content.GetVersionAsync(versionId, ct);
        if (target is null || target.ActivityId != trackId) return "notFound";

        // Same guard as the editor's save/revert: a live session's staged tail rides on the current head —
        // moving the head under it would splice the tail onto different geometry at finalize. A ride checked
        // out into the editor holds its head the same way: the open session stamped those exact bytes.
        if (await _staging.ExistsAsync(trackId, ct)) return TrackEditApiErrors.StillRecording;
        if (await ActivityMetaQueries.IsCheckedOutAsync(_context, trackId, ct))
            return TrackEditApiErrors.AlreadyCheckedOut;

        string userId = _visibility.GetCurrentUserId();
        await using (var tx = await _context.Database.BeginTransactionAsync(ct))
        {
            await _content.RestoreVersionAsync(trackId, versionId, ct);
            await RebuildScalarsFromHeadAsync(trackId, userId, ct);
            await tx.CommitAsync(ct);
        }

        _invalidator.InvalidateUser(userId);   // the head moved — the cached slices/coords are of the old points
        EnqueueDerive(trackId, userId);
        return null;
    }

    /// <summary>
    /// The head moved, so everything denormalized from it is now a lie: distance, ascent, duration, the bbox,
    /// the start time. Rebuilt from whatever the head now points at, inside the caller's transaction — the same
    /// funnel the editor's save and revert use, so a restored or reset ride cannot disagree with an edited one.
    /// </summary>
    private async Task RebuildScalarsFromHeadAsync(TrackId trackId, string userId, CancellationToken ct)
    {
        var content = await _content.GetHeadDataAsync(trackId, ct)
            ?? throw new InvalidOperationException($"track {trackId}'s head has no object");
        await TrackEditScalarStats.WriteAsync(
            _context, _normalization, _resolver, trackId, TrackBlobCodec.Decode(content), userId, ct);
    }

    /// <summary>
    /// The same derive chain the editor's save and revert enqueue: the geometry changed, so segment matches,
    /// efforts, heatmaps and photo positions computed from the old head are stale until re-derived. No commit
    /// id — neither a restore nor a reset is an edit script.
    /// </summary>
    private void EnqueueDerive(TrackId trackId, string userId) =>
        // TrackDeriveQueue owns the bounded-queue-full warning, so a dropped re-derive stays explicable in
        // the logs. (Surfacing it to the client like save/revert do is a parked contract change, not this fix.)
        _derives.EnqueueFullDerive([trackId.Value], userId, DeriveReason.TrackEdit);

    /// <summary>What this version IS, in the user's words. Derived from the lineage DAG (0 parents = it came
    /// from outside · 1 = an edit, a restore or a system recompute · N = a merge), never from a stored label.
    /// A restore reuses its parent's object (same sha); a recompute (elevation refresh, re-finalize) writes a
    /// new one — that is what tells them apart, since both carry one parent and no commit.</summary>
    private static string Provenance(
        TrackVersionRow v, int parentCount, int crossTrackParents, int missingParents,
        bool sameObjectAsParent, string? source)
    {
        if (missingParents > 0 && parentCount == missingParents)
            return "Merged — the rides it came from have been deleted";
        if (parentCount >= 2)
            return $"Merged from {parentCount} rides";
        if (crossTrackParents == 1)
            return "Split from another ride";
        if (parentCount == 1)
        {
            if (v.CommitId is not null) return "Edited";
            return sameObjectAsParent ? "Restored an earlier version" : "Recalculated (elevation refresh)";
        }
        return source is ActivitySources.LiveRecording or ActivitySources.AutoRecording ? "Recorded" : "Imported";
    }

    /// <summary>Names of every cross-track parent in the whole history, in one query (was one per version).</summary>
    private async Task<IReadOnlyDictionary<TrackId, string>> NamesByIdAsync(
        IReadOnlyCollection<TrackId> trackIds, CancellationToken ct)
    {
        if (trackIds.Count == 0) return new Dictionary<TrackId, string>();
        return (await _context.Activities.AsNoTracking()
                .Where(a => trackIds.Contains(a.TrackId))
                .Select(a => new { a.TrackId, Name = a.TrackName ?? $"Activity {a.TrackId}" })
                .ToListAsync(ct))
            .ToDictionary(x => x.TrackId, x => x.Name);
    }
}
