using RouteBaseWeb.Helpers;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Photos;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Photos;

/// <inheritdoc cref="IPhotoQueryService" />
public class PhotoQueryService : IPhotoQueryService
{
    private readonly RouteBaseDbContext _db;
    private readonly IVisibilityService _visibility;
    private readonly ICurrentUserService _currentUser;
    private readonly IPhotoImageStore _store;

    public PhotoQueryService(
        RouteBaseDbContext db, IVisibilityService visibility,
        ICurrentUserService currentUser, IPhotoImageStore store)
    {
        _db = db;
        _visibility = visibility;
        _currentUser = currentUser;
        _store = store;
    }

    /// <inheritdoc />
    public async Task<PhotoListResponse> ListAsync(
        TrackId? trackId, DateOnly? from, DateOnly? to, string sort, int page, int pageSize,
        PhotoFieldScope scope, CancellationToken ct = default)
    {
        var userId = _currentUser.GetUserId();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        IQueryable<Photo> q = _db.Photos.AsNoTracking();

        if (trackId is { } tid)
        {
            var owner = await _db.Photos.AsNoTracking().Where(p => p.TrackId == tid).Select(p => p.UserId).FirstOrDefaultAsync(ct);
            if (!await CanViewPhotoAsync(owner, tid, ct))
                return new PhotoListResponse(0, page, pageSize, Array.Empty<PhotoDto>());
            q = q.Where(p => p.TrackId == tid);
            // No track load needed — markers come from the stored lat/lng.
        }
        else
        {
            q = q.Where(p => p.UserId == userId);
            // The photo's OWN local day (see IPhotoQueryService): shift each row by its TzOffset and
            // compare against local-midnight bounds. AddHours translates to SQLite datetime() and
            // TzOffset can be fractional (e.g. +5.75). Null CapturedUtc never matches a filter.
            if (from is { } f)
            {
                var fromLocal = f.ToDateTime(TimeOnly.MinValue);
                q = q.Where(p => p.CapturedUtc != null
                    && p.CapturedUtc.Value.AddHours(p.TzOffset ?? 0) >= fromLocal);
            }
            if (to is { } t2)
            {
                var beforeNextLocal = t2.AddDays(1).ToDateTime(TimeOnly.MinValue); // inclusive `to`
                q = q.Where(p => p.CapturedUtc != null
                    && p.CapturedUtc.Value.AddHours(p.TzOffset ?? 0) < beforeNextLocal);
            }
        }

        // Grouping by activity orders the (activity start desc, then capture) so a group's photos are
        // contiguous; the client inserts a header when the activity changes. Default is flat by capture.
        q = sort == "activity"
            ? q.OrderByDescending(p => _db.Activities.Where(a => a.TrackId == p.TrackId)
                    .Select(a => (DateTime?)a.StartDateTimeUtc).FirstOrDefault())
                .ThenByDescending(p => p.CapturedUtc ?? p.ImportedUtc)
            : q.OrderByDescending(p => p.CapturedUtc ?? p.ImportedUtc);

        int total = await q.CountAsync(ct);
        var rows = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // Activity name/start for the page's photos (grouping headers + lightbox link).
        var ids = rows.Select(r => r.TrackId).Distinct().ToList();
        var acts = await _db.Activities.AsNoTracking()
            .Where(a => ids.Contains(a.TrackId))
            .Select(a => new { a.TrackId, a.TrackName, a.StartDateTimeUtc })
            .ToDictionaryAsync(a => a.TrackId, a => (a.TrackName, a.StartDateTimeUtc), ct);

        var items = rows.Select(p =>
        {
            acts.TryGetValue(p.TrackId, out var a);
            // Non-owners (?trackId path on a friend's activity) get the trimmed shape — AS-05. Under
            // NeverOwner the row comparison is skipped entirely: the ambient user is an impersonated
            // owner there, so it would answer "owner" for an anonymous share visitor.
            var isOwner = scope == PhotoFieldScope.OwnerWhenOwned && p.UserId == userId;
            return ToDto(p, a.TrackName, a.StartDateTimeUtc, isOwner);
        }).ToList();
        return new PhotoListResponse(total, page, pageSize, items);
    }

