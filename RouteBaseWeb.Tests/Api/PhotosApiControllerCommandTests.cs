using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Photos;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Tracks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Characterization pins for <see cref="PhotosApiController"/>'s mutate surface — place/unplace/restore
/// round-trip, restore-without-location guard, and the row-committed-before-files delete ordering (AS-03).
/// These invariants had no test before B3; they pin current behavior so the query/command service
/// extraction stays byte-for-byte behavior-frozen. Real SQLite; a NullTrackReader (empty samples).
/// </summary>
public class PhotosApiControllerCommandTests : IAsyncLifetime
{
    private const string Me = "me";

    private RouteBaseDbContext _context = null!;
    private OrderRecordingStore _store = null!;
    private readonly NullTrackReader _trackReader = new();

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:photoscmd_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();

        _context.Users.Add(new AppUser { UserId = Me, DisplayName = Me, Email = $"{Me}@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        _store = new OrderRecordingStore(id => _context.Photos.AsNoTracking().Any(p => p.PhotoId == id));
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private PhotosApiController ControllerFor(string userId)
    {
        var currentUser = new TestCurrentUserService(userId);
        // B3: the mutate endpoints delegate to PhotoCommandService, so the pins drive the controller over
        // a real command service (real _context, NullTrackReader → empty samples, the order-recording
        // store). Construction change only.
        var command = new PhotoCommandService(_context, _trackReader, _store, currentUser);
        return new PhotosApiController(
            null!, null!, null!, currentUser, null!, command, null!, null!, null!,
            NullLogger<PhotosApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private async Task SeedActivityAsync(TrackId trackId)
    {
        _context.Activities.Add(new Activity { TrackId = trackId, UserId = Me, ActivityType = "Ride" });
        await _context.SaveChangesAsync();
    }

    private async Task<PhotoId> SeedPhotoAsync(
        PhotoId photoId, TrackId trackId, string locationSource, double? lat, double? lng, bool onMap)
    {
        _context.Photos.Add(new Photo
        {
            PhotoId = photoId, TrackId = trackId, UserId = Me, ContentHash = "abcdef1234567890",
            LocationSource = locationSource, Lat = lat, Lng = lng, OnMap = onMap,
            CapturedUtc = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc),
        });
        await _context.SaveChangesAsync();
        return photoId;
    }

    private Photo Reload(PhotoId photoId) =>
        _context.Photos.AsNoTracking().First(p => p.PhotoId == photoId);

    // ── unplace / restore round-trip ─────────────────────────────────────────
    [Fact]
    public async Task Unplace_KeepsLatLng_ThenRestore_SnapsBack()
    {
        await SeedActivityAsync(new TrackId(1));
        var id = await SeedPhotoAsync(new PhotoId(110), new TrackId(1), "manual", 47.5, 8.5, onMap: true);

        await ControllerFor(Me).Unplace(id.Value, default);
        var afterUnplace = Reload(id);
        Assert.False(afterUnplace.OnMap);
        Assert.Equal(47.5, afterUnplace.Lat); // coords kept so restore can snap back
        Assert.Equal(8.5, afterUnplace.Lng);

        var restore = await ControllerFor(Me).Restore(id.Value, default);
        Assert.IsType<OkObjectResult>(restore);
        var afterRestore = Reload(id);
        Assert.True(afterRestore.OnMap);
        Assert.Equal(47.5, afterRestore.Lat);
        Assert.Equal(8.5, afterRestore.Lng);
    }

    [Fact]
    public async Task Restore_WithoutStoredLocation_Returns409()
    {
        await SeedActivityAsync(new TrackId(1));
        var id = await SeedPhotoAsync(new PhotoId(111), new TrackId(1), "none", null, null, onMap: false);

        var result = await ControllerFor(Me).Restore(id.Value, default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.False(Reload(id).OnMap);
    }

    // ── delete: row committed before rendition files (AS-03) ─────────────────
    [Fact]
    public async Task Delete_RemovesRowBeforeFiles()
    {
        await SeedActivityAsync(new TrackId(1));
        var id = await SeedPhotoAsync(new PhotoId(120), new TrackId(1), "manual", 47.5, 8.5, onMap: true);

        await ControllerFor(Me).Delete(id.Value, default);

        Assert.Equal(new[] { id }, _store.Deleted.ToArray());
        Assert.All(_store.RowExistedAtDelete, existed => Assert.False(existed)); // row gone before store call
    }

    [Fact]
    public async Task DeleteMany_RemovesRowsBeforeFiles()
    {
        await SeedActivityAsync(new TrackId(1));
        var a = await SeedPhotoAsync(new PhotoId(130), new TrackId(1), "manual", 47.5, 8.5, onMap: true);
        var b = await SeedPhotoAsync(new PhotoId(131), new TrackId(1), "manual", 47.6, 8.6, onMap: true);

        await ControllerFor(Me).DeleteMany(new DeletePhotosRequest(new List<int> { a.Value, b.Value }), default);

        Assert.Equal(2, _store.Deleted.Count);
        Assert.All(_store.RowExistedAtDelete, existed => Assert.False(existed));
    }

    /// <summary>Bulk delete is documented to silently ignore ids the caller doesn't own — so an
    /// all-gone/all-foreign selection is a 200 with deleted=0 (lets the client reconcile its grid),
    /// not a 404 the UI can't distinguish from a real failure.</summary>
    [Fact]
    public async Task DeleteMany_NoRowsDeleted_ReturnsOkWithZeroCount()
    {
        var result = await ControllerFor(Me).DeleteMany(new DeletePhotosRequest(new List<int> { 999 }), default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, Assert.IsType<DeletedCountResponse>(ok.Value).Deleted);
        Assert.Empty(_store.Deleted);
    }

    // ── doubles ──────────────────────────────────────────────────────────────
    /// <summary>Records each DeleteAsync call and whether the photo row still existed at that moment —
    /// pins the row-then-files ordering (a store call must see an already-committed deletion).</summary>
    private sealed class OrderRecordingStore : IPhotoImageStore
    {
        private readonly Func<PhotoId, bool> _rowExists;
        public List<PhotoId> Deleted { get; } = new();
        public List<bool> RowExistedAtDelete { get; } = new();

        public OrderRecordingStore(Func<PhotoId, bool> rowExists) => _rowExists = rowExists;

        public Task DeleteAsync(PhotoId photoId, CancellationToken ct = default)
        {
            Deleted.Add(photoId);
            RowExistedAtDelete.Add(_rowExists(photoId));
            return Task.CompletedTask;
        }

        public Task PutAsync(PhotoId photoId, PhotoTier tier, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
            => Task.CompletedTask;
        public Stream? OpenRead(PhotoId photoId, PhotoTier tier) => null;
        public string GetPhysicalPath(PhotoId photoId, PhotoTier tier) => "";
        public IEnumerable<int> EnumerateStoredIds() => Array.Empty<int>();
    }

    private sealed class NullTrackReader : ITrackReader
    {
        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default) =>
            Task.FromResult<DecodedTrack?>(null);
    }
}
