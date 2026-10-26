using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RouteBaseWeb.Services;

/// <summary>
/// Write operations on activities. Only the owner may mutate or delete; every write invalidates the
/// owner's cache token so viewers' cached GPS/slice entries (tokened to the owner) drop immediately.
/// </summary>
public class ActivityCommandService : IActivityCommandService
{
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;
    private readonly UserCacheInvalidator _invalidator;
    private readonly ILogger<ActivityCommandService> _logger;
    private readonly IActivityLifecycleService _lifecycle;
    private readonly Tracks.ITrackObjectStore? _objects;
    private readonly Photos.IPhotoImageStore? _photoStore;

    // Shared with SegmentCrud via Models.Visibilities — VisibilityService branches on these exact
    // strings for both entity types, so a second private list could silently drift.

    // Object + photo stores are optional: production DI injects them; test constructions pass none (they
    // assert on rows, not on reclaimed objects or filesystem renditions).
    public ActivityCommandService(
        RouteBaseDbContext context, IVisibilityService visibility,
        UserCacheInvalidator invalidator, ILogger<ActivityCommandService> logger,
        IActivityLifecycleService lifecycle,
        Tracks.ITrackObjectStore? objects = null,
        Photos.IPhotoImageStore? photoStore = null)
    {
        _context     = context;
        _visibility  = visibility;
        _invalidator = invalidator;
        _logger      = logger;
        _lifecycle   = lifecycle;
        _objects     = objects;
        _photoStore  = photoStore;
    }

    public async Task<bool> UpdateActivityAsync(TrackId trackId, string? name, string? activityType, string? visibility)
    {
        if (visibility != null && !Visibilities.IsValid(visibility))
            return false;

        var activity = await _visibility.OwnedActivities(_context.Activities)
            .FirstOrDefaultAsync(a => a.TrackId == trackId);
        if (activity == null)
            return false;

        if (name != null)
            activity.TrackName = name;
        if (activityType != null)
            activity.ActivityType = activityType;
        if (visibility != null)
#pragma warning disable CA1308 // visibility persisted lowercase; readers branch on the lowercase vocabulary (Visibilities)
            activity.Visibility = visibility.ToLowerInvariant();
#pragma warning restore CA1308

        await _context.SaveChangesAsync();

        // Evict this user's cached track coords / map slices so the rename/retype/
        // visibility change is reflected immediately instead of after the sliding TTL.
        _invalidator.InvalidateUser(_visibility.GetCurrentUserId());

        _logger.LogInformation("Updated activity {TrackId}: name={Name}, type={Type}, visibility={Vis}",
            trackId, name, activityType, visibility);
        return true;
    }

    public async Task<int> UpdateManyAsync(IReadOnlyList<TrackId> trackIds, string? activityType, string? visibility)
    {
        if (visibility != null && !Visibilities.IsValid(visibility))
            return 0;
        if (trackIds.Count == 0)
            return 0;

        // One load of the owned matches, one SaveChanges, one cache evict — same shape as the lifecycle
        // batch (TransitionManyAsync). Foreign/missing ids simply aren't in the owned set, so they're skipped.
        var owned = await _visibility.OwnedActivities(_context.Activities)
            .Where(a => trackIds.Contains(a.TrackId))
            .ToListAsync();
        if (owned.Count == 0)
            return 0;

        foreach (var activity in owned)
        {
            if (activityType != null)
                activity.ActivityType = activityType;
            if (visibility != null)
#pragma warning disable CA1308 // visibility persisted lowercase; readers branch on the lowercase vocabulary (Visibilities)
                activity.Visibility = visibility.ToLowerInvariant();
#pragma warning restore CA1308
        }

        await _context.SaveChangesAsync();

        // A visibility/type flip stays within the released lifecycle, so — like the single-item path —
        // this only evicts the owner's cached slices; no heatmap stale-mark is needed.
        _invalidator.InvalidateUser(_visibility.GetCurrentUserId());

        _logger.LogInformation("Bulk-updated {Count} activities: type={Type}, visibility={Vis}",
            owned.Count, activityType, visibility);
        return owned.Count;
    }

