using System.Text.Json;

namespace RouteBaseWeb.Services.Uploads;

/// <summary>One staged file's manifest row. Property names/positions are the on-disk JSON contract
/// shared by both upload verticals — do not reorder/rename (existing manifests must keep deserializing).
/// SizeBytes last with a default so manifests written before IE-05/PI-03 deserialize (undercount 0
/// acceptable — batches are ephemeral ≤24 h).</summary>
public sealed record StagedManifestFile(string OriginalName, string StagedName, long SizeBytes = 0);

/// <summary>
/// The feature manifest contract for <see cref="StagedBatchStore{TManifest}"/>. Each vertical keeps its
/// own manifest record (feature fields + exact serialized envelope for on-disk back-compat); the store
/// only needs the committed flag, the file list, and a committed-copy factory.
/// </summary>
public interface IStagedBatchManifest<TSelf> where TSelf : IStagedBatchManifest<TSelf>
{
    bool Committed { get; }
    List<StagedManifestFile> Files { get; }
    TSelf WithCommitted();
}

/// <summary>Per-feature strings for <see cref="StagedBatchStore{TManifest}"/>: the temp root dir name,
/// the exception-message label, and the exact log templates (preserved verbatim per vertical).
/// A null <see cref="ManifestNotFoundLog"/> disables that warning (the photo vertical never logged it).</summary>
public sealed record StagedBatchStoreOptions
{
    public required string RootDirName { get; init; }
    public required string Label { get; init; }
    public required string LoadRejectedInvalidIdLog { get; init; }
    public string? ManifestNotFoundLog { get; init; }
    public required string ReadManifestFailedLog { get; init; }
    public required string DeleteBatchFailedLog { get; init; }
    public required string OrphanCleanupFailedLog { get; init; }
}

/// <summary>
/// Shared per-batch temp-file staging behavior for chunked batch uploads (CR5-02) — the mechanism that
/// was twin-maintained in TrackImportStaging ↔ PhotoImportStaging. Owns the directory layout
/// <c>{TempPath}/{root}/{userId}/{batchId}</c>, batchId validation (Guid "N" — rejects path traversal,
/// GI-04), userId path sanitization, and every twin-fixed manifest invariant:
/// <list type="bullet">
/// <item>manifest read-modify-write under one lock so concurrent chunk appends (or a racing commit)
/// can't clobber each other's writes (GI-11 ↔ photo twin);</item>
/// <item>in-lock committed guard on append — a late chunk must not add files to a running import
/// (IE-06 ↔ PI-05);</item>
/// <item>commit-race re-check under the lock — TryMarkCommittedAsync returns true only for the call that
/// performed the uncommitted→committed transition, so a losing commit/abort never deletes a running
/// import's files or releases its reservation (IE-01 ↔ PI-04);</item>
/// <item>atomic manifest writes: serialize to a sibling .tmp then rename over the target, so lock-free
/// readers see old-or-new, never torn (IE-02 ↔ PI-08);</item>
/// <item>collision suffixing probes the disk (not the manifest count) so a suffixed candidate can't
/// itself clobber a prior one (GI-05 ↔ photo twin).</item>
/// </list>
/// One lock per store instance is fine: staging ops are short and per-user rates are tiny.
/// </summary>
public sealed class StagedBatchStore<TManifest> where TManifest : class, IStagedBatchManifest<TManifest>
{
    private readonly string _rootDir;
    private readonly StagedBatchStoreOptions _opts;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    private readonly TimeProvider _clock;

    public StagedBatchStore(StagedBatchStoreOptions options, ILogger logger, TimeProvider clock)
    {
        _clock = clock;
        _opts = options;
        _rootDir = Path.Combine(Path.GetTempPath(), options.RootDirName);
        _logger = logger;
    }

    /// <summary>Creates a new empty batch dir and writes <paramref name="emptyManifest"/>
    /// (the feature's manifest with no files). Returns the new batchId (a GUID "N" string).</summary>
    public string BeginBatch(string userId, TManifest emptyManifest)
    {
        var batchId = Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        var batchDir = GetBatchDir(userId, batchId);
        Directory.CreateDirectory(batchDir);

        WriteManifestAtomic(GetManifestPath(batchDir), JsonSerializer.Serialize(emptyManifest));
        return batchId;
    }

    /// <summary>Returns true if the batch directory and its manifest exist for the user.</summary>
    public bool BatchExists(string userId, string batchId) =>
        IsValidBatchId(batchId) && File.Exists(GetManifestPath(GetBatchDir(userId, batchId)));

