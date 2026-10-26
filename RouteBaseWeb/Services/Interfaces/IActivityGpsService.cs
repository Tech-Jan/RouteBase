using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Access to raw GPS data for activities. All methods verify visibility before
/// returning data — the GPS table has no user_id column of its own.
/// </summary>
public interface IActivityGpsService
{
    /// <summary>
    /// Full GPS rows for a single track (all points, ordered by GPS id).
    /// </summary>
    Task<List<GpsData>> GetGpsDataForTrackAsync(TrackId trackId, CancellationToken ct = default);

    /// <summary>
    /// Full GPS rows for a track the caller OWNS, at any lifecycle state. Same relationship to
    /// <see cref="GetGpsDataForTrackAsync"/> as <see cref="GetOwnedTrackCoordinatesAsync"/> has to the
    /// coordinate reads: ownership is the whole gate, and the caller decides which states it will serve.
    /// </summary>
    Task<List<GpsData>> GetOwnedGpsDataForTrackAsync(TrackId trackId, CancellationToken ct = default);

    /// <summary>
    /// Lat/lng coordinates for a single track at full resolution, with caching.
    /// </summary>
    Task<List<GpsCoordinate>> GetTrackCoordinatesAsync(TrackId trackId, CancellationToken ct = default);

    /// <summary>
    /// Lat/lng coordinates for a single track downsampled to at most
    /// <paramref name="maxPoints"/> points. Downsampling is pushed into SQLite.
    /// </summary>
    Task<List<GpsCoordinate>> GetTrackCoordinatesAsync(TrackId trackId, int maxPoints, CancellationToken ct = default);

    /// <summary>
    /// Lat/lng for a track the caller OWNS, at any lifecycle state — the visible-gated reads above see only
    /// released activities, so a trashed ride reads empty through them. This is the read the trash slice needs
    /// to draw its minimaps; ownership is the whole gate, so callers must add their own state check.
    /// </summary>
    Task<List<GpsCoordinate>> GetOwnedTrackCoordinatesAsync(TrackId trackId, int maxPoints, CancellationToken ct = default);

    /// <summary>
    /// Bulk-fetch downsampled GPS coordinates for multiple activities in a single query.
    /// Returns a dictionary keyed by TrackId. Only loads lat/lng columns.
    /// </summary>
    Task<Dictionary<int, List<GpsCoordinate>>> GetBulkTrackCoordinatesAsync(
        List<int> trackIds, int maxPointsPerTrack, CancellationToken ct = default);
}
