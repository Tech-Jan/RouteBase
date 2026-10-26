using RouteBaseWeb.Services.Storage;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Photos;

/// <summary>
/// Photo-semantics layer over the domain-agnostic <see cref="FileSystemBlobStore"/>: maps a
/// <see cref="PhotoTier"/> to a variant name and pins the <c>photos</c> namespace + <c>jpg</c> extension.
/// Files land at <c>{Root}/photos/{photoId/1000}/{photoId}_{tier}.jpg</c> (§1b).
/// </summary>
public sealed class FileSystemPhotoImageStore : IPhotoImageStore
{
    private const string Namespace = "photos";
    private const string Ext = "jpg";

    // The tiers this store actually persists in Phase 2 — DeleteAsync loops these.
    private static readonly PhotoTier[] StoredTiers = { PhotoTier.Thumb, PhotoTier.Preview };

    private readonly FileSystemBlobStore _blobs;

    public FileSystemPhotoImageStore(PhotoStorageOptions options)
        => _blobs = new FileSystemBlobStore(options.Root);

    private static string Variant(PhotoTier tier) => tier switch
    {
        PhotoTier.Thumb   => "thumb",
        PhotoTier.Preview => "preview",
        // Display/Original are reserved (§1a) — not produced or served in Phase 2.
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Only Thumb/Preview are stored in Phase 2."),
    };

    public Task PutAsync(PhotoId photoId, PhotoTier tier, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        => _blobs.PutAsync(Namespace, photoId.Value, Variant(tier), Ext, bytes, ct);

    public Stream? OpenRead(PhotoId photoId, PhotoTier tier)
        => _blobs.OpenRead(Namespace, photoId.Value, Variant(tier), Ext);

    public string GetPhysicalPath(PhotoId photoId, PhotoTier tier)
        => _blobs.GetPath(Namespace, photoId.Value, Variant(tier), Ext);

    public Task DeleteAsync(PhotoId photoId, CancellationToken ct = default)
    {
        foreach (var tier in StoredTiers)
            _blobs.DeleteVariant(Namespace, photoId.Value, Variant(tier), Ext);
        return Task.CompletedTask;
    }

    public IEnumerable<int> EnumerateStoredIds() =>
        StoredTiers
            .SelectMany(tier => _blobs.EnumerateIds(Namespace, Variant(tier), Ext))
            .Select(id => (int)id)
            .Distinct();
}