    /// <summary>
    /// Appends a chunk of <paramref name="files"/> to an existing staged batch and updates its manifest.
    /// Returns the number of files staged. Throws <see cref="DirectoryNotFoundException"/> if the batch
    /// (manifest) does not exist, <see cref="InvalidOperationException"/> if it is already committed.
    /// </summary>
    public async Task<int> AppendFilesAsync(
        string userId, string batchId, IEnumerable<IFormFile> files, CancellationToken ct)
    {
        if (!IsValidBatchId(batchId))
            throw new DirectoryNotFoundException($"{_opts.Label} staging batch {batchId} not found.");

        var batchDir = GetBatchDir(userId, batchId);
        var manifestPath = GetManifestPath(batchDir);
        if (!File.Exists(manifestPath))
            throw new DirectoryNotFoundException($"{_opts.Label} staging batch {batchId} not found.");

        // Read-modify-write the manifest under the lock (GI-11) so concurrent chunk appends
        // (or a racing TryMarkCommittedAsync) can't clobber each other's writes.
        await _manifestLock.WaitAsync(ct);
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, ct);
            var manifest = JsonSerializer.Deserialize<TManifest>(json)
                ?? throw new InvalidOperationException($"{_opts.Label} staging manifest for batch {batchId} is unreadable.");

            // In-lock guard (IE-06/PI-05): a commit can mark the manifest committed between the
            // controller's pre-check and this write; a late chunk must not add files to a running import.
            if (manifest.Committed)
                throw new InvalidOperationException($"{_opts.Label} staging batch {batchId} is already committed.");

            var staged = 0;
            foreach (var file in files)
            {
                var safeName = Path.GetFileName(file.FileName);
                // GetFileName neutralizes directories but passes "." / ".." / "" through (SVC-14) — those
                // make File.Create target the batch dir itself (500). No traversal either way; give the
                // row a synthetic name instead of failing the whole chunk.
                if (safeName is "" or "." or "..")
                    safeName = $"unnamed_{staged}";
                var stagedPath = Path.Combine(batchDir, safeName);

                // Append a counter suffix on name collision, probing disk (not manifest count) so the
                // suffixed candidate can't itself already exist (GI-05: a_1.x could clobber a prior a_1.x).
                if (File.Exists(stagedPath))
                {
                    var baseName = Path.GetFileNameWithoutExtension(safeName);
                    var ext      = Path.GetExtension(safeName);
                    for (int n = 1; File.Exists(stagedPath); n++)
                        stagedPath = Path.Combine(batchDir, $"{baseName}_{n}{ext}");
                }

                await using (var dest = File.Create(stagedPath))
                    await file.CopyToAsync(dest, ct);

                manifest.Files.Add(new StagedManifestFile(file.FileName, Path.GetFileName(stagedPath), file.Length));
                staged++;
            }

