using RouteBaseWeb.Helpers;
using System.Security.Cryptography;
using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Services;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// Runs a staged photo batch import (Phase-1 §3–§5). Processes files <b>serially</b> — photos are ~1000×
/// heavier than a GPS point, so one-at-a-time bounds peak RAM (decode dominates) and makes in-batch dedup
/// race-free. Per file: hash + EXIF → dedup (D5) → resolve capture UTC → match (owned activities) → on a
/// single time match derive (SkiaSharp) + store files + persist the Photo row; otherwise leave the staged
/// file for the user to assign (ambiguous/unmatched) or keep (duplicate).
/// </summary>
public class PhotoImportOrchestrator
{
    private readonly RouteBaseDbContext _db;
    private readonly IVisibilityService _visibility;
    private readonly IActivityQueryService _activities;
    private readonly ITrackReader _trackReader;
    private readonly IActivityTimezoneResolver _tz;
    private readonly PhotoExifReader _exif;
    private readonly PhotoDerivativeService _derivatives;
    private readonly IPhotoImageStore _store;
    private readonly PhotoImportStaging _staging;
    private readonly ILogger<PhotoImportOrchestrator> _logger;

    private readonly TimeProvider _clock;

    public PhotoImportOrchestrator(
        RouteBaseDbContext db, IVisibilityService visibility, IActivityQueryService activities,
        ITrackReader trackReader, IActivityTimezoneResolver tz, PhotoExifReader exif,
        PhotoDerivativeService derivatives, IPhotoImageStore store, PhotoImportStaging staging,
        ILogger<PhotoImportOrchestrator> logger,
        TimeProvider clock)
    {
        _clock = clock;
        _db = db;
        _visibility = visibility;
        _activities = activities;
        _trackReader = trackReader;
        _tz = tz;
        _exif = exif;
        _derivatives = derivatives;
        _store = store;
        _staging = staging;
        _logger = logger;
    }

