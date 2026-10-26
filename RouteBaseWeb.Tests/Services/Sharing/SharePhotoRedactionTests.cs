using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Photos;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// The AS-05 owner-field redaction under IMPERSONATION.
///
/// This is a regression fixture for a real leak: the share slice reads through
/// <c>ShareLinkAccess.WidenAsync</c>, which runs the read as the resource OWNER, while
/// <see cref="PhotoQueryService"/> decided "is this the owner?" by comparing each row against the AMBIENT
/// current user. Inside the widened scope that comparison answered YES for an anonymous visitor, so a
/// share view received <c>CameraModel</c> and <c>OrigFilename</c> — the owner's device and a full
/// home-directory path — while a signed-in FRIEND correctly received neither.
///
/// The fix makes the decision an explicit caller-supplied <see cref="PhotoFieldScope"/>. These tests pin
/// that it survives impersonation, which is the only condition under which the original bug appeared.
/// </summary>
public class SharePhotoRedactionTests : IAsyncLifetime
{
    private const string Owner = "owner-user";
    private const int TrackId = 7;

    private RouteBaseDbContext _db = null!;
    private CurrentUserService _currentUser = null!;
    private PhotoQueryService _query = null!;
    private string _storeRoot = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:sharephoto_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _db = new RouteBaseDbContext(options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.EnsureCreatedAsync();

        _db.Users.Add(new AppUser
        {
            UserId = Owner, DisplayName = Owner, Email = "o@t.com", IsAdmin = false,
        });
        _db.Activities.Add(new Activity
        {
            TrackId = new TrackId(TrackId), UserId = Owner, TrackName = "ride",
            Visibility = "public", Lifecycle = LifecycleStates.Released,
        });
        _db.Photos.Add(new Photo
        {
            PhotoId = new PhotoId(1),
            TrackId = new TrackId(TrackId),
            UserId = Owner,
            // The two owner-only fields, with values whose leak is self-evidently a problem.
            CameraModel = "Pixel 9 Pro",
            OrigFilename = "/home/owner/Pictures/IMG_0001.jpg",
            OnMap = true,
            Lat = 47.1,
            Lng = 8.2,
            ContentHash = "abcdef1234567890",
        });
        await _db.SaveChangesAsync();

        _storeRoot = Path.Combine(Path.GetTempPath(), $"sharephoto_{Guid.NewGuid():N}");
        var store = new FileSystemPhotoImageStore(new PhotoStorageOptions { Root = _storeRoot });

        // No HttpContext: CurrentUserService then answers from the override alone, which is exactly the
        // state WidenAsync puts it in.
        _currentUser = new CurrentUserService(new NoHttpContextAccessor(), new ConfigurationBuilder().Build());
        _query = new PhotoQueryService(
            _db, new VisibilityService(_currentUser, null!, _db), _currentUser, store);
    }

    public async Task DisposeAsync()
    {
        try { if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, recursive: true); } catch { }
        await _db.DisposeAsync();
    }

    private sealed class NoHttpContextAccessor : Microsoft.AspNetCore.Http.IHttpContextAccessor
    {
        public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
    }

    private Task<PhotoListResponse> ListAsAsync(string userId, PhotoFieldScope scope)
    {
        using var _ = _currentUser.BeginUserScope(userId);
        return _query.ListAsync(new TrackId(TrackId), null, null, "captured", 1, 50, scope, default);
    }

    /// <summary>THE regression. Impersonating the owner must not turn the redaction off.</summary>
    [Fact]
    public async Task NeverOwner_RedactsOwnerFields_EvenWhileImpersonatingTheOwner()
    {
        var page = await ListAsAsync(Owner, PhotoFieldScope.NeverOwner);

        var photo = Assert.Single(page.Items);
        Assert.Null(photo.CameraModel);
        Assert.Null(photo.OrigFilename);
    }

    /// <summary>
    /// The leak was not abstract: the original filename is a filesystem path from the owner's machine.
    /// Asserted on the serialized shape so it also catches a future field carrying the same value.
    /// </summary>
    [Fact]
    public async Task NeverOwner_LeaksNoHomeDirectoryPath_Anywhere()
    {
        var page = await ListAsAsync(Owner, PhotoFieldScope.NeverOwner);

        var json = System.Text.Json.JsonSerializer.Serialize(page);
        Assert.DoesNotContain("/home/owner", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Pixel 9 Pro", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counterpart: the authenticated surface is unchanged. Without this, "redact everything" would
    /// pass the test above while quietly breaking the owner's own photo grid.
    /// </summary>
    [Fact]
    public async Task OwnerWhenOwned_StillGivesTheRealOwnerTheirOwnFields()
    {
        var page = await ListAsAsync(Owner, PhotoFieldScope.OwnerWhenOwned);

        var photo = Assert.Single(page.Items);
        Assert.Equal("Pixel 9 Pro", photo.CameraModel);
        Assert.Equal("/home/owner/Pictures/IMG_0001.jpg", photo.OrigFilename);
    }

    /// <summary>
    /// CanRestore is an owner-only affordance and leaked the same way. Cosmetic today (the share view sets
    /// canPlace=false so no button renders), pinned so it stays cosmetic.
    /// </summary>
    [Fact]
    public async Task NeverOwner_DoesNotOfferTheOwnerOnlyRestoreAffordance()
    {
        _db.Photos.Single().OnMap = false;
        await _db.SaveChangesAsync();

        var page = await ListAsAsync(Owner, PhotoFieldScope.NeverOwner);

        Assert.False(Assert.Single(page.Items).CanRestore);
    }
}
