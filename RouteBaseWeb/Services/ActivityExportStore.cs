using System.Globalization;
using RouteBaseWeb.Services.Storage;

namespace RouteBaseWeb.Services;

/// <summary>Where a finished export ZIP lives, and who may fetch it.</summary>
public interface IActivityExportStore
{
    /// <summary>A fresh job id. Monotonic within a process; the owner check below is what actually guards
    /// the file, so ids need to be unique, not unguessable.</summary>
    long NextJobId();

    /// <summary>Move a fully-built ZIP into the store. The source path is consumed (moved, not copied).</summary>
    void Commit(string userId, long jobId, string builtFilePath);

    /// <summary>Open a committed ZIP, or null if this user has no such job (missing and not-yours answer
    /// identically — the surface's 404-never-403 convention).</summary>
    Stream? OpenRead(string userId, long jobId);

    /// <summary>Delete every export older than <paramref name="maxAge"/>. Returns how many went.</summary>
    int SweepOlderThan(TimeSpan maxAge, DateTimeOffset now);
}

/// <summary>
/// Export ZIPs on disk, over the domain-agnostic <see cref="FileSystemBlobStore"/> (the same mechanics the
/// photo renditions use). Files land at <c>{Root}/exports/{jobId/1000}/{jobId}_{userId}.zip</c>.
///
/// The owning user is part of the FILENAME rather than tracked in a table: an export is a derived artifact
/// with a short life, and the queue that produces it is itself in-memory, so a row that outlived a restart
/// would only ever point at a file the sweep is about to delete. Encoding the owner keeps the read path a
/// pure path computation — no index, no lookup, and no way to serve someone else's ZIP by guessing an id.
/// </summary>
public sealed class ActivityExportStore : IActivityExportStore
{
    internal const string Namespace = "exports";
    private const string Ext = "zip";

    private readonly FileSystemBlobStore _blobs;
    private readonly string _namespaceDir;
    private readonly ILogger<ActivityExportStore> _logger;
    private long _nextJobId = 1;

    public ActivityExportStore(ActivityExportStorageOptions options, ILogger<ActivityExportStore> logger)
    {
        _blobs = new FileSystemBlobStore(options.Root);
        _namespaceDir = Path.Combine(options.Root, Namespace);
        _logger = logger;
    }

    public long NextJobId() => Interlocked.Increment(ref _nextJobId);

    // The user id is a SHA-256 hex 'sub' claim, so it is already filename-safe; the guard is here because a
    // path separator arriving in it would silently write outside the store.
    private static string Variant(string userId) =>
        userId.Any(c => c is '/' or '\\' or ':' || Path.GetInvalidFileNameChars().Contains(c))
            ? throw new ArgumentException("user id is not filename-safe", nameof(userId))
            : userId;

    public void Commit(string userId, long jobId, string builtFilePath)
    {
        var path = _blobs.GetPath(Namespace, jobId, Variant(userId), Ext);
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"export path has no directory: {path}"));
        // Move, not copy: the caller built straight into a temp file, so this is the atomic publish step.
        File.Move(builtFilePath, path, overwrite: true);
    }

    public Stream? OpenRead(string userId, long jobId) =>
        _blobs.OpenRead(Namespace, jobId, Variant(userId), Ext);

    public int SweepOlderThan(TimeSpan maxAge, DateTimeOffset now)
    {
        if (!Directory.Exists(_namespaceDir)) return 0;

        var cutoff = now - maxAge;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_namespaceDir, $"*.{Ext}", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) > cutoff.UtcDateTime) continue;
                File.Delete(path);
                deleted++;
            }
            catch (IOException ex)
            {
                // A download in flight holds the file open. It will be swept on the next pass.
                _logger.LogDebug(ex, "Export sweep skipped {Path}", path);
            }
        }
        return deleted;
    }
}

/// <summary>Root directory of the export store — set from the database directory in Program.cs, exactly
/// like <see cref="Photos.PhotoStorageOptions"/>.</summary>
public sealed class ActivityExportStorageOptions
{
    public required string Root { get; init; }

    /// <summary>How long a finished export stays downloadable. It is a derived artifact the user can
    /// always rebuild, and the ZIPs are large, so this is deliberately short.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);

    public string RetentionText =>
        Retention.TotalHours >= 1
            ? $"{Retention.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} hours"
            : $"{Retention.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes";
}