    public async Task ImportBatchAsync(
        string userId, string batchId,
        Action<string> onFileStarted,
        Action<PhotoFileResult> onFileCompleted,
        CancellationToken ct)
    {
        var batch = await _staging.LoadAsync(userId, batchId);
        if (batch == null)
        {
            _logger.LogError("Photo staging batch {BatchId} not found for user {UserId}", batchId, userId);
            return;
        }

        // Forced-target imports assign directly, so they don't need the candidate set.
        var candidates = batch.ForceTrackId is null
            ? await LoadCandidateMetaAsync(userId, ct)
            : Array.Empty<PhotoMatchActivity>();
        var batchHashes = new HashSet<string>(StringComparer.Ordinal);
        var batchTimes = new HashSet<DateTime>();

        foreach (var staged in batch.Files)
        {
            ct.ThrowIfCancellationRequested();
            onFileStarted(staged.OriginalName);

            try
            {
                var result = await ProcessFileAsync(
                    userId, batchId, staged, batch.PreviewMaxEdge, batch.ForceTrackId,
                    candidates, batchHashes, batchTimes, ct);
                onFileCompleted(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Photo import failed for {File}", staged.OriginalName);
                _staging.TryDeleteFile(userId, batchId, staged.StagedName); // unrecoverable → drop the staged file
                onFileCompleted(new PhotoFileResult(staged.OriginalName, "failed", ex.Message, null, null));
            }
        }
    }

    /// <summary>Outcome of assigning a staged (ambiguous/unmatched/duplicate) file to a user-chosen
    /// activity. <c>assigned</c> persists it; <c>duplicate</c> = D5 hit and not kept; <c>gone</c> = the
    /// staged file expired; <c>failed</c> = decode/store error.</summary>
    public sealed record AssignResult(string Outcome, int? PhotoId);

    /// <summary>Manually assign a staged file to <paramref name="trackId"/> (import-result "Assign…" and
    /// duplicate "keep anyway"). Reuses the same derive+store+persist path as auto-match, tagged
    /// <c>manual</c>. The caller pre-authorizes that the user may view/assign to the target activity.</summary>
    public async Task<AssignResult> AssignStagedAsync(
        string userId, string batchId, string stagedName, TrackId trackId, bool keepAnyway, CancellationToken ct)
    {
        var file = await _staging.GetFileAsync(userId, batchId, stagedName);
        if (file == null) return new AssignResult("gone", null);

        var batch = await _staging.LoadAsync(userId, batchId);
        int previewMaxEdge = batch?.PreviewMaxEdge ?? ProfileSettingsDefaults.PhotoPreviewMaxEdge;

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(file.StagedPath, ct); }
        catch { return new AssignResult("gone", null); }

#pragma warning disable CA1308 // photo content hash stored/compared as lowercase hex
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA1308
        PhotoExif exif;
        using (var ms = new MemoryStream(bytes, writable: false)) exif = _exif.Read(ms);
        var (capturedUtc, offsetHours) = await ResolveCapturedAsync(exif, userId);

        if (!keepAnyway &&
            await IsDuplicateAsync(userId, hash, capturedUtc, new(StringComparer.Ordinal), new(), ct))
            return new AssignResult("duplicate", null);

        var track = await LoadSamplesAsync(trackId, ct);
        var pos = track.Count > 0
            ? PhotoMatchService.ResolveAlongTrackPositionM(capturedUtc, exif.GpsLat, exif.GpsLng, track)
            : null;
        var (lat, lng, src) = PhotoMatchService.ResolveLocation(exif.HasGps, exif.GpsLat, exif.GpsLng, capturedUtc, track);

        try
        {
            var photoId = await PersistAsync(userId, trackId, pos, lat, lng, src, PhotoAssignments.Manual, exif, capturedUtc, offsetHours,
                hash, file.OriginalName, bytes, previewMaxEdge, ct);
            _staging.TryDeleteFile(userId, batchId, stagedName);
            return new AssignResult("assigned", photoId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assign failed for staged {File}", stagedName);
            return new AssignResult("failed", null);
        }
    }

    private async Task<PhotoFileResult> ProcessFileAsync(
        string userId, string batchId, PhotoImportStaging.StagedFile staged, int previewMaxEdge,
        int? forceTrackId, IReadOnlyList<PhotoMatchActivity> candidates,
        HashSet<string> batchHashes, HashSet<DateTime> batchTimes, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(staged.StagedPath, ct);
#pragma warning disable CA1308 // photo content hash stored/compared as lowercase hex
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA1308

        PhotoExif exif;
        using (var ms = new MemoryStream(bytes, writable: false))
            exif = _exif.Read(ms);

        var (capturedUtc, offsetHours) = await ResolveCapturedAsync(exif, userId);

        // D5 dedup: same user, same SHA-256 OR same captured_utc — vs. persisted + earlier-in-batch.
        if (await IsDuplicateAsync(userId, hash, capturedUtc, batchHashes, batchTimes, ct))
            return new PhotoFileResult(staged.OriginalName, "duplicate", null, staged.StagedName, null);

        // "Add photos to this activity" (§2): assign straight to the target, no matching.
        if (forceTrackId is { } tid)
        {
            var track = await LoadSamplesAsync(new TrackId(tid), ct);
            var pos = track.Count > 0
                ? PhotoMatchService.ResolveAlongTrackPositionM(capturedUtc, exif.GpsLat, exif.GpsLng, track)
                : null;
            var (lat, lng, src) = PhotoMatchService.ResolveLocation(exif.HasGps, exif.GpsLat, exif.GpsLng, capturedUtc, track);
            var forcedId = await PersistAsync(userId, new TrackId(tid), pos, lat, lng, src, PhotoAssignments.Manual, exif, capturedUtc, offsetHours,
                hash, staged.OriginalName, bytes, previewMaxEdge, ct);
            batchHashes.Add(hash);
            if (capturedUtc is { } fcu) batchTimes.Add(fcu);
            _staging.TryDeleteFile(userId, batchId, staged.StagedName);
            _logger.LogInformation("Imported photo {File} → activity {TrackId} (direct, photo {PhotoId})",
                staged.OriginalName, tid, forcedId);
            return new PhotoFileResult(staged.OriginalName, "imported", null, null, null);
        }

        var match = await MatchPhotoAsync(capturedUtc, exif.GpsLat, exif.GpsLng, candidates, ct);

        switch (match)
        {
            case PhotoMatchResult.AutoMatch am:
            {
                var track = await LoadSamplesAsync(new TrackId(am.TrackId), ct); // cache hit — loaded during matching
                var (lat, lng, src) = PhotoMatchService.ResolveLocation(exif.HasGps, exif.GpsLat, exif.GpsLng, capturedUtc, track);
                var photoId = await PersistAsync(userId, new TrackId(am.TrackId), am.TrackPositionM, lat, lng, src, PhotoAssignments.Auto, exif,
                    capturedUtc, offsetHours, hash, staged.OriginalName, bytes, previewMaxEdge, ct);
                batchHashes.Add(hash);
                if (capturedUtc is { } cu) batchTimes.Add(cu);
                _staging.TryDeleteFile(userId, batchId, staged.StagedName); // terminal → drop the staged original
                _logger.LogInformation("Imported photo {File} → activity {TrackId} (photo {PhotoId})",
                    staged.OriginalName, am.TrackId, photoId);
                return new PhotoFileResult(staged.OriginalName, "imported", null, null, null);
            }
            case PhotoMatchResult.Ambiguous amb:
                return new PhotoFileResult(staged.OriginalName, "ambiguous", null, staged.StagedName,
                    amb.Candidates.Select(ToDto).ToList());
            case PhotoMatchResult.GpsCandidates gps:
                return new PhotoFileResult(staged.OriginalName, "ambiguous", null, staged.StagedName,
                    gps.Candidates.Select(ToDto).ToList());
            default:
                return new PhotoFileResult(staged.OriginalName, "unmatched", null, staged.StagedName, null);
        }
    }

    // ── persistence (files-before-row would need the id up front; autoincrement forces row→files with
    //    compensation — a committed row with a missing file just shows the ThumbHash placeholder, §1b) ──
    private async Task<int> PersistAsync(
        string userId, TrackId trackId, double? trackPositionM, double? lat, double? lng, string locationSource,
        string assignment, PhotoExif exif, DateTime? capturedUtc, double? offsetHours, string hash,
        string origName, byte[] bytes, int previewMaxEdge, CancellationToken ct)
    {
        var d = _derivatives.Create(bytes, previewMaxEdge); // throws on undecodable → caller marks failed

        var photo = new Photo
        {
            UserId = userId,
            TrackId = trackId,
            Assignment = assignment,
            CapturedUtc = capturedUtc,
            TzOffset = offsetHours,
            Lat = lat,
            Lng = lng,
            LocationSource = locationSource,
            TrackPositionM = trackPositionM,
            OrigFilename = origName,
            ContentHash = hash,
            CameraModel = exif.CameraModel,
            HasExifTime = exif.HasTime,
            Thumbhash = d.ThumbHash,
            Storage = PhotoStorages.Local,
            Width = d.Width,
            Height = d.Height,
            ImportedUtc = _clock.GetUtcNow().UtcDateTime,
        };

        _db.Photos.Add(photo);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _store.PutAsync(photo.PhotoId, PhotoTier.Thumb, d.Thumb, ct);
            await _store.PutAsync(photo.PhotoId, PhotoTier.Preview, d.Preview, ct);
        }
        catch
        {
            // CancellationToken.None: when the failure IS a cancellation, compensating with the same
            // token would throw immediately and leave the row committed with no rendition files — a
            // permanent placeholder photo (the orphan sweep removes files without rows, never rows
            // without files). AS-03 precedent: compensation must run to completion.
            _db.Photos.Remove(photo);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        _db.Entry(photo).State = EntityState.Detached; // keep the ChangeTracker lean across the batch
        return photo.PhotoId.Value;
    }

    // ── matching (I/O around the pure PhotoMatchService) ──────────────────────
    private async Task<PhotoMatchResult> MatchPhotoAsync(
        DateTime? utc, double? lat, double? lng,
        IReadOnlyList<PhotoMatchActivity> candidates, CancellationToken ct)
    {
        var decision = PhotoMatchService.Match(utc, lat, lng, candidates);

        switch (decision)
        {
            case PhotoMatchResult.AutoMatch am:
            {
                var track = await LoadSamplesAsync(new TrackId(am.TrackId), ct);
                var pos = track.Count > 0 ? PhotoMatchService.ResolveAlongTrackPositionM(utc, lat, lng, track) : null;
                return new PhotoMatchResult.AutoMatch(am.TrackId, pos);
            }
            case PhotoMatchResult.Ambiguous amb when lat.HasValue && lng.HasValue:
            {
                var withTracks = new List<PhotoMatchActivity>();
                foreach (var c in amb.Candidates)
                {
                    var meta = candidates.First(m => m.TrackId == c.TrackId);
                    withTracks.Add(meta with { Track = await LoadSamplesAsync(new TrackId(c.TrackId), ct) });
                }
                return PhotoMatchService.Match(utc, lat, lng, withTracks); // re-run → distances annotated
            }
            case PhotoMatchResult.Unmatched when lat.HasValue && lng.HasValue:
            {
                var bbox = await BboxCandidatesAsync(lat.Value, lng.Value, candidates, ct);
                return bbox.Count == 0 ? decision : PhotoMatchService.Match(utc, lat, lng, bbox);
            }
            default:
                return decision;
        }
    }

    private async Task<IReadOnlyList<PhotoMatchActivity>> LoadCandidateMetaAsync(string userId, CancellationToken ct)
    {
        var rows = await _visibility.OwnedActivities(_db.Activities)
            .Where(a => a.StartDateTimeUtc != null)
            .Select(a => new { a.TrackId, a.TrackName, a.StartDateTimeUtc, a.StartDateTimeLocal, a.TimeElapsed })
            .ToListAsync(ct);

        return rows.Select(a =>
        {
            // The SQL WHERE above guarantees non-null; ??-throw re-proves it for the shaper.
            var rawUtc = a.StartDateTimeUtc ?? throw new InvalidOperationException("filtered to non-null above");
            var startUtc = DateTime.SpecifyKind(rawUtc, DateTimeKind.Utc);
            var endUtc = startUtc.AddSeconds(a.TimeElapsed ?? 0);
            double? tzOffsetHours = a.StartDateTimeLocal is { } loc
                ? (loc - a.StartDateTimeUtc.Value).TotalHours
                : null;
            return new PhotoMatchActivity(a.TrackId.Value, a.TrackName ?? $"Activity {a.TrackId}",
                startUtc, endUtc, tzOffsetHours, Track: null);
        }).ToList();
    }

    private async Task<IReadOnlyList<PhotoMatchActivity>> BboxCandidatesAsync(
        double lat, double lng, IReadOnlyList<PhotoMatchActivity> meta, CancellationToken ct)
    {
        var ids = await _activities.GetTrackIdsContainingPointAsync(lat, lng, ct);

        var byId = meta.ToDictionary(m => m.TrackId);
        var result = new List<PhotoMatchActivity>();
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var m)) continue; // not owned / not a candidate
            var track = await LoadSamplesAsync(new TrackId(id), ct);
            if (track.Count > 0) result.Add(m with { Track = track });
        }
        return result;
    }

    private Task<IReadOnlyList<TrackSample>> LoadSamplesAsync(TrackId trackId, CancellationToken ct) =>
        PhotoTrackSamples.LoadAsync(_trackReader, trackId, ct);

    // ── tz + dedup ────────────────────────────────────────────────────────────
    private async Task<(DateTime? utc, double? offsetHours)> ResolveCapturedAsync(PhotoExif exif, string userId)
    {
        if (!exif.HasTime) return (null, null);
        var local = exif.DateTimeOriginalLocal.Value;

        // (a) EXIF OffsetTimeOriginal — direct, no lookup (§3).
        if (exif.UtcOffset is { } off)
            return (DateTime.SpecifyKind(local - off, DateTimeKind.Utc), off.TotalHours);

        // (b) GPS-derived tz (offset at the capture instant).
        if (exif.HasGps)
        {
            var tzOff = await RefineGpsOffsetAsync(_tz, exif.GpsLat, exif.GpsLng, local, userId);
            if (tzOff is { } o)
                return (DateTime.SpecifyKind(local - o, DateTimeKind.Utc), o.TotalHours);
        }

        // (c) No resolvable tz → no capture UTC (won't time-match; lands unmatched for manual assign).
        return (null, null);
    }

    // Two-pass because we don't have a real UTC instant to probe with yet: the first pass's guess
    // is used to compute a much closer approximation, then re-probed — fixes DST-transition-date
    // misreads where the naive local-as-UTC guess can be off by up to the very offset it's solving for.
    internal static async Task<TimeSpan?> RefineGpsOffsetAsync(
        IActivityTimezoneResolver tz, double? lat, double? lng, DateTime local, string userId)
    {
        var guess = await tz.ResolveOffsetAsync(lat, lng, DateTime.SpecifyKind(local, DateTimeKind.Utc), userId);
        if (guess is not { } g) return null;
        var refined = await tz.ResolveOffsetAsync(lat, lng,
            DateTime.SpecifyKind(local - g, DateTimeKind.Utc), userId);
        return refined ?? g;
    }

    private async Task<bool> IsDuplicateAsync(
        string userId, string hash, DateTime? capturedUtc,
        HashSet<string> batchHashes, HashSet<DateTime> batchTimes, CancellationToken ct)
    {
        if (batchHashes.Contains(hash)) return true;
        if (capturedUtc is { } t0 && batchTimes.Contains(t0)) return true;

        var owned = _db.Photos.Where(p => p.UserId == userId);
        return capturedUtc is { } t
            ? await owned.AnyAsync(p => p.ContentHash == hash || p.CapturedUtc == t, ct)
            : await owned.AnyAsync(p => p.ContentHash == hash, ct);
    }

    private static PhotoCandidateDto ToDto(PhotoMatchCandidate c)
    {
        var startLocal = c.TzOffsetHours is { } off
            ? c.StartUtc.AddHours(off)
            : c.StartUtc;
        return new PhotoCandidateDto(c.TrackId, c.Name, startLocal.ToWallClockIso(), c.GpsDistanceM);
    }
}
