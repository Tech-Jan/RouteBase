using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Services.Interfaces;

public interface IStravaImportOrchestrator
{
    /// <summary>Imports a single Strava activity (fetches streams + detail, then persists).</summary>
    Task<Activity?> ImportActivityAsync(StravaActivity stravaActivity, string? userId = null);

    /// <summary>Imports activities in parallel (3–4 concurrent fetches, serial persist). Faster wall-clock than the serial path.</summary>
    /// <param name="onActivityImported">Fired once per successfully persisted track with its fresh TrackId (never on skip/fail).</param>
    Task<(int imported, int skipped, int failed)> ImportActivitiesParallelAsync(
        IReadOnlyList<ExternalActivitySummary> activities,
        string userId,
        Action<int, string, int, int, int> progressCallback,
        Action<int> onActivityImported,
        CancellationToken ct,
        int maxConcurrency = 4);
}
