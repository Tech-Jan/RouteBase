using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Write operations on activities. Only the activity owner may mutate or delete.
/// </summary>
public interface IActivityCommandService
{
    /// <summary>
    /// Update mutable fields on an activity. Returns false if the activity does
    /// not exist or is not owned by the current user.
    /// </summary>
    Task<bool> UpdateActivityAsync(TrackId trackId, string? name, string? activityType, string? visibility);

    /// <summary>
    /// Bulk-update mutable fields on many activities in one round-trip (one load, one save, one cache
    /// evict). Owner-scoped: ids not owned by the current user are silently skipped. Applies only the
    /// non-null fields to every matched row. Returns the number of activities actually changed.
    /// </summary>
    Task<int> UpdateManyAsync(IReadOnlyList<TrackId> trackIds, string? activityType, string? visibility);

    /// <summary>
    /// Delete an activity and all its associated GPS data, pictures, and segment matches.
    /// Returns false if the activity does not exist or is not owned by the current user.
    /// </summary>
    Task<bool> DeleteActivityAsync(TrackId trackId);

    /// <summary>
    /// Bulk soft-delete: moves many owned activities to the trash (reversible). Foreign/missing ids are
    /// silently skipped. Returns the number actually moved. Mirrors <see cref="DeleteActivityAsync"/>
    /// over the lifecycle service's batch transition.
    /// </summary>
    Task<int> DeleteManyAsync(IReadOnlyList<TrackId> trackIds);

    /// <summary>
    /// Permanent purge of an activity and all its child data. Owner-scoped: a no-op (returns false) if
    /// <paramref name="ownerId"/> does not match the activity's own owner, so a caller can never purge a
    /// row belonging to someone else even if it gets the trackId wrong. Background/non-HTTP callers
    /// (session cleanup, abandoned-session purge) pass the owner id they already read off the row they're
    /// purging — cache is invalidated against that same id, so the right owner's slices still get evicted.
    /// User-facing callers must ownership-check first — use <see cref="DeleteActivityAsync"/>. Returns
    /// false if no such activity, or if it belongs to a different owner.
    ///
    /// <paramref name="requireTrashed"/> re-asserts Lifecycle == trashed inside the delete's own
    /// transaction and refuses (false) otherwise. The trash purge passes true: its endpoint snapshots the
    /// trashed set once and then deletes in a loop, so a ride restored after the snapshot must be caught
    /// here, not by the stale snapshot. Callers that legitimately purge non-trashed rows (recording
    /// discard/finalize, session cleanup, edit revert) keep the default.
    /// </summary>
    Task<bool> DeleteActivityCoreAsync(TrackId trackId, string ownerId, bool requireTrashed = false);
}
