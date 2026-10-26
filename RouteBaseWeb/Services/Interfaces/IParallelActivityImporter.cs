using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Optional capability for sync providers that support parallel batch import.
/// The worker detects this interface at runtime and uses the fast path when available.
/// </summary>
public interface IParallelActivityImporter
{
    /// <param name="onActivityImported">
    /// Fired once per successfully persisted track with its fresh TrackId (never on skip/fail).
    /// The worker collects these to run the incremental per-import match after the batch drains.
    /// </param>
    Task<(int imported, int skipped, int failed)> ImportActivitiesParallelAsync(
        IReadOnlyList<ExternalActivitySummary> activities,
        string userId,
        Action<int, string, int, int, int> progressCallback,
        Action<int> onActivityImported,
        CancellationToken ct);
}
