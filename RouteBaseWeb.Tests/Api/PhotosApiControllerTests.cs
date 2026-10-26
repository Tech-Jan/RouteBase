using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Models.Photos;
using RouteBaseWeb.Services.Photos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the AS-01 review gate on <see cref="PhotosApiController"/>: a non-owner may only reach photos on
/// a <c>confirmed</c> activity — <c>pending</c>/<c>discarded</c> auto-recordings stay owner-only, matching
/// VisibleActivities. Real SQLite + a temp-root <see cref="FileSystemPhotoImageStore"/> so serving hits a
/// real rendition file. Visibility is <c>public</c> so the gate (not visibility) is what denies.
/// </summary>
public class PhotosApiControllerTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Other = "other";

    private string _conn = null!;
    private RouteBaseDbContext _context = null!;
    private string _storeRoot = null!;
    private FileSystemPhotoImageStore _store = null!;

    public async Task InitializeAsync()
    {
        _conn = $"Data Source=file:photosapi_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        foreach (var id in new[] { Me, Other })
            _context.Users.Add(new AppUser { UserId = id, DisplayName = id, Email = $"{id}@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        _storeRoot = Path.Combine(Path.GetTempPath(), $"photosapi_{Guid.NewGuid():N}");
        _store = new FileSystemPhotoImageStore(new PhotoStorageOptions { Root = _storeRoot });
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, recursive: true); } catch { }
        return _context.DisposeAsync().AsTask();
    }

    private PhotosApiController ControllerFor(string userId)
    {
        var currentUser = new TestCurrentUserService(userId);
        // B3: GetImage/List now delegate to PhotoQueryService, so these read tests wire the real query
        // service (over the seeded _context + temp-root store) into the thin controller. Construction
        // change only — every assertion below is unchanged.
        // CR5-06: the photo view-gate lives in VisibilityService.CanViewActivityAsync, wired over the real
        // seeded _context (the TestVisibilityService double has no DB). Friendship is passed null! because
        // no case reaches the friends branch: all seeded activities are public (gate denies on Lifecycle,
        // or CanViewItemAsync short-circuits on "public" before touching friendship).
        var query = new PhotoQueryService(
            _context, new VisibilityService(currentUser, null!, _context), currentUser, _store);
        return new PhotosApiController(
            null!, null!, null!, currentUser, query, null!, null!, null!, null!,
            NullLogger<PhotosApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    /// <summary>Seed a public activity in the given review state plus one photo owned by <see cref="Me"/>.</summary>
    private async Task<int> SeedPhotoAsync(TrackId trackId, string lifecycle, string? reason = null)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = Me, ActivityType = "Ride",
            Source = "auto-recording", Lifecycle = lifecycle, LifecycleReason = reason, Visibility = "public",
        });
        _context.Photos.Add(new Photo
        {
            PhotoId = new PhotoId(trackId.Value), TrackId = trackId, UserId = Me, ContentHash = "abcdef1234567890",
        });
        await _context.SaveChangesAsync();
        await _store.PutAsync(new PhotoId(trackId.Value), PhotoTier.Thumb, new byte[] { 1, 2, 3 });
        return trackId.Value;
    }

    [Fact]
    public async Task GetImage_FriendViewer_PendingActivity_Returns404()
    {
        var id = await SeedPhotoAsync(new TrackId(1), LifecycleStates.InWork, LifecycleReasons.AwaitingReview);

        var result = await ControllerFor(Other).GetImage(id, "thumb");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task List_TrackId_DiscardedActivity_ReturnsEmptyForFriend()
    {
        var id = await SeedPhotoAsync(new TrackId(2), LifecycleStates.Trashed, LifecycleReasons.ReviewRejected);

        var result = await ControllerFor(Other).List(id, null, null, "captured", 1, 60, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<PhotoListResponse>(ok.Value);
        Assert.Equal(0, resp.Total);
    }

    [Fact]
    public async Task GetImage_Owner_PendingActivity_Serves()
    {
        var id = await SeedPhotoAsync(new TrackId(3), LifecycleStates.InWork, LifecycleReasons.AwaitingReview);

        var result = await ControllerFor(Me).GetImage(id, "thumb");

        AssertServesThumbOf(id, result);
    }

    [Fact]
    public async Task GetImage_FriendViewer_ConfirmedActivity_Serves()
    {
        var id = await SeedPhotoAsync(new TrackId(4), LifecycleStates.Released);

        var result = await ControllerFor(Other).GetImage(id, "thumb");

        AssertServesThumbOf(id, result);
    }

    /// <summary>A bare <c>IsType&lt;PhysicalFileResult&gt;</c> passes for the wrong photo or the wrong tier —
    /// pin the exact rendition path and content type so serving photo B (or a preview instead of a thumb)
    /// fails loudly.</summary>
    private void AssertServesThumbOf(int id, IActionResult result)
    {
        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(_store.GetPhysicalPath(new PhotoId(id), PhotoTier.Thumb), file.FileName);
        Assert.Equal("image/jpeg", file.ContentType);
    }

    // ── AS-05 non-owner DTO trim ──────────────────────────────────────────────
    /// <summary>Seed a confirmed public activity + one fully-populated photo owned by <see cref="Me"/>.</summary>
    private async Task<int> SeedRichPhotoAsync(TrackId trackId, bool onMap)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = Me, ActivityType = "Ride",
            Source = "auto-recording", Lifecycle = LifecycleStates.Released, Visibility = "public",
        });
        _context.Photos.Add(new Photo
        {
            PhotoId = new PhotoId(trackId.Value), TrackId = trackId, UserId = Me, ContentHash = "abcdef1234567890",
            Lat = 47.5, Lng = 8.5, OnMap = onMap, LocationSource = "exif",
            CameraModel = "Pixel 8 Pro", OrigFilename = "/home/me/IMG_0001.jpg",
            Description = "sunset over the lake",
        });
        await _context.SaveChangesAsync();
        return trackId.Value;
    }

    private static PhotoDto SingleDto(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<PhotoListResponse>(ok.Value);
        return Assert.Single(resp.Items);
    }

    [Fact]
    public async Task List_Owner_ReturnsFullDto()
    {
        var id = await SeedRichPhotoAsync(new TrackId(10), onMap: true);

        var dto = SingleDto(await ControllerFor(Me).List(id, null, null, "captured", 1, 60, default));

        Assert.Equal("Pixel 8 Pro", dto.CameraModel);
        Assert.Equal("/home/me/IMG_0001.jpg", dto.OrigFilename);
        Assert.Equal("sunset over the lake", dto.Description);
        Assert.Equal(47.5, dto.MarkerLat);
        Assert.Equal(8.5, dto.MarkerLng);
    }

    [Fact]
    public async Task List_Friend_DropsCameraModelAndOrigFilename()
    {
        var id = await SeedRichPhotoAsync(new TrackId(11), onMap: true);

        var dto = SingleDto(await ControllerFor(Other).List(id, null, null, "captured", 1, 60, default));

        Assert.Null(dto.CameraModel);
        Assert.Null(dto.OrigFilename);
    }

    [Fact]
    public async Task List_Friend_PinnedPhoto_KeepsCoords()
    {
        var id = await SeedRichPhotoAsync(new TrackId(12), onMap: true);

        var dto = SingleDto(await ControllerFor(Other).List(id, null, null, "captured", 1, 60, default));

        Assert.Equal(47.5, dto.MarkerLat);
        Assert.Equal(8.5, dto.MarkerLng);
    }

    [Fact]
    public async Task List_Friend_UnpinnedPhoto_HidesCoords()
    {
        var id = await SeedRichPhotoAsync(new TrackId(13), onMap: false);

        var dto = SingleDto(await ControllerFor(Other).List(id, null, null, "captured", 1, 60, default));

        Assert.Null(dto.MarkerLat);
        Assert.Null(dto.MarkerLng);
        Assert.False(dto.CanRestore); // owner-only affordance — never signalled to friends
    }

    [Fact]
    public async Task List_Friend_KeepsDescription()
    {
        var id = await SeedRichPhotoAsync(new TrackId(14), onMap: false);

        var dto = SingleDto(await ControllerFor(Other).List(id, null, null, "captured", 1, 60, default));

        Assert.Equal("sunset over the lake", dto.Description);
    }
}
