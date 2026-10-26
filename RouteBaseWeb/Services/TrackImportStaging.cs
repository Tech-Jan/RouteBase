using RouteBaseWeb.Services.Uploads;

namespace RouteBaseWeb.Services;

/// <summary>
/// Per-batch temporary file staging for background activity-file import — a thin typed wrapper over the
/// shared <see cref="StagedBatchStore{TManifest}"/> (CR5-02), which owns the manifest lock, atomic
/// writes, commit-race semantics, and orphan sweep. Track-specific here: the <c>useFilenameAsId</c>
/// batch flag and the manifest envelope (property names/positions = on-disk back-compat contract).
/// Files live at <c>{TempPath}/routebase-import/{userId}/{batchId}/</c> with a <c>manifest.json</c>.
/// </summary>
public class TrackImportStaging : IBatchUploadStaging
{
    private readonly StagedBatchStore<ManifestDto> _store;

    public TrackImportStaging(ILogger<TrackImportStaging> logger, TimeProvider clock)
    {
        _store = new StagedBatchStore<ManifestDto>(new StagedBatchStoreOptions
        {
            RootDirName = "routebase-import",
            Label = "GPX",
            LoadRejectedInvalidIdLog = "GPX staging load rejected invalid batchId {BatchId}",
            ManifestNotFoundLog = "GPX staging manifest not found for batch {BatchId}",
            ReadManifestFailedLog = "Failed to read GPX staging manifest for batch {BatchId}",
            DeleteBatchFailedLog = "Failed to delete GPX staging dir for batch {BatchId}",
            OrphanCleanupFailedLog = "Orphan cleanup failed for {Dir}",
        }, logger, clock);
    }

    public sealed record StagedFile(string OriginalName, string StagedPath, long SizeBytes);

    public sealed record StagedBatch(
        string UserId,
        string BatchId,
        bool UseFilenameAsId,
        IReadOnlyList<StagedFile> Files,
        bool Committed);

    /// <summary>
    /// Creates a new, empty per-batch temp directory with a manifest holding no files yet.
    /// Files are added afterwards in chunks via <see cref="AppendFilesAsync"/>, then the
    /// batch is enqueued. Returns the new batchId (a GUID string).
    /// </summary>
    public string BeginBatch(string userId, bool useFilenameAsId) =>
        _store.BeginBatch(userId, new ManifestDto(useFilenameAsId, new List<StagedManifestFile>()));

    /// <summary>Returns true if the batch directory and its manifest exist for the user.</summary>
    public bool BatchExists(string userId, string batchId) => _store.BatchExists(userId, batchId);

    /// <summary>
    /// Appends a chunk of <paramref name="files"/> to an existing staged batch and updates its
    /// manifest. Returns the number of files staged. Throws <see cref="DirectoryNotFoundException"/>
    /// if the batch (manifest) does not exist.
    /// </summary>
    public Task<int> AppendFilesAsync(
        string userId, string batchId, IEnumerable<IFormFile> files, CancellationToken ct) =>
        _store.AppendFilesAsync(userId, batchId, files, ct);

    /// <summary>
    /// Loads the staged batch from disk. Returns null if the batch directory or manifest is missing.
    /// </summary>
    public async Task<StagedBatch?> LoadAsync(string userId, string batchId)
    {
        var manifest = await _store.LoadManifestAsync(userId, batchId);
        if (manifest == null) return null;

        var batchDir = _store.GetBatchDir(userId, batchId);
        var files = manifest.Files
            .Select(f => new StagedFile(f.OriginalName, Path.Combine(batchDir, f.StagedName), f.SizeBytes))
            .ToList();

        return new StagedBatch(userId, batchId, manifest.UseFilenameAsId, files, manifest.Committed);
    }

    /// <summary>Lifecycle summary for the shared <see cref="BatchUploadCoordinator"/> (CR5-02).</summary>
    public async Task<StagedBatchInfo?> LoadInfoAsync(string userId, string batchId)
    {
        var batch = await LoadAsync(userId, batchId);
        return batch == null
            ? null
            : new StagedBatchInfo(batch.Committed, batch.Files.Count, batch.Files.Sum(f => f.SizeBytes));
    }

    /// <summary>
    /// Marks a staged batch as committed (enqueued for import) so a late/duplicate abort can't
    /// delete a running import's files or release its reservation. Returns false if the batch is gone.
    /// </summary>
    public Task<bool> TryMarkCommittedAsync(string userId, string batchId) =>
        _store.TryMarkCommittedAsync(userId, batchId);

    /// <summary>Deletes the batch directory. Safe to call on missing paths.</summary>
    public void Delete(string userId, string batchId) => _store.Delete(userId, batchId);

    /// <summary>
    /// Removes all per-user staging directories older than <paramref name="maxAge"/>.
    /// Call at application startup to clean up orphans from crashes/restarts.
    /// </summary>
    public void CleanupOrphans(TimeSpan maxAge) => _store.CleanupOrphans(maxAge);

    /// <summary>Test seam — see <see cref="StagedBatchStore{TManifest}.CleanupOrphansCore"/>.</summary>
    internal void CleanupOrphansCore(DateTime cutoff, Func<string, DateTime> createdUtc) =>
        _store.CleanupOrphansCore(cutoff, createdUtc);

    // On-disk manifest envelope — property names/positions are the serialized contract; existing
    // staged batches (≤24 h TTL) must keep deserializing across upgrades.
    // Committed is last with a default so manifests written before IQ-03 deserialize as false.
    private sealed record ManifestDto(bool UseFilenameAsId, List<StagedManifestFile> Files, bool Committed = false)
        : IStagedBatchManifest<ManifestDto>
    {
        public ManifestDto WithCommitted() => this with { Committed = true };
    }
}
