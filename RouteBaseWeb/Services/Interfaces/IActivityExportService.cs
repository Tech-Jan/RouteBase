using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Bulk export: streams a ZIP of per-activity GPX files for the caller's activities.
/// </summary>
public interface IActivityExportService
{
    /// <summary>
    /// Writes a ZIP (one GPX entry per activity, plus a <c>manifest.json</c> entry) to <paramref name="output"/>,
    /// streamed so peak memory is roughly one activity. Only the caller's own activities are included;
    /// <paramref name="trackIds"/> null or empty means all of them. A requested id the caller doesn't own, or
    /// whose GPX export fails, is SKIPPED rather than turning the whole request into a 403 or a mysteriously
    /// partial ZIP (API-10) — the manifest records which ids landed and which didn't, and why.
    /// In-zip name collisions get a counter suffix.
    /// <paramref name="onProgress"/> is called with (done, total) after each activity, for the queued
    /// export's progress toast — a few hundred GPX files take long enough that silence reads as a hang.
    /// </summary>
    Task<BulkExportManifest> WriteAllAsZipAsync(
        Stream output, string userId, IReadOnlyList<int>? trackIds, CancellationToken ct,
        Action<int, int>? onProgress = null);
}

/// <summary>What a bulk export actually did — also serialized as the ZIP's own <c>manifest.json</c> entry,
/// so a client that only kept the file can still tell what it's holding.</summary>
public sealed record BulkExportManifest(
    IReadOnlyList<int> IncludedIds,
    IReadOnlyList<BulkExportSkip> Skipped);

/// <summary>One requested id that didn't make it into the ZIP, and why.</summary>
public sealed record BulkExportSkip(int TrackId, string Reason)
{
    public const string NotOwned = "not_owned";
    public const string ExportFailed = "export_failed";
}
