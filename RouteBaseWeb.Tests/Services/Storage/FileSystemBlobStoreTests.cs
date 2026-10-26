using System.Text;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Storage;
using Xunit;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Tests.Services.Storage;

/// <summary>
/// Pins the domain-agnostic FS blob mechanics: fan-out pathing, crash-safe put, streaming read, and
/// idempotent delete — plus the thin FileSystemPhotoImageStore mapping (tier → variant, photos ns).
/// Each test roots the store at a fresh temp dir so nothing touches the real database/photos folder.
/// </summary>
public class FileSystemBlobStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gv-blobstore-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(0, "0/0_thumb.jpg")]
    [InlineData(999, "0/999_thumb.jpg")]
    [InlineData(1000, "1/1000_thumb.jpg")]
    [InlineData(1001, "1/1001_thumb.jpg")]
    [InlineData(2_000_000, "2000/2000000_thumb.jpg")]
    public void GetPath_FansOutByThousand(long id, string expectedTail)
    {
        var store = new FileSystemBlobStore(_root);
        var path = store.GetPath("photos", id, "thumb", "jpg");
        var expected = Path.Combine(_root, "photos", Path.Combine(expectedTail.Split('/')));
        Assert.Equal(expected, path);
    }

    [Fact]
    public async Task Put_ThenOpenRead_RoundTrips()
    {
        var store = new FileSystemBlobStore(_root);
        var bytes = Encoding.UTF8.GetBytes("hello-blob");

        await store.PutAsync("photos", 42, "preview", "jpg", bytes);

        await using var stream = store.OpenRead("photos", 42, "preview", "jpg");
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    public async Task Put_OverwritesExisting_AndLeavesNoTempFile()
    {
        var store = new FileSystemBlobStore(_root);
        await store.PutAsync("photos", 7, "thumb", "jpg", new byte[] { 1, 2, 3 });
        await store.PutAsync("photos", 7, "thumb", "jpg", new byte[] { 9 });

        await using (var stream = store.OpenRead("photos", 7, "thumb", "jpg"))
        {
            using var ms = new MemoryStream();
            await stream!.CopyToAsync(ms);
            Assert.Equal(new byte[] { 9 }, ms.ToArray());
        }

        // No .tmp residue from the atomic write.
        var bucket = Path.GetDirectoryName(store.GetPath("photos", 7, "thumb", "jpg"))!;
        Assert.Empty(Directory.EnumerateFiles(bucket, "*.tmp"));
    }

    [Fact]
    public void OpenRead_MissingFile_ReturnsNull()
    {
        var store = new FileSystemBlobStore(_root);
        Assert.Null(store.OpenRead("photos", 12345, "preview", "jpg"));
    }

    [Fact]
    public void DeleteVariant_MissingFileOrBucket_DoesNotThrow()
    {
        var store = new FileSystemBlobStore(_root);
        store.DeleteVariant("photos", 555, "thumb", "jpg"); // no bucket dir at all
    }

    // ── FileSystemPhotoImageStore (thin photo layer) ──────────────────────────

    [Fact]
    public async Task PhotoStore_DeleteAsync_RemovesBothTiers_ButLeavesOtherPhotos()
    {
        var store = new FileSystemPhotoImageStore(new PhotoStorageOptions { Root = _root });
        await store.PutAsync(new PhotoId(100), PhotoTier.Thumb, new byte[] { 1 });
        await store.PutAsync(new PhotoId(100), PhotoTier.Preview, new byte[] { 2 });
        await store.PutAsync(new PhotoId(101), PhotoTier.Thumb, new byte[] { 3 }); // same bucket, must survive

        await store.DeleteAsync(new PhotoId(100));

        Assert.Null(store.OpenRead(new PhotoId(100), PhotoTier.Thumb));
        Assert.Null(store.OpenRead(new PhotoId(100), PhotoTier.Preview));
        await using var survivor = store.OpenRead(new PhotoId(101), PhotoTier.Thumb);
        Assert.NotNull(survivor);
    }

    [Fact]
    public void PhotoStore_Path_UsesPhotosNamespaceAndJpg()
    {
        var store = new FileSystemPhotoImageStore(new PhotoStorageOptions { Root = _root });
        var path = store.GetPhysicalPath(new PhotoId(2500), PhotoTier.Preview);
        Assert.Equal(Path.Combine(_root, "photos", "2", "2500_preview.jpg"), path);
    }

    [Fact]
    public void PhotoStore_UnsupportedTier_Throws()
    {
        var store = new FileSystemPhotoImageStore(new PhotoStorageOptions { Root = _root });
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetPhysicalPath(new PhotoId(1), PhotoTier.Original));
    }
}
