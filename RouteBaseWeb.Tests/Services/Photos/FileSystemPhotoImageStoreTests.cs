using RouteBaseWeb.Services.Photos;
using Xunit;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>Pins <see cref="FileSystemPhotoImageStore.EnumerateStoredIds"/> — the orphan-sweep (AS-02)
/// reconciliation scan: distinct ids across tiers, none double-counted, fan-out buckets crossed.</summary>
public class FileSystemPhotoImageStoreTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemPhotoImageStore _store;

    public FileSystemPhotoImageStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"photostore_{Guid.NewGuid():N}");
        _store = new FileSystemPhotoImageStore(new PhotoStorageOptions { Root = _root });
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task EnumerateStoredIds_ReturnsDistinctIds_AcrossTiers()
    {
        await _store.PutAsync(new PhotoId(5), PhotoTier.Thumb, new byte[] { 1 });
        await _store.PutAsync(new PhotoId(5), PhotoTier.Preview, new byte[] { 2 });
        await _store.PutAsync(new PhotoId(1005), PhotoTier.Thumb, new byte[] { 3 }); // different fan-out bucket, thumb only

        var ids = _store.EnumerateStoredIds().OrderBy(x => x).ToList();

        Assert.Equal(new[] { 5, 1005 }, ids); // id 5 counted once despite two tier files
    }
}
