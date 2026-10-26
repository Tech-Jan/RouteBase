using System.Globalization;

namespace RouteBaseWeb.Services.Storage;

/// <summary>
/// Domain-agnostic filesystem blob mechanics: directory fan-out, crash-safe writes, streaming reads.
/// Parameterized by <c>namespace + id + variant + ext</c> and rooted at a configurable directory — it
/// knows nothing about photos, tiers, or the DB. The photo layer (<c>FileSystemPhotoImageStore</c>) is a
/// thin wrapper; a future heatmap/tile consumer or an <c>IBlobStore</c> promotion reuses this same core
/// (Photos §1d — rule-of-three: don't generalize further until a second real consumer lands).
///
/// Fan-out path: <c>{root}/{ns}/{id/1000}/{id}_{variant}.{ext}</c> → ~ (1000 × variants) files per bucket
/// dir, constant as the corpus grows. Lookups <b>never enumerate</b> — the DB is the index, callers hold
/// the id and <c>open()</c> directly (O(log n) on htree/B-tree filesystems regardless of folder size, §12a).
/// </summary>
public sealed class FileSystemBlobStore
{
    private const int BucketSize = 1000;
    private readonly string _root;

    public FileSystemBlobStore(string root) => _root = root;

    /// <summary>Absolute path of one blob. Pure — computes, touches no disk (used by the controller for
    /// <c>PhysicalFileResult</c>/sendfile, and internally by the read/write/delete methods).</summary>
    public string GetPath(string ns, long id, string variant, string ext)
    {
        var bucket = (id / BucketSize).ToString(CultureInfo.InvariantCulture);
        return Path.Combine(_root, ns, bucket, $"{id}_{variant}.{ext}");
    }

    /// <summary>Write a blob crash-safely: stage to a sibling <c>.tmp</c> then atomically move into place,
    /// so an interrupted write never leaves a half-file that a reader would stream. Creates the bucket dir.</summary>
    public async Task PutAsync(string ns, long id, string variant, string ext,
        ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var path = GetPath(ns, id, variant, ext);
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"blob path has no directory: {path}"));

        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            await fs.WriteAsync(bytes, ct);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Open a blob for reading, or <c>null</c> if it doesn't exist (a missing derived file is
    /// harmless — the caller falls back to the ThumbHash placeholder, §1b). <c>FileShare.Read</c> so
    /// concurrent reads are fine; a concurrent write swaps the file atomically underneath.</summary>
    public Stream? OpenRead(string ns, long id, string variant, string ext)
    {
        var path = GetPath(ns, id, variant, ext);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    /// <summary>Delete one blob if present. Best-effort and idempotent — a missing file is not an error.</summary>
    public void DeleteVariant(string ns, long id, string variant, string ext)
    {
        var path = GetPath(ns, id, variant, ext);
        try { File.Delete(path); } catch (DirectoryNotFoundException) { /* bucket gone — already absent */ }
    }

    /// <summary>Ids of every stored blob for a namespace/variant/ext, parsed from filenames. The one method
    /// that <b>does</b> scan the tree — for orphan reconciliation, not lookups (those open by id). A missing
    /// namespace dir yields nothing; names that don't parse to an id (e.g. in-flight <c>.tmp</c>) are skipped.</summary>
    public IEnumerable<long> EnumerateIds(string ns, string variant, string ext)
    {
        var nsDir = Path.Combine(_root, ns);
        if (!Directory.Exists(nsDir)) yield break;

        var suffix = $"_{variant}.{ext}";
        foreach (var path in Directory.EnumerateFiles(nsDir, $"*.{ext}", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var idPart = name[..^suffix.Length];
            if (long.TryParse(idPart, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                yield return id;
        }
    }
}
