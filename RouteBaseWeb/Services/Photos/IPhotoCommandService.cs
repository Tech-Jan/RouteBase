using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Photos;

/// <summary>Result of a photo mutation, mapped to HTTP status by the controller.</summary>
public enum PhotoCommandOutcome
{
    Ok,
    PhotoNotFound,
    NoRememberedLocation,   // restore with no stored lat/lng to snap back to
}

/// <summary>Restore also echoes the location it snapped back to.</summary>
public readonly record struct RestoreResult(PhotoCommandOutcome Outcome, double? Lat, double? Lng);

/// <summary>
/// The photo mutate surface for existing rows: place/unplace/restore and delete (row committed before
/// rendition files, AS-03). Staged-file assignment stays on <see cref="PhotoImportOrchestrator"/>; the
/// controller wires that path directly.
/// </summary>
public interface IPhotoCommandService
{
    Task<PhotoCommandOutcome> PlaceAsync(PhotoId photoId, double lat, double lng, CancellationToken ct = default);
    Task<PhotoCommandOutcome> UnplaceAsync(PhotoId photoId, CancellationToken ct = default);
    Task<RestoreResult> RestoreAsync(PhotoId photoId, CancellationToken ct = default);
    Task<PhotoCommandOutcome> DeleteAsync(PhotoId photoId, CancellationToken ct = default);

    /// <summary>Bulk-delete the caller's photos; ids the caller doesn't own are silently ignored.
    /// Returns the number of rows deleted (0 = nothing matched → 404 at the controller).</summary>
    Task<int> DeleteManyAsync(IReadOnlyCollection<int> photoIds, CancellationToken ct = default);

    /// <summary>Recompute location + along-track position for every one of the caller's photos on a track
    /// whose geometry changed (track edit): manual/exif locations are kept (activity-independent),
    /// track-derived ones re-resolve; TrackPositionM recomputes for all. Returns photos updated.</summary>
    Task<int> RematchTrackPhotosAsync(TrackId trackId, CancellationToken ct = default);
}
