using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Lifecycle;

/// <summary>One row of the trash list. <paramref name="MergedIntoName"/> is non-null only for a ride a merge
/// consumed AND whose merge product still exists — that is what makes purging it destructive.</summary>
public sealed record TrashedActivity(
    int TrackId, string? Name, string? ActivityType, string? StartedIso,
    double? DistanceMeters, double? AscentMeters,
    string Reason, DateTimeOffset? TrashedAt, string RestoresTo,
    int? MergedIntoId, string? MergedIntoName);

/// <summary>
/// Reads of the trash. Owner-scoped, read-only — every write goes through <see cref="IActivityLifecycleService"/>,
/// which is the sole writer of the lifecycle columns.
/// </summary>
public sealed class TrashQueryService(RouteBaseDbContext context, IVisibilityService visibility)
{
    private readonly RouteBaseDbContext _context = context;
    private readonly IVisibilityService _visibility = visibility;

    /// <summary>
    /// Everything in the user's trash, as activities, most recently thrown away first. The Trash page renders
    /// the same <c>_ActivityCard</c> the dashboard does, and that partial takes the entity — so the slice needs
    /// the rows themselves, not just the <see cref="TrashedActivity"/> summary.
    /// </summary>
    public IQueryable<Activity> TrashedActivities() =>
        _visibility.OwnedActivities(_context.Activities)
            .Where(a => a.Lifecycle == LifecycleStates.Trashed)
            .OrderByDescending(a => a.LifecycleAt)
            .ThenByDescending(a => a.TrackId);

    /// <summary>Is this one of the caller's own trashed rides? The gate for any trash-scoped read.</summary>
    public Task<bool> IsTrashedAsync(TrackId trackId, CancellationToken ct = default) =>
        TrashedActivities().AnyAsync(a => a.TrackId == trackId, ct);

    /// <summary>Just the ids in the user's trash — the purge gate wants a set, not a rendering.</summary>
    public Task<List<TrackId>> TrashedIdsAsync(CancellationToken ct = default) =>
        TrashedActivities().Select(a => a.TrackId).ToListAsync(ct);

    /// <summary>Which of these ids are actually in the caller's trash (the purge scope gate).</summary>
    public Task<List<TrackId>> TrashedIdsAmongAsync(IReadOnlyList<TrackId> trackIds, CancellationToken ct = default) =>
        TrashedActivities().Where(a => trackIds.Contains(a.TrackId)).Select(a => a.TrackId).ToListAsync(ct);

    /// <summary>
    /// The user's trash, most recently thrown away first. This is the RENDERING projection — it carries a
    /// correlated sub-select per row for the merge-product name — so it belongs to the list endpoints only;
    /// anything that just needs ids takes <see cref="TrashedIdsAsync"/> instead.
    /// </summary>
    public async Task<IReadOnlyList<TrashedActivity>> ListAsync(CancellationToken ct = default)
    {
        // Left-join the merge product by superseded_by. It is a real FK with ON DELETE SET NULL, so a NULL
        // here means "nothing supersedes this any more" — either it was never merged, or the merge product
        // has itself been purged, in which case purging this row destroys nothing that still exists.
        var rows = await TrashedActivities()
            .Select(a => new
            {
                a.TrackId, a.TrackName, a.ActivityType, a.StartDateTimeLocal,
                a.TotalDistance, a.TotalAscent, a.LifecycleReason, a.LifecycleAt, a.LifecyclePrev,
                a.SupersededBy,
                MergedIntoName = _context.Activities
                    .Where(m => m.TrackId == a.SupersededBy).Select(m => m.TrackName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        // Two different kinds of time, so two different treatments (CC-02):
        //   StartedIso is a WALL CLOCK — the ride's own local day (the -Iso terminal suffix is the
        //   AR-02 wall-clock convention; E2 renamed it off startedAt, which is an INSTANT everywhere
        //   else on the wire). Ship the local value, exactly as
        //   ActivityRowDto.StartLocal does, so /Activities and /Activities/Trash cannot disagree by a day.
        //   TrashedAt is an INSTANT. SQLite hands it back Kind=Unspecified, which STJ serializes without a
        //   'Z', and the browser then reads as local. Stamp Utc so it carries the suffix and renders in the
        //   reader's zone.
        return rows.Select(a => new TrashedActivity(
            a.TrackId.Value, a.TrackName, a.ActivityType, a.StartDateTimeLocal.ToWallClockIso(),
            a.TotalDistance, a.TotalAscent,
            a.LifecycleReason ?? LifecycleReasons.User,
            a.LifecycleAt.ToUtcOffset(),
            // Restore returns it where it came from; a row with no recorded prev goes back into circulation.
            LifecycleStates.IsValid(a.LifecyclePrev) ? a.LifecyclePrev : LifecycleStates.Released,
            a.SupersededBy?.Value, a.MergedIntoName)).ToList();
    }

    /// <summary>
    /// The ids in the trash whose purge is IRREVERSIBLE in a way the user must be told about: a merge's
    /// source, while the merged ride still exists. Purging one of these means that merge can never be undone
    /// (invariant 4) — the un-merge is possible exactly while its inputs exist, and losing it must be an
    /// informed choice, not a side-effect of emptying the trash.
    /// </summary>
    public async Task<IReadOnlyList<TrackId>> MergeSourcesAmongAsync(
        IReadOnlyList<TrackId> trackIds, CancellationToken ct = default) =>
        await _visibility.OwnedActivities(_context.Activities)
            .Where(a => a.Lifecycle == LifecycleStates.Trashed
                     && trackIds.Contains(a.TrackId)
                     && a.SupersededBy != null)
            .Select(a => a.TrackId)
            .ToListAsync(ct);
}
