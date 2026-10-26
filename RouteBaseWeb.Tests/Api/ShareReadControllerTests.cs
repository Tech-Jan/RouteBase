using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Models.Photos;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// The share photo subresources must die with the activity's lifecycle, exactly like the share PAGE does:
/// an owner who trashes (or parks) a shared ride expects it gone from the link everywhere, but the photo
/// list and image bytes kept serving until link expiry — the widened read impersonates the owner, and the
/// photo gates short-circuit on owner identity with no lifecycle clause.
/// </summary>
public class ShareReadControllerTests
{
    private const int ActivityId = 42;

    private static ShareGrant ActivityGrant() =>
        new(TokenId: 1, ShareKind.Activity, ActivityId, "owner", DateTime.UtcNow.AddHours(1));

    private static ShareReadController Controller(bool activityVisible, FakePhotoQuery? photos = null) =>
        new(new FakeAccess(ActivityGrant()), photos ?? new FakePhotoQuery(), new FakeActivityQuery(activityVisible))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    [Fact]
    public async Task Photos_404_WhenTheActivityIsNoLongerVisible()
    {
        // "No longer visible" under the widened OWNER identity means non-Released (trashed/parked/in-work):
        // VisibleActivities admits an owner's activity only while Released — the share page's own 404 rule.
        var result = await Controller(activityVisible: false).Photos(CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Photos_ServesTheList_WhileTheActivityIsReleased()
    {
        var result = await Controller(activityVisible: true).Photos(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<PhotoListResponse>(ok.Value);
    }

    [Fact]
    public async Task Photos_RequestsExactlyTheDocumentedCap()
    {
        // The share payload bounds are a design decision, not an accident —
        // pin the literal so a future edit to either place is forced to reconcile with the other.
        var photos = new FakePhotoQuery();

        await Controller(activityVisible: true, photos).Photos(CancellationToken.None);

        Assert.Equal(ShareReadController.MaxPhotosPerActivity, photos.RequestedPageSize);
    }

    [Fact]
    public async Task PhotoImage_404_WhenTheActivityIsNoLongerVisible()
    {
        var photos = new FakePhotoQuery();

        var result = await Controller(activityVisible: false, photos).PhotoImage(1, "thumb", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        // Ordering pin: the visibility gate sits BEFORE any photo lookup, so a trashed/invisible activity
        // 404s without the membership guard OR the byte fetch ever running — image bytes for a trashed
        // activity must not serve just because the bytes still exist on disk.
        Assert.False(photos.PhotoMembershipAsked);
        Assert.False(photos.ImagePathAsked);
    }

    [Fact]
    public async Task PhotoImage_404_WhenThePhotoBelongsToAnotherActivity()
    {
        // The cross-tenant guard: a live activity token (granted activity 42) asks for a photo that lives
        // on a DIFFERENT activity. The membership check must 404 it — and it must do so BEFORE the bytes are
        // fetched. The servable path is seeded non-null so that dropping the guard would actually leak
        // activity B's bytes to this anonymous viewer (not merely fall through to a null-path 404).
        var photos = new FakePhotoQuery
        {
            PhotoInActivity = (_, _) => false,               // photo 999 is not in activity 42
            ServableImagePath = "/srv/photos/other/thumb.jpg", // ...but its bytes exist and could be served
        };

        var result = await Controller(activityVisible: true, photos).PhotoImage(999, "thumb", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.True(photos.PhotoMembershipAsked);   // the guard WAS consulted
        Assert.False(photos.ImagePathAsked);        // and short-circuited before any byte fetch
    }

    [Fact]
    public async Task PhotoImage_ServesTheFile_WhenThePhotoBelongsToTheGrantedActivity()
    {
        // Success path — without this a fix that 404s everything would still pass the guard test above.
        var photos = new FakePhotoQuery
        {
            PhotoInActivity = (photoId, trackId) => photoId == 7 && trackId == ActivityId,
            ServableImagePath = "/srv/photos/42/thumb.jpg",
        };

        var result = await Controller(activityVisible: true, photos).PhotoImage(7, "thumb", CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("/srv/photos/42/thumb.jpg", file.FileName);
        Assert.Equal("image/jpeg", file.ContentType);
    }

    // ---- fakes -------------------------------------------------------------------------------------

    private sealed class FakeAccess(ShareGrant? grant) : IShareLinkAccess
    {
        public Task<ShareGrant?> ResolveAsync(string token, CancellationToken ct) => Task.FromResult(grant);
        public Task<ShareGrant?> CurrentAsync(CancellationToken ct) => Task.FromResult(grant);

        public async Task<T?> WidenAsync<T>(ShareKind kind, int resourceId, Func<Task<T?>> read, CancellationToken ct)
            where T : class
            => grant is null || !grant.Covers(kind, resourceId) ? null : await read();
    }

    private sealed class FakePhotoQuery : IPhotoQueryService
    {
        public bool ImagePathAsked;
        public bool PhotoMembershipAsked;
        public int RequestedPageSize;

        /// <summary>The cross-tenant guard's answer, keyed by (photoId, trackId). Defaults permissive so
        /// every prior test keeps its behaviour; seed a false to drive the "photo from another activity"
        /// 404 branch that no test used to reach.</summary>
        public Func<int, int, bool> PhotoInActivity = (_, _) => true;

        /// <summary>Path handed back once the guards pass. Null (the default) preserves the old "no file →
        /// 404" behaviour; a real path lets a success-path test see the bytes actually served.</summary>
        public string? ServableImagePath;

        public Task<PhotoListResponse> ListAsync(
            TrackId? trackId, DateOnly? from, DateOnly? to, string sort, int page, int pageSize,
            PhotoFieldScope scope, CancellationToken ct = default)
        {
            RequestedPageSize = pageSize;
            return Task.FromResult(new PhotoListResponse(0, page, pageSize, Array.Empty<PhotoDto>()));
        }

        public Task<bool> IsPhotoInActivityAsync(PhotoId photoId, TrackId trackId, CancellationToken ct = default)
        {
            PhotoMembershipAsked = true;
            return Task.FromResult(PhotoInActivity(photoId.Value, trackId.Value));
        }

        public Task<PhotoMarkersResponse> GetMapMarkersAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string?> GetServableImagePathAsync(PhotoId photoId, PhotoTier tier, CancellationToken ct = default)
        {
            ImagePathAsked = true;
            return Task.FromResult(ServableImagePath);
        }
    }

    /// <summary>Only <see cref="IsActivityVisibleAsync"/> is real — it is the one seam the controller may
    /// consult; everything else throwing keeps the test honest about what the gate depends on.</summary>
    private sealed class FakeActivityQuery(bool visible) : IActivityQueryService
    {
        public Task<bool> IsActivityVisibleAsync(TrackId trackId) => Task.FromResult(visible);

        public Task<List<Activity>> GetAllActivitiesAsync() => throw new NotImplementedException();
        public Task<Activity?> GetActivityMetadataByIdAsync(int id) => throw new NotImplementedException();
        public Task<Activity?> GetOwnedActivityMetadataByIdAsync(int id) => throw new NotImplementedException();
        public Task<bool> IsActivityOwnedAsync(TrackId trackId) => throw new NotImplementedException();
        public Task<Dictionary<int, ActivityMapSlice>> GetActivitySlicesByIdsAsync(List<int> trackIds, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<ActivityEndpoint>> GetActivityEndpointsAsync(List<int> trackIds, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<string>> GetActivityTypesAsync() => throw new NotImplementedException();
        public Task<List<int>> GetTrackIdsContainingPointAsync(double lat, double lng, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> GetVisibleActivityCountAsync() => throw new NotImplementedException();
        public Task<Dictionary<TrackId, int>> GetPhotoCountsAsync(IReadOnlyCollection<TrackId> trackIds, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<(List<Activity> Items, int TotalCount)> GetPagedActivitiesAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string sortColumn, string sortDirection,
            int page, int pageSize, string? ownerUserId = null, IReadOnlyList<string>? activityTypes = null,
            GridFilterCollection? filters = null, IReadOnlyList<string>? ownerUserIds = null,
            bool ownerMatchNone = false, IReadOnlyCollection<TrackId>? restrictIds = null, int? knownCount = null)
            => throw new NotImplementedException();
        public Task<List<TrackId>> GetFilteredActivityIdsAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string? ownerUserId = null,
            IReadOnlyList<string>? activityTypes = null, GridFilterCollection? filters = null,
            IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false,
            IReadOnlyCollection<TrackId>? restrictIds = null)
            => throw new NotImplementedException();
        public Task<List<Activity>> GetActivitiesWithGpsCoordinatesAsync(int limit = 100)
            => throw new NotImplementedException();
        public Task<ActivityStatistics> GetStatisticsAsync(
            string? activityType = null, DateTime? startDate = null, DateTime? endDate = null, string? ownerUserId = null)
            => throw new NotImplementedException();
    }
}
