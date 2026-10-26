namespace RouteBaseWeb.Models.Photos;

/// <summary>
/// Whether a read may emit the owner-only fields (AS-05: camera model, original filename, off-map coords,
/// restore affordance).
///
/// Stated by the CALLER rather than inferred from the ambient current user, because the ambient user is not
/// always the human being served: <c>ShareLinkAccess.WidenAsync</c> impersonates the resource OWNER for the
/// duration of a share read, so a per-row <c>p.UserId == currentUser</c> comparison answers "owner" for an
/// anonymous visitor and hands them the owner's device model and home-directory paths.
/// </summary>
public enum PhotoFieldScope
{
    /// <summary>Per-row, gated on the row belonging to the ambient current user. The authenticated surface,
    /// where the ambient user IS the human making the request.</summary>
    OwnerWhenOwned,

    /// <summary>Never, whatever the ambient user says. Any surface reading under an impersonated identity —
    /// today the share slice.</summary>
    NeverOwner,
}

/// <summary>
/// One photo as the grid / Details panel / minimap consume it. Mirrors the TS <c>PhotoDto</c> — edit both
/// in the same commit. Image bytes are fetched from <c>/api/photos/{photoId}/image?tier=…&amp;v={V}</c>;
/// <c>V</c> is the content hash prefix that makes those URLs content-addressed (immutable caching).
/// </summary>
public sealed record PhotoDto(
    int PhotoId,
    int TrackId,
    string? TrackName,          // owning activity — for grouping / lightbox link
    DateTimeOffset? ActivityStartUtc,
    string? ThumbHashBase64,
    int Width,
    int Height,
    double? MarkerLat,          // stored location when shown on map (on_map); null = unplaced OR hidden
    double? MarkerLng,
    double? TrackPositionM,
    bool HasExifGps,
    [property: WireValues(typeof(PhotoLocationSources))]
    string? LocationSource,      // drives the placement/unpin UI
    bool CanRestore,             // off-map but has remembered coords → offer "show on map" instead of "place"
    DateTimeOffset? CapturedUtc,
    double? TzOffsetHours,
    string? CameraModel,
    string? OrigFilename,
    string? Description,
    string V);

/// <summary>Paginated list envelope for the Photos grid.</summary>
public sealed record PhotoListResponse(int Total, int Page, int PageSize, IReadOnlyList<PhotoDto> Items);

/// <summary>Slim marker for the Activities overview map overlay — only what a map pin/thumbnail needs.
/// Coordinates are the photo's single stored <c>lat</c>/<c>lng</c> (resolved at import), so this is a
/// plain indexed query — no track load. <c>LocationSource</c> lets the client mark estimated
/// (<c>interpolated</c>) points distinctly from exact (<c>exif</c>/<c>manual</c>). Mirrors the TS
/// <c>PhotoMarkerDto</c> — edit both in the same commit.</summary>
public sealed record PhotoMarkerDto(
    int PhotoId,
    int TrackId,
    double Lat,
    double Lng,
    [property: WireValues(typeof(PhotoLocationSources))] string? LocationSource,
    string? ThumbHashBase64,
    string V);

/// <summary>Envelope for the overview marker list.</summary>
public sealed record PhotoMarkersResponse(IReadOnlyList<PhotoMarkerDto> Items);

public sealed record AssignPhotoRequest(string BatchId, string StagedName, int TrackId, bool KeepAnyway = false);

/// <summary>Hand-place an unplaced photo's map marker at a clicked lat/lng (Details "place on map").</summary>
public sealed record PlacePhotoRequest(double Lat, double Lng);

public sealed record DeletePhotosRequest(List<int> PhotoIds);

/// <summary>Response of POST /api/photos/batch/begin — richer than ImportApiController's
/// BeginBatchResponse (Models/Uploads/BatchUploadContracts.cs): echoes the resolved preview size and,
/// when the "add photos to this activity" path was used, the forced track id.</summary>
public sealed record PhotoBeginBatchResponse(string? BatchId, int PreviewMaxEdge, int? TrackId);

/// <summary>Response of POST /api/photos/assign on success.</summary>
public sealed record AssignPhotoResponse(int? PhotoId, string Outcome);

/// <summary>
/// 409 body when POST /api/photos/assign finds the staged file is a duplicate. Carries more than the
/// sanctioned bare <c>{error}</c> convention (an outcome discriminator) — unread by the TS client
/// (pages/import.ts's assign() checks resp.ok only), named regardless.
/// </summary>
// Uniform error body (D32): `{ code, message, outcome }` (this body is always a 409).
public sealed record DuplicatePhotoConflictBody(string Message, string Outcome, string Code = ApiErrorCodes.Conflict);

/// <summary>Response of POST /api/photos/{id}/place and /{id}/restore on success. Lat/Lng are nullable
/// only because Restore's underlying result carries them that way (Services/Photos/IPhotoCommandService.cs
/// RestoreResult) — Place's own request fields are always non-null.</summary>
public sealed record PhotoLocationResponse(int PhotoId, double? Lat, double? Lng);

/// <summary>Response of POST /api/photos/delete.</summary>
public sealed record DeletedCountResponse(int Deleted);
