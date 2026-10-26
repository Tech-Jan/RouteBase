using RouteBaseWeb.Models;
using System.IO.Compression;
using System.Text.Json;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <summary>
/// Streams a ZIP of per-activity GPX exports. Reuses <see cref="IGpxExportService"/> for each activity
/// and writes its bytes straight into a <see cref="ZipArchive"/> over the caller's output stream, so peak
/// memory is ≈ one activity (not the whole library). Scoped to the caller's own activities.
/// </summary>
public class ActivityExportService : IActivityExportService
{
    private readonly IActivityQueryService _activityQuery;
    private readonly IGpxExportService _gpx;
    private readonly ILogger<ActivityExportService> _logger;

    public ActivityExportService(
        IActivityQueryService activityQuery,
        IGpxExportService gpx,
        ILogger<ActivityExportService> logger)
    {
        _activityQuery = activityQuery;
        _gpx           = gpx;
        _logger        = logger;
    }

    public async Task<BulkExportManifest> WriteAllAsZipAsync(
        Stream output, string userId, IReadOnlyList<int>? trackIds, CancellationToken ct,
        Action<int, int>? onProgress = null)
    {
        var mine = (await _activityQuery.GetAllActivitiesAsync())
            .Where(a => a.UserId == userId)
            .ToList();
        var ownedIds = mine.Select(a => a.TrackId).ToHashSet();
        // Ride start per activity, for the zip entry timestamps below — derived from the pass we already
        // make for ownership, so it costs no extra query.
        var startLocal = mine
            .GroupBy(a => a.TrackId)
            .ToDictionary(g => g.Key, g => g.First().StartDateTimeLocal);

        var skipped = new List<BulkExportSkip>();
        List<TrackId> ids;
        if (trackIds is { Count: > 0 })
        {
            // A requested id the caller doesn't own is SKIPPED, not a 403 for the whole request and not a
            // silently smaller ZIP — the manifest records exactly which ids that were (API-10).
            ids = new List<TrackId>();
            foreach (var id in trackIds)
            {
                if (ownedIds.Contains(new TrackId(id))) ids.Add(new TrackId(id));
                else skipped.Add(new BulkExportSkip(id, BulkExportSkip.NotOwned));
            }
        }
        else
        {
            ids = ownedIds.ToList();
        }

        // leaveOpen: the caller owns the response stream; disposing the archive flushes the central
        // directory but must not close the HTTP response body.
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var included = new List<int>();

        // Reported per activity, INCLUDING the ones that fail to export — the toast is measuring how far
        // through the list we are, not how many entries landed.
        var processed = 0;
        onProgress?.Invoke(0, ids.Count);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            var export = await _gpx.ExportAsync(new TrackId(id.Value), ct);
            if (export is not { Stream: { } gpxStream, FileName: { } fileName })
            {
                skipped.Add(new BulkExportSkip(id.Value, BulkExportSkip.ExportFailed));
                onProgress?.Invoke(++processed, ids.Count);
                continue;
            }

            var entry = zip.CreateEntry(UniqueName(usedNames, fileName), CompressionLevel.Optimal);
            StampRideDate(entry, startLocal.GetValueOrDefault(id));
            await using (var entryStream = entry.Open())
                await gpxStream.CopyToAsync(entryStream, ct);
            await gpxStream.DisposeAsync();
            included.Add(id.Value);
            onProgress?.Invoke(++processed, ids.Count);
        }

        var manifest = new BulkExportManifest(included, skipped);
        await WriteManifestEntryAsync(zip, manifest, ct);

        _logger.LogInformation(
            "Activity export: wrote {Count} GPX entries ({SkippedCount} skipped) for user {UserId}",
            included.Count, skipped.Count, userId);
        return manifest;
    }

    /// <summary>
    /// Dates a zip entry at the ride instead of the export. Without this every extracted .gpx carries the
    /// day the ZIP was built (CreateEntry defaults LastWriteTime to now), which makes a decade of rides
    /// unsortable by date in a file manager.
    ///
    /// Wall-clock local, not the UTC instant: a zip timestamp is an MS-DOS field with no zone, read back as
    /// local time — so the file date matches the date the activities table shows. The zip epoch only spans
    /// 1980..2107 and the setter throws outside it, so an out-of-range or unknown start keeps the default.
    /// Note the format carries LAST-MODIFIED only; a "date created" is the extractor's own clock.
    /// </summary>
    private static void StampRideDate(ZipArchiveEntry entry, DateTime? startLocal)
    {
        if (startLocal is not { } t || t.Year < 1980 || t.Year > 2107) return;
        entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), TimeSpan.Zero);
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task WriteManifestEntryAsync(ZipArchive zip, BulkExportManifest manifest, CancellationToken ct)
    {
        var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, manifest, ManifestJsonOptions, ct);
    }

    /// <summary>Reserves a unique in-zip name, adding a counter suffix on collision (mirrors staging).</summary>
    private static string UniqueName(HashSet<string> used, string fileName)
    {
        if (used.Add(fileName)) return fileName;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext      = Path.GetExtension(fileName);
        for (int n = 1; ; n++)
        {
            var candidate = $"{baseName}_{n}{ext}";
            if (used.Add(candidate)) return candidate;
        }
    }
}
