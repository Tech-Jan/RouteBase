using RouteBaseWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// Shared Activity name/tz lookups for the TrackEdit services (R-13). Replaces the three identical
/// TrackName dictionary queries (EditSessionService root-names + names, TrackEditRevertService names) and
/// folds GetSourcesAsync's separate tz + name queries into one projection.
/// </summary>
internal static class ActivityMetaQueries
{
    /// <summary>
    /// Is this ride CHECKED OUT into an open edit session? The one question every head-moving operation
    /// (revert, reset, restore) has to ask: an editor holding it has stamped the current head and replays its
    /// script against those bytes, so moving the head under it would either splice the edit onto geometry it
    /// never saw or fail at save with a stale-stamp 409 the user cannot explain.
    /// </summary>
    public static Task<bool> IsCheckedOutAsync(RouteBaseDbContext context, TrackId trackId, CancellationToken ct) =>
        context.Activities.AsNoTracking()
            .AnyAsync(a => a.TrackId == trackId && a.InEditSessionId != null, ct);

    /// <summary>Track id → name (null name kept) for the given ids; empty dictionary when none.</summary>
    public static async Task<IReadOnlyDictionary<TrackId, string?>> NamesAsync(
        RouteBaseDbContext context, IEnumerable<TrackId> ids, CancellationToken ct)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return new Dictionary<TrackId, string?>();
        return await context.Activities.AsNoTracking()
            .Where(a => idList.Contains(a.TrackId))
            .Select(a => new { a.TrackId, a.TrackName })
            .ToDictionaryAsync(a => a.TrackId, a => a.TrackName, ct);
    }

    /// <summary>Track id → (name, tz-offset hours) in one query (DateTimeTimezone null → 0).</summary>
    public static async Task<IReadOnlyDictionary<TrackId, (string? Name, double TzHours)>> NameAndTzAsync(
        RouteBaseDbContext context, IReadOnlyList<TrackId> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<TrackId, (string?, double)>();
        var rows = await context.Activities.AsNoTracking()
            .Where(a => ids.Contains(a.TrackId))
            .Select(a => new { a.TrackId, a.TrackName, a.DateTimeTimezone })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.TrackId, r => ((string?)r.TrackName, (double)(r.DateTimeTimezone ?? 0f)));
    }
}