    /// <summary>
    /// The user-facing delete: moves the activity to the TRASH. Owner-scoped, reversible, destroys nothing.
    ///
    /// This is the whole point of the lifecycle axis. Deleting used to mean an immediate purge, which is what
    /// let one click destroy a merged ride's ten sources beyond recovery. The permanent purge still exists —
    /// <see cref="DeleteActivityCoreAsync"/> — but the user now reaches it only through the trash, behind an
    /// explicit "delete permanently".
    /// </summary>
    public async Task<bool> DeleteActivityAsync(TrackId trackId)
    {
        var result = await _lifecycle.TransitionAsync(
            trackId, LifecycleStates.Trashed, LifecycleReasons.User);
        return result.Ok;
    }

    /// <inheritdoc />
    public async Task<int> DeleteManyAsync(IReadOnlyList<TrackId> trackIds)
    {
        // The lifecycle batch owns the owner-scoping, per-item audit log, single save, and the grouped
        // cache-evict + heatmap stale-mark for the released-boundary crossing. Unowned ids are skipped.
        var result = await _lifecycle.TransitionManyAsync(
            trackIds, LifecycleStates.Trashed, LifecycleReasons.User);
        return result.MovedIds.Count;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteActivityCoreAsync(TrackId trackId, string ownerId, bool requireTrashed = false)
    {
        var activity = await _context.Activities.FirstOrDefaultAsync(a => a.TrackId == trackId);
        if (activity == null || activity.UserId != ownerId)
            return false;

        // Collect this track's photo ids before the delete so their rendition files can be purged after
        // the transaction commits (§1b: row → then files; a leftover file is a harmless swept orphan).
        var photoIds = _photoStore == null
            ? new List<int>()
            : await _context.Database.SqlQueryRaw<int>(
                "SELECT photo_id AS Value FROM Photos WHERE track_id = {0}", trackId.Value).ToListAsync();

        // The objects this track's versions point at — the only ones this purge can possibly orphan. Read
        // BEFORE the delete (the version rows are about to go) and swept after it, so the GC never has to
        // anti-join the whole object table, and an object another writer just PUT is never a candidate.
        var objectShas = _objects == null
            ? new List<string>()
            : await _context.Database.SqlQueryRaw<string>(
                "SELECT DISTINCT object_sha AS Value FROM Activity_Version WHERE track_id = {0}", trackId.Value).ToListAsync();

        // Atomic: child-row deletes + activity removal all commit together, so a mid-way
        // failure can't leave the activity row alive with its GPS/photos already gone.
        await using var tx = await _context.Database.BeginTransactionAsync();

        // Purge guard, INSIDE the transaction: the trash endpoint's trashed-set snapshot goes stale over
        // its (up to 5000-id) loop, and a ride restored in that window must survive. Re-reading here means
        // a restore either lands before this check (we skip) or conflicts with this tx's writes (WAL
        // busy-snapshot aborts us loudly) — never a silent purge of a restored ride (SVC-05 pattern).
        if (requireTrashed)
        {
            var lifecycle = await _context.Database.SqlQueryRaw<string>(
                "SELECT lifecycle AS Value FROM Activity WHERE track_ID = {0}", trackId.Value).FirstOrDefaultAsync();
            if (lifecycle != LifecycleStates.Trashed)
                return false;   // restore won the race — nothing written yet, tx rolls back empty
        }

        // Delete child rows first to respect foreign key constraints. The table list (and each
        // table's why — Item 25, ME-01, inval-09) is owned by ChildTableRegistry (CR5-05); its order
        // is FK-safe. A new track-keyed table registers there once and every delete seam picks it up.
        foreach (var child in ChildTableRegistry.TrackChildTables)
            await _context.Database.ExecuteSqlRawAsync(child.DeleteByKeySql, trackId.Value);

        _context.Activities.Remove(activity);
        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        // Versions are gone → reclaim any of THEIR objects that no version references any more. Objects are
        // shared content, so they die by refcount, never by track key (another activity may be the very same
        // bytes). The decode cache needs no eviction: it is keyed by hash, so a swept object simply stops
        // being asked for.
        if (_objects != null)
            await _objects.SweepUnreferencedAsync(objectShas);

        // Rows committed — purge the photo rendition files (best-effort; a failure just leaves an orphan).
        if (_photoStore != null)
            foreach (var id in photoIds)
                await _photoStore.DeleteAsync(new PhotoId(id));

        // Evict the owner's cached track coords / map slices so the deleted activity
        // stops rendering immediately instead of lingering until the sliding TTL.
        _invalidator.InvalidateUser(ownerId);

        _logger.LogInformation("Deleted activity {TrackId} with GPS data, pictures, and segment matches", trackId);
        return true;
    }
}
