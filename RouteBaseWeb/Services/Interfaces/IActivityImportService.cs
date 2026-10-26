using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Provider-agnostic activity import pipeline.
/// Accepts external activity summaries + GPS points and persists them to the database.
/// </summary>
public interface IActivityImportService
{
    /// <summary>
    /// Whether this file has already been imported — and if so, WHAT it is now. Blocking a re-import stays
    /// correct in every case (re-importing a ride you merged away would double-count its distance); only the
    /// message was lying, telling you "duplicate" about a ride sitting in your trash. Null = not imported.
    /// </summary>
    Task<ExistingActivity?> FindExistingAsync(long externalId, string source, string userId);

    /// <summary>
    /// Inserts the activity + GPS points in a single transaction for the given user.
    /// Computes total_descent from the altitude stream.
    /// Returns the new <see cref="Activity"/> (with TrackId populated) or null on failure.
    /// </summary>
    Task<Activity?> ImportFromSummaryAsync(ExternalActivitySummary summary, List<ExternalGpsPoint> gpsPoints, string userId);

    /// <summary>
    /// Packs a track's points into the Track_Blob store (sole import/finalize track-write funnel).
    /// </summary>
    Task WriteTrackBlobAsync(TrackId trackId, List<ExternalGpsPoint> points);

    /// <summary>
    /// Computes total descent in metres by summing all negative altitude deltas.
    /// </summary>
    double ComputeTotalDescent(List<ExternalGpsPoint> points);

    /// <summary>
    /// Expands (or inserts) the Activity_Bbox row from a point batch (value-based, skips null coords).
    /// </summary>
    Task UpsertActivityBboxAsync(TrackId trackId, IReadOnlyList<ExternalGpsPoint> points);

    /// <summary>
    /// Expands (or inserts) the Activity_Bbox row from the track's stored blob (no-op when it has none).
    /// </summary>
    Task UpsertActivityBboxAsync(TrackId trackId);
}