            await WriteManifestAtomicAsync(manifestPath, JsonSerializer.Serialize(manifest), ct);
            return staged;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>Loads the batch manifest, or null if the dir/manifest is missing or unreadable.</summary>
    public async Task<TManifest?> LoadManifestAsync(string userId, string batchId)
    {
        if (!IsValidBatchId(batchId))
        {
            LogTemplated(LogLevel.Warning, null, _opts.LoadRejectedInvalidIdLog, batchId);
            return null;
        }

        var batchDir = GetBatchDir(userId, batchId);
        var manifestPath = GetManifestPath(batchDir);
        if (!File.Exists(manifestPath))
        {
            if (_opts.ManifestNotFoundLog is { } template)
                LogTemplated(LogLevel.Warning, null, template, batchId);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath);
            return JsonSerializer.Deserialize<TManifest>(json);
        }
        catch (Exception ex)
        {
            LogTemplated(LogLevel.Error, ex, _opts.ReadManifestFailedLog, batchId);
            return null;
        }
    }

    /// <summary>
    /// Marks a staged batch as committed (enqueued for import). Re-checks Committed under the lock
    /// (IE-01/PI-04): two racing commits must not both transition + enqueue. Returns true only when this
    /// call performed the uncommitted→committed transition; false = lost the race or the batch is gone.
    /// </summary>
    public async Task<bool> TryMarkCommittedAsync(string userId, string batchId)
    {
        if (!IsValidBatchId(batchId)) return false;

        var manifestPath = GetManifestPath(GetBatchDir(userId, batchId));
        if (!File.Exists(manifestPath)) return false;

        await _manifestLock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath);
            var manifest = JsonSerializer.Deserialize<TManifest>(json);
            if (manifest == null || manifest.Committed) return false;

            await WriteManifestAtomicAsync(
                manifestPath, JsonSerializer.Serialize(manifest.WithCommitted()), CancellationToken.None);
            return true;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>Deletes one staged file (terminal-outcome cleanup / after an assign). Safe on missing
    /// paths; <see cref="Path.GetFileName(string)"/> keeps the name from escaping the batch dir.</summary>
    public void TryDeleteFile(string userId, string batchId, string stagedName)
    {
        if (!IsValidBatchId(batchId)) return;
        var safe = Path.GetFileName(stagedName); // never let a name escape the batch dir
        var path = Path.Combine(GetBatchDir(userId, batchId), safe);
        try { File.Delete(path); } catch (DirectoryNotFoundException) { /* already gone */ }
    }

    /// <summary>Deletes the batch directory. Safe to call on missing paths.</summary>
    public void Delete(string userId, string batchId)
    {
        if (!IsValidBatchId(batchId)) return;

        var batchDir = GetBatchDir(userId, batchId);
        try
        {
            if (Directory.Exists(batchDir))
                Directory.Delete(batchDir, recursive: true);
        }
        catch (Exception ex)
        {
            LogTemplated(LogLevel.Warning, ex, _opts.DeleteBatchFailedLog, batchId);
        }
    }

    /// <summary>Removes all per-user staging batch dirs older than <paramref name="maxAge"/> (startup sweep).</summary>
    public void CleanupOrphans(TimeSpan maxAge) => CleanupOrphansCore(
        _clock.GetUtcNow().UtcDateTime - maxAge, dir => new DirectoryInfo(dir).CreationTimeUtc);

    /// <summary>
    /// Two-level sweep: delete each <em>batch</em> dir older than <paramref name="cutoff"/> (not the
    /// user dir, whose CreationTime = its first-ever batch — which used to take young batches with it
    /// and then re-enumerate already-deleted children), then remove now-empty user dirs.
    /// <paramref name="createdUtc"/> is injected because Linux can't set directory creation time in tests.
    /// </summary>
    internal void CleanupOrphansCore(DateTime cutoff, Func<string, DateTime> createdUtc)
    {
        if (!Directory.Exists(_rootDir)) return;

        foreach (var userDir in Directory.EnumerateDirectories(_rootDir))
        {
            foreach (var batchDir in Directory.EnumerateDirectories(userDir))
            {
                try
                {
                    if (createdUtc(batchDir) < cutoff)
                        Directory.Delete(batchDir, recursive: true);
                }
                catch (Exception ex)
                {
                    LogTemplated(LogLevel.Warning, ex, _opts.OrphanCleanupFailedLog, batchDir);
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(userDir).Any())
                    Directory.Delete(userDir);
            }
            catch (Exception ex)
            {
                LogTemplated(LogLevel.Warning, ex, _opts.OrphanCleanupFailedLog, userDir);
            }
        }
    }

    /// <summary>The batch directory path (for wrappers to resolve staged-file paths). Validation of the
    /// batchId is the caller's concern — every public store entry point validates first.</summary>
    public string GetBatchDir(string userId, string batchId) =>
        Path.Combine(_rootDir, SanitizePathSegment(userId), batchId);

    // Atomic manifest write (IE-02/PI-08): serialize to a sibling .tmp then rename over the target. A
    // rename is atomic on the same volume, so lock-free readers see old-or-new, never torn.
    private static void WriteManifestAtomic(string manifestPath, string json)
    {
        var tmp = manifestPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, manifestPath, overwrite: true);
    }

    private static async Task WriteManifestAtomicAsync(string manifestPath, string json, CancellationToken ct)
    {
        var tmp = manifestPath + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, manifestPath, overwrite: true);
    }

    private static bool IsValidBatchId(string batchId) => Guid.TryParseExact(batchId, "N", out _);

    private static string GetManifestPath(string batchDir) => Path.Combine(batchDir, "manifest.json");

    private static string SanitizePathSegment(string segment) =>
        string.Concat(segment.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // The templates are per-vertical constants injected once via options (each feature's original log
    // text preserved verbatim), not user data — safe despite CA2254's static-template preference.
#pragma warning disable CA2254
    private void LogTemplated(LogLevel level, Exception? ex, string template, string arg) =>
        _logger.Log(level, ex, template, arg);
#pragma warning restore CA2254
}
