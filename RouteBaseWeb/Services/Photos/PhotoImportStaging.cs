using RouteBaseWeb.Services.Uploads;

namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// Per-batch temp file staging for background photo import — a thin typed wrapper over the shared
/// <see cref="StagedBatchStore{TManifest}"/> (CR5-02, superseding the Photos-plan "mirrored, not shared"
/// decision), which owns the manifest lock, atomic writes, commit-race semantics, and orphan sweep.
/// Photo-specific here: the batch-level <c>previewMaxEdge</c> (§1a), the "add photos to this activity"
/// <c>forceTrackId</c> (§2), and the per-file lookup/delete seams the assign flow needs. Files live at
/// <c>{TempPath}/routebase-photos/{userId}/{batchId}/</c> with a <c>manifest.json</c>. Ambiguous/unmatched
/// staged files survive here until the user assigns them (which reads + deletes the file) or the TTL
/// sweep removes the batch.
/// </summary>
public class PhotoImportStaging : IBatchUploadStaging
{
    private readonly StagedBatchStore<ManifestDto> _store;

    public PhotoImportStaging(ILogger<PhotoImportStaging> logger, TimeProvider clock)
    {
        _store = new StagedBatchStore<ManifestDto>(new StagedBatchStoreOptions
        {
            RootDirName = "routebase-photos",
            Label = "Photo",
            LoadRejectedInvalidIdLog = "Photo staging load rejected invalid batchId {BatchId}",
            ManifestNotFoundLog = null, // this vertical never warned on a missing manifest at load
            ReadManifestFailedLog = "Failed to read photo staging manifest for batch {BatchId}",
            DeleteBatchFailedLog = "Failed to delete photo staging dir for batch {BatchId}",
            OrphanCleanupFailedLog = "Photo orphan cleanup failed for {Dir}",
        }, logger, clock);
    }

    public sealed record StagedFile(string OriginalName, string StagedName, string StagedPath, long SizeBytes);

    public sealed record StagedBatch(
        string UserId, string BatchId, int PreviewMaxEdge, int? ForceTrackId,
        IReadOnlyList<StagedFile> Files, bool Committed);

    /// <summary>Create a new empty batch dir + manifest holding the chosen preview size and, for the
    /// "add photos to this activity" path (§2), an optional <paramref name="forceTrackId"/> that makes the
    /// import assign every photo straight to that activity (skipping matching). Returns the batchId.</summary>
    public string BeginBatch(string userId, int previewMaxEdge, int? forceTrackId = null) =>
        _store.BeginBatch(userId, new ManifestDto(previewMaxEdge, new List<StagedManifestFile>(), forceTrackId));

    public bool BatchExists(string userId, string batchId) => _store.BatchExists(userId, batchId);

    /// <summary>Append a chunk of files to an open batch. Returns the count staged.</summary>
    public Task<int> AppendFilesAsync(
        string userId, string batchId, IEnumerable<IFormFile> files, CancellationToken ct) =>
        _store.AppendFilesAsync(userId, batchId, files, ct);

    /// <summary>Load the staged batch, or null if the dir/manifest is missing.</summary>
    public async Task<StagedBatch?> LoadAsync(string userId, string batchId)
    {
        var manifest = await _store.LoadManifestAsync(userId, batchId);
        if (manifest == null) return null;

        var batchDir = _store.GetBatchDir(userId, batchId);
        var files = manifest.Files
            .Select(f => new StagedFile(f.OriginalName, f.StagedName, Path.Combine(batchDir, f.StagedName), f.SizeBytes))
            .ToList();

        return new StagedBatch(userId, batchId, manifest.PreviewMaxEdge, manifest.ForceTrackId, files, manifest.Committed);
    }

    /// <summary>Lifecycle summary for the shared <see cref="BatchUploadCoordinator"/> (CR5-02).</summary>
    public async Task<StagedBatchInfo?> LoadInfoAsync(string userId, string batchId)
    {
        var batch = await LoadAsync(userId, batchId);
        return batch == null
            ? null
            : new StagedBatchInfo(batch.Committed, batch.Files.Count, batch.Files.Sum(f => f.SizeBytes));
    }

    /// <summary>Resolve one staged file's absolute path (used by the assign endpoint), or null if gone.</summary>
    public async Task<StagedFile?> GetFileAsync(string userId, string batchId, string stagedName)
    {
        var batch = await LoadAsync(userId, batchId);
        return batch?.Files.FirstOrDefault(f =>
            string.Equals(f.StagedName, stagedName, StringComparison.Ordinal));
    }

    /// <summary>Mark the batch committed (enqueued) so a late/duplicate abort can't delete a running import.</summary>
    public Task<bool> TryMarkCommittedAsync(string userId, string batchId) =>
        _store.TryMarkCommittedAsync(userId, batchId);

    /// <summary>Delete one staged file (terminal-outcome cleanup / after an assign). Safe on missing paths.</summary>
    public void TryDeleteFile(string userId, string batchId, string stagedName) =>
        _store.TryDeleteFile(userId, batchId, stagedName);

    /// <summary>Delete the whole batch dir. Safe on missing paths.</summary>
    public void Delete(string userId, string batchId) => _store.Delete(userId, batchId);

    /// <summary>Remove per-user staging batch dirs older than <paramref name="maxAge"/> (startup sweep).</summary>
    public void CleanupOrphans(TimeSpan maxAge) => _store.CleanupOrphans(maxAge);

    /// <summary>Test seam — see <see cref="StagedBatchStore{TManifest}.CleanupOrphansCore"/>.</summary>
    internal void CleanupOrphansCore(DateTime cutoff, Func<string, DateTime> createdUtc) =>
        _store.CleanupOrphansCore(cutoff, createdUtc);

    // On-disk manifest envelope — property names/positions are the serialized contract; existing
    // staged batches (≤24 h TTL) must keep deserializing across upgrades.
    private sealed record ManifestDto(int PreviewMaxEdge, List<StagedManifestFile> Files, int? ForceTrackId = null, bool Committed = false)
        : IStagedBatchManifest<ManifestDto>
    {
        public ManifestDto WithCommitted() => this with { Committed = true };
    }
}
