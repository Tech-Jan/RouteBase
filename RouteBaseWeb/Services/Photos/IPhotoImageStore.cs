using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Photos;

/// <summary>Rendition tiers. Phase 2 produces and serves <see cref="Thumb"/> + <see cref="Preview"/> only;
/// <see cref="Display"/>/<see cref="Original"/> are reserved so adding HQ later is a new enum value + a
/// store branch, not an app-wide change (Photos §1a seam).</summary>
public enum PhotoTier { Thumb, Preview, Display, Original }

/// <summary>
/// Tier-aware store for a photo's rendition files. The app never touches image bytes directly — it goes
/// through this seam, so a new tier or a new backend (S3, multi-location) is additive (§1a/§1c). The
/// Phase-2 implementation is <c>FileSystemPhotoImageStore</c> over <c>FileSystemBlobStore</c>.
/// </summary>
public interface IPhotoImageStore
{
    /// <summary>Write the JPEG bytes for one tier (crash-safe; overwrites any existing file).</summary>
    Task PutAsync(PhotoId photoId, PhotoTier tier, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);

    /// <summary>Open the tier file for reading, or <c>null</c> if absent (→ ThumbHash placeholder, never a crash).</summary>
    Stream? OpenRead(PhotoId photoId, PhotoTier tier);

    /// <summary>Absolute path of the tier file — for streamed <c>PhysicalFileResult</c>/sendfile serving.</summary>
    string GetPhysicalPath(PhotoId photoId, PhotoTier tier);

    /// <summary>Delete every rendition file for a photo. Best-effort, idempotent (orphans are swept, §1b).</summary>
    Task DeleteAsync(PhotoId photoId, CancellationToken ct = default);

    /// <summary>Ids of every photo that has at least one stored rendition file, parsed from the tree.
    /// For orphan reconciliation (<c>PhotoOrphanSweepService</c>) only — never a lookup path (those open
    /// by id). Scans the filesystem; do not call on a request path.</summary>
    IEnumerable<int> EnumerateStoredIds();
}
