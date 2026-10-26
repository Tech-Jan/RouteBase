using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Unified interface for syncing activities from external providers (Strava, Garmin Connect, etc.).
/// Shrunk (SV-05) to what <see cref="OperationQueueWorker"/> actually consumes; a future provider
/// re-adds only the auth/fetch members it needs (ISP — the old OAuth-shaped members were Strava-specific).
/// </summary>
public interface IActivitySyncService
{
    /// <summary>
    /// Unique identifier for this sync provider (e.g., "strava", "garmin").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Human-readable display name (e.g., "Strava", "Garmin Connect").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Fetch ALL activities from the provider (handles pagination automatically).
    /// </summary>
    /// <param name="after">Only fetch activities after this date.</param>
    Task<List<ExternalActivitySummary>> FetchAllActivitiesAsync(DateTime? after = null);

    /// <summary>
    /// Import a single activity (metadata + GPS data) into the local database.
    /// </summary>
    /// <returns>The imported Activity, or null if it already exists or failed.</returns>
    Task<Activity?> ImportActivityAsync(ExternalActivitySummary externalActivity, string? userId = null);
}
