using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Photos;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>Pins the AS-02 orphan-rendition sweep core: files without a Photos row are purged (rows kept
/// intact), and files younger than the mtime grace are left for the next run. Real SQLite + a temp-root
/// <see cref="FileSystemPhotoImageStore"/> so the filesystem walk and DB re-check both run for real.</summary>
public class PhotoOrphanSweepServiceTests : IAsyncLifetime
{
    private RouteBaseDbContext _context = null!;
    private string _root = null!;
    private FileSystemPhotoImageStore _store = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:orphansweep_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = "me", DisplayName = "me", Email = "me@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        _root = Path.Combine(Path.GetTempPath(), $"orphansweep_{Guid.NewGuid():N}");
        _store = new FileSystemPhotoImageStore(new PhotoStorageOptions { Root = _root });
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return _context.DisposeAsync().AsTask();
    }

    private async Task AddPhotoRowAsync(int id)
    {
        _context.Activities.Add(new Activity { TrackId = new TrackId(id), UserId = "me", ActivityType = "Ride" });
        _context.Photos.Add(new Photo { PhotoId = new PhotoId(id), TrackId = new TrackId(id), UserId = "me" });
        await _context.SaveChangesAsync();
    }

    private async Task WriteRenditionsAsync(int id)
    {
        await _store.PutAsync(new PhotoId(id), PhotoTier.Thumb, new byte[] { 1 });
        await _store.PutAsync(new PhotoId(id), PhotoTier.Preview, new byte[] { 2 });
    }

    private bool HasFiles(int id) =>
        File.Exists(_store.GetPhysicalPath(new PhotoId(id), PhotoTier.Thumb))
        || File.Exists(_store.GetPhysicalPath(new PhotoId(id), PhotoTier.Preview));

    [Fact]
    public async Task SweepOnce_DeletesFilesWithoutRows()
    {
        await AddPhotoRowAsync(1);         // row + files → live
        await WriteRenditionsAsync(1);
        await WriteRenditionsAsync(2);     // files only → orphan

        var purged = await PhotoOrphanSweepService.SweepOnceAsync(
            _context, _store, TimeSpan.Zero, null, DateTime.UtcNow, default);

        Assert.Equal(1, purged);
        Assert.True(HasFiles(1));   // live photo untouched
        Assert.False(HasFiles(2));  // orphan purged
    }

    [Fact]
    public async Task SweepOnce_SkipsFreshFiles()
    {
        await WriteRenditionsAsync(2); // orphan, just written

        var purged = await PhotoOrphanSweepService.SweepOnceAsync(
            _context, _store, TimeSpan.FromHours(1), null, DateTime.UtcNow, default);

        Assert.Equal(0, purged);
        Assert.True(HasFiles(2)); // within grace window → left for next run
    }
}
