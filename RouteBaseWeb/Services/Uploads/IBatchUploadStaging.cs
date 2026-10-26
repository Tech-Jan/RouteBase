namespace RouteBaseWeb.Services.Uploads;

/// <summary>Lifecycle summary of a staged batch — all the coordinator needs for its
/// begin/append/commit/abort sequencing; feature manifests stay behind their typed wrappers.</summary>
public sealed record StagedBatchInfo(bool Committed, int FileCount, long TotalBytes);

/// <summary>
/// The staging surface <see cref="BatchUploadCoordinator"/> drives (CR5-02) — implemented by both
/// typed wrappers (TrackImportStaging, PhotoImportStaging) over <see cref="StagedBatchStore{TManifest}"/>.
/// BeginBatch is deliberately absent: its parameters are feature-specific, so the coordinator takes
/// it as a delegate.
/// </summary>
public interface IBatchUploadStaging
{
    /// <summary>Loads the batch's lifecycle summary, or null if the dir/manifest is missing or unreadable.</summary>
    Task<StagedBatchInfo?> LoadInfoAsync(string userId, string batchId);

    /// <summary>Appends a chunk to an open batch; throws <see cref="InvalidOperationException"/> if it
    /// raced a commit (in-lock committed guard, IE-06 ↔ PI-05).</summary>
    Task<int> AppendFilesAsync(string userId, string batchId, IEnumerable<IFormFile> files, CancellationToken ct);

    /// <summary>True only for the call that performed the uncommitted→committed transition (IE-01 ↔ PI-04).</summary>
    Task<bool> TryMarkCommittedAsync(string userId, string batchId);

    /// <summary>Deletes the batch directory. Safe on missing paths.</summary>
    void Delete(string userId, string batchId);
}
