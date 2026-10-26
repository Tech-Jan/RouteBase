using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Tracks;

/// <summary>
/// Reads a track's GPS points as a decoded, ordinal-indexed <see cref="DecodedTrack"/>, from cache
/// or storage. The seam that replaces per-consumer EF GPS materialization (segment matching, heatmap
/// painting, …) with one shared decode per track.
/// </summary>
public interface ITrackReader
{
    /// <summary>Decoded track ordered by GPS_ID, from cache or storage. Null when the track
    /// has no GPS points. No visibility checks — callers pre-authorize track ids
    /// (same contract as every existing GPS read: the GPS table has no user_id).</summary>
    Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default);
}