    /// <inheritdoc />
    public async Task<PhotoMarkersResponse> GetMapMarkersAsync(CancellationToken ct = default)
    {
        const int MaxMarkers = 5000;
        var userId = _currentUser.GetUserId();

        // Minimal column projection in SQL; V (content-hash prefix) + thumbhash base64 mapped in memory
        // (Convert.ToBase64String / string slicing don't translate to SQL — same split as ToDto).
        var rows = await _db.Photos.AsNoTracking()
            .Where(p => p.UserId == userId && p.OnMap && p.Lat != null && p.Lng != null)
            .OrderByDescending(p => p.CapturedUtc ?? p.ImportedUtc)
            .Take(MaxMarkers)
            .Select(p => new { p.PhotoId, p.TrackId, p.Lat, p.Lng, p.LocationSource, p.Thumbhash, p.ContentHash })
            .ToListAsync(ct);

        // Pattern-narrow lat/lng client-side: the SQL WHERE already excluded nulls, but the shaper
        // can't see that — the patterns re-prove it without a null-forgiving read.
        var items = new List<PhotoMarkerDto>(rows.Count);
        foreach (var p in rows)
        {
            if (p.Lat is not double lat || p.Lng is not double lng) continue;
            items.Add(new PhotoMarkerDto(
                p.PhotoId.Value, p.TrackId.Value,
                GpsPrecision.Coord(lat), GpsPrecision.Coord(lng), p.LocationSource,
                p.Thumbhash is { } th ? Convert.ToBase64String(th) : null,
                p.ContentHash is { Length: >= 8 } ch ? ch[..8] : (p.ContentHash ?? "")));
        }

        return new PhotoMarkersResponse(items);
    }

    /// <inheritdoc />
    public Task<bool> IsPhotoInActivityAsync(PhotoId photoId, TrackId trackId, CancellationToken ct = default) =>
        _db.Photos.AsNoTracking().AnyAsync(p => p.PhotoId == photoId && p.TrackId == trackId, ct);

    /// <inheritdoc />
    public async Task<string?> GetServableImagePathAsync(PhotoId photoId, PhotoTier tier, CancellationToken ct = default)
    {
        var photo = await _db.Photos.AsNoTracking()
            .Where(p => p.PhotoId == photoId)
            .Select(p => new { p.PhotoId, p.TrackId, p.UserId })
            .FirstOrDefaultAsync(ct);
        if (photo == null) return null;

        if (!await CanViewPhotoAsync(photo.UserId, photo.TrackId, ct)) return null;

        var path = _store.GetPhysicalPath(photoId, tier);
        return File.Exists(path) ? path : null;
    }

    private async Task<bool> CanViewPhotoAsync(string? photoOwner, TrackId trackId, CancellationToken ct = default)
    {
        if (photoOwner == _currentUser.GetUserId()) return true;
        // Non-owners clear the shared activity review+visibility gate (AS-01 / CR5-06) — one seam, not a
        // re-hand-rolled query. Pending/discarded auto-recordings stay owner-only.
        return await _visibility.CanViewActivityAsync(trackId);
    }

    /// <summary>Shape a photo for the wire. <paramref name="isOwner"/> gates owner-only fields (AS-05):
    /// non-owners never receive the camera model / original filename (device + home-directory leaks) and
    /// only get coordinates for photos still pinned on the map (<c>on_map</c>) — the unpin choice is a
    /// deliberate "hide the location", so an off-map photo's coords stay owner-only. Description is
    /// user-authored and kept for everyone; CanRestore is an owner-only affordance (restore is owner-gated).</summary>
    private static PhotoDto ToDto(Photo p, string? trackName = null, DateTime? activityStartUtc = null, bool isOwner = true)
    {
        // The marker is the single stored location — resolved once at write time, no track load here.
        // A hidden photo (on_map=false) reports no marker but stays restorable via CanRestore.
        string v = p.ContentHash is { Length: >= 8 } ch ? ch[..8] : (p.ContentHash ?? "");
        double? markerLat = p.OnMap ? GpsPrecision.Coord(p.Lat) : null;
        double? markerLng = p.OnMap ? GpsPrecision.Coord(p.Lng) : null;
        bool canRestore = isOwner && !p.OnMap && p.Lat != null && p.Lng != null;
        // ToUtcOffset, not an implicit cast: the DB round-trip strips DateTimeKind, and the implicit
        // conversion would read Unspecified as server-local (AR-02; parse guard in photos.ts).
        return new PhotoDto(
            p.PhotoId.Value, p.TrackId.Value, trackName, activityStartUtc.ToUtcOffset(),
            p.Thumbhash is { } th ? Convert.ToBase64String(th) : null,
            p.Width, p.Height, markerLat, markerLng, p.TrackPositionM, p.LocationSource == PhotoLocationSources.Exif, p.LocationSource,
            canRestore, p.CapturedUtc.ToUtcOffset(), p.TzOffset,
            isOwner ? p.CameraModel : null, isOwner ? p.OrigFilename : null, p.Description, v);
    }

}
