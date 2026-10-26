using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Photos;

/// <inheritdoc cref="IPhotoCommandService" />
public class PhotoCommandService : IPhotoCommandService
{
    private readonly RouteBaseDbContext _db;
    private readonly ITrackReader _trackReader;
    private readonly IPhotoImageStore _store;
    private readonly ICurrentUserService _currentUser;

    public PhotoCommandService(
        RouteBaseDbContext db, ITrackReader trackReader, IPhotoImageStore store,
        ICurrentUserService currentUser)
    {
        _db = db;
        _trackReader = trackReader;
        _store = store;
        _currentUser = currentUser;
    }

    /// <inheritdoc />
    public async Task<PhotoCommandOutcome> PlaceAsync(PhotoId photoId, double lat, double lng, CancellationToken ct = default)
    {
        var userId = _currentUser.GetUserId();
        var photo = await _db.Photos.FirstOrDefaultAsync(p => p.PhotoId == photoId && p.UserId == userId, ct);
        if (photo == null) return PhotoCommandOutcome.PhotoNotFound;

        photo.Lat = lat;
        photo.Lng = lng;
        photo.LocationSource = PhotoLocationSources.Manual;
        photo.OnMap = true;
        var track = await PhotoTrackSamples.LoadAsync(_trackReader, photo.TrackId, ct);
        if (track.Count > 0)
            photo.TrackPositionM = PhotoMatchService.ResolveAlongTrackPositionM(null, lat, lng, track);
        await _db.SaveChangesAsync(ct);
        return PhotoCommandOutcome.Ok;
    }

    /// <inheritdoc />
    public async Task<PhotoCommandOutcome> UnplaceAsync(PhotoId photoId, CancellationToken ct = default)
    {
        var userId = _currentUser.GetUserId();
        var photo = await _db.Photos.FirstOrDefaultAsync(p => p.PhotoId == photoId && p.UserId == userId, ct);
        if (photo == null) return PhotoCommandOutcome.PhotoNotFound;

        photo.OnMap = false; // keep lat/lng/source so it can be restored to the same spot
        await _db.SaveChangesAsync(ct);
        return PhotoCommandOutcome.Ok;
    }

    /// <inheritdoc />
    public async Task<RestoreResult> RestoreAsync(PhotoId photoId, CancellationToken ct = default)
    {
        var userId = _currentUser.GetUserId();
        var photo = await _db.Photos.FirstOrDefaultAsync(p => p.PhotoId == photoId && p.UserId == userId, ct);
        if (photo == null) return new RestoreResult(PhotoCommandOutcome.PhotoNotFound, null, null);
        if (photo.Lat is null || photo.Lng is null)
            return new RestoreResult(PhotoCommandOutcome.NoRememberedLocation, null, null);

        photo.OnMap = true;
        await _db.SaveChangesAsync(ct);
        return new RestoreResult(PhotoCommandOutcome.Ok, photo.Lat, photo.Lng);
    }

    /// <inheritdoc />
    public async Task<PhotoCommandOutcome> DeleteAsync(PhotoId photoId, CancellationToken ct = default)
    {
        var userId = _currentUser.GetUserId();
        var photo = await _db.Photos.FirstOrDefaultAsync(p => p.PhotoId == photoId && p.UserId == userId, ct);
        if (photo == null) return PhotoCommandOutcome.PhotoNotFound;

        _db.Photos.Remove(photo);
        await _db.SaveChangesAsync(ct);
        // Purge after the row committed → don't let a client disconnect abort it (would strand the files
        // until the orphan sweep). CancellationToken.None, matching the service-layer purges (AS-03).
        await _store.DeleteAsync(photoId, CancellationToken.None); // row gone → then files (a leftover file is a swept orphan)
        return PhotoCommandOutcome.Ok;
    }

    /// <inheritdoc />
    public async Task<int> DeleteManyAsync(IReadOnlyCollection<int> photoIds, CancellationToken ct = default)
    {
        var userId = _currentUser.GetUserId();
        var ids = photoIds.Select(i => new PhotoId(i)).ToList();
        var photos = await _db.Photos.Where(p => ids.Contains(p.PhotoId) && p.UserId == userId).ToListAsync(ct);
        if (photos.Count == 0) return 0;

        _db.Photos.RemoveRange(photos);
        await _db.SaveChangesAsync(ct);
        // CancellationToken.None: a mid-loop disconnect must not abort the purge after the rows committed (AS-03).
        foreach (var p in photos) await _store.DeleteAsync(p.PhotoId, CancellationToken.None);
        return photos.Count;
    }

    /// <inheritdoc />
    public async Task<int> RematchTrackPhotosAsync(TrackId trackId, CancellationToken ct = default)
    {
        var userId = _currentUser.GetUserId();
        var photos = await _db.Photos.Where(p => p.TrackId == trackId && p.UserId == userId).ToListAsync(ct);
        if (photos.Count == 0) return 0;

        var track = await PhotoTrackSamples.LoadAsync(_trackReader, trackId, ct);
        foreach (var photo in photos)
        {
            // Manual/exif locations are activity-independent → kept; interpolated/none re-resolve;
            // TrackPositionM recomputes for all.
            if (photo.LocationSource is not (PhotoLocationSources.Manual or PhotoLocationSources.Exif))
            {
                var (lat, lng, src) = PhotoMatchService.ResolveLocation(false, null, null, AsUtc(photo.CapturedUtc), track);
                photo.Lat = lat; photo.Lng = lng; photo.LocationSource = src;
            }
            photo.TrackPositionM = track.Count > 0
                ? PhotoMatchService.ResolveAlongTrackPositionM(AsUtc(photo.CapturedUtc), photo.Lat, photo.Lng, track)
                : null;
        }
        await _db.SaveChangesAsync(ct);
        return photos.Count;
    }

    private static DateTime? AsUtc(DateTime? dt) =>
        dt is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
}
