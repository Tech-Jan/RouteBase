using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Services;

/// <summary>
/// Thin facade implementing <see cref="IActivitySyncService"/> + <see cref="IParallelActivityImporter"/>
/// for Strava. Delegates fetching to <see cref="IStravaApiClient"/> and import to
/// <see cref="IStravaImportOrchestrator"/>. Auth is handled by <see cref="IStravaAuthService"/>
/// directly at the controller/page seam, not through this facade.
/// </summary>
public class StravaService : IActivitySyncService, IParallelActivityImporter
{
    private readonly IStravaApiClient _apiClient;
    private readonly IStravaImportOrchestrator _orchestrator;

    public StravaService(
        IStravaApiClient apiClient,
        IStravaImportOrchestrator orchestrator)
    {
        _apiClient   = apiClient;
        _orchestrator = orchestrator;
    }

    // ── IActivitySyncService ─────────────────────────────────────────────────

    public string ProviderId  => "strava";
    public string DisplayName => "Strava";

    public async Task<List<ExternalActivitySummary>> FetchAllActivitiesAsync(DateTime? after = null)
    {
        var activities = await _apiClient.GetAllActivitiesAsync(after);
        return activities.Select(a => StravaImportOrchestrator.MapToExternalSummary(a)).ToList();
    }

    async Task<Activity?> IActivitySyncService.ImportActivityAsync(ExternalActivitySummary externalActivity, string? userId)
    {
        if (externalActivity.RawData is not StravaActivity stravaActivity)
            return null;
        return await _orchestrator.ImportActivityAsync(stravaActivity, userId);
    }

    // ── IParallelActivityImporter ────────────────────────────────────────────

    public Task<(int imported, int skipped, int failed)> ImportActivitiesParallelAsync(
        IReadOnlyList<ExternalActivitySummary> activities,
        string userId,
        Action<int, string, int, int, int> progressCallback,
        Action<int> onActivityImported,
        CancellationToken ct)
        => _orchestrator.ImportActivitiesParallelAsync(
            activities, userId, progressCallback, onActivityImported, ct);
}
