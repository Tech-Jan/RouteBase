using System.Globalization;
using System.Security.Claims;
using RouteBaseWeb.Api;
using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Photos;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// The isolation suite the activity share path never had — the twin of
/// <see cref="ShareSegmentReaderIsolationTests"/> / <see cref="ShareRaceReaderIsolationTests"/>, which the
/// segment and race kinds already carry. The activity kind is the only one that reaches its data through
/// <c>ShareLinkAccess.WidenAsync</c> (impersonation) rather than an owner-scoped-by-construction reader, so
/// it is the one kind whose photo path had no end-to-end anonymous-visitor test.
///
/// Unlike the segment/race isolation tests, these pin BEHAVIOUR, not source structure — the activity path is
/// a controller over shared services, not a single reader whose constructor can be interrogated. Every
/// property here is stated in terms of what an anonymous <c>/s/{token}/api/photos</c> request returns, so it
/// survives however the read is later re-plumbed (e.g. a later convergence that would move this
/// path off <c>WidenAsync</c> is blocked on out-of-scope files; these guards must hold before AND after it).
///
/// The whole suite runs with a NULL <see cref="IFriendshipService"/> on the <see cref="VisibilityService"/>.
/// That is deliberate and load-bearing: it is the "single user" property made mechanical. The share read of
/// an owner's own activity must never consult the friend graph, so a friend service that does not exist must
/// never be dereferenced. A future edit that routes the share photo read through the friend graph turns this
/// whole file into NullReferenceExceptions — which is the intended alarm.
/// </summary>
public class ShareActivityReaderIsolationTests : IAsyncLifetime
{
    private const string Owner = "owner-user";
    private const int PrivateActivityId = 42;   // shared; private + released — links authorize on OWNERSHIP, not visibility
    private const int OtherActivityId   = 99;   // a DIFFERENT activity, never named by the token under test

    private RouteBaseDbContext _db = null!;
    private DefaultHttpContext _http = null!;
    private CurrentUserService _currentUser = null!;
    private ShareLinkAccess _access = null!;
    private ShareReadController _controller = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:shareactivity_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _db = new RouteBaseDbContext(options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.EnsureCreatedAsync();

        _db.Users.Add(new AppUser { UserId = Owner, DisplayName = Owner, Email = "o@t.com", IsAdmin = false });

        // The shared activity is PRIVATE: a share link is authorized on ownership, not visibility, so its
        // photos must serve to an anonymous visitor even though "private" would hide them from every
        // signed-in non-owner. This is exactly the row the impersonation gate lets through today.
        _db.Activities.Add(new Activity
        {
            TrackId = new TrackId(PrivateActivityId), UserId = Owner, TrackName = "private ride",
            Visibility = Visibilities.Private, Lifecycle = LifecycleStates.Released,
        });
        // A second activity the token under test does NOT cover. Its photo must never appear in the list —
        // the trackId is taken from the grant, not from any caller-supplied parameter.
        _db.Activities.Add(new Activity
        {
            TrackId = new TrackId(OtherActivityId), UserId = Owner, TrackName = "other ride",
            Visibility = Visibilities.Public, Lifecycle = LifecycleStates.Released,
        });
        await _db.SaveChangesAsync();

        _db.Photos.Add(Photo(1, new TrackId(PrivateActivityId)));
        _db.Photos.Add(Photo(2, new TrackId(PrivateActivityId)));
        _db.Photos.Add(Photo(3, new TrackId(OtherActivityId)));   // the cross-tenant bait
        await _db.SaveChangesAsync();

        // ONE HttpContext threads through everything, exactly as in production: ShareLinkAccess reads the
        // share principal off IHttpContextAccessor, and the controller writes ShareResponseHeaders to the
        // same Response.
        _http = new DefaultHttpContext();
        var accessor = new FixedHttpContextAccessor { HttpContext = _http };
        _currentUser = new CurrentUserService(accessor, new ConfigurationBuilder().Build());

        // friendship service = null!  → the single-user property, mechanically enforced (see class remarks).
        var visibility = new VisibilityService(_currentUser, null!, _db);
        var photoQuery = new PhotoQueryService(_db, visibility, _currentUser, new NullPhotoImageStore());
        var activityQuery = new ActivityQueryService(
            _db, visibility, new MemoryCache(new MemoryCacheOptions()), new UserCacheInvalidator());

        _access = new ShareLinkAccess(_db, _currentUser, accessor, TimeProvider.System);
        _controller = new ShareReadController(_access, photoQuery, activityQuery)
        {
            ControllerContext = new ControllerContext { HttpContext = _http },
        };
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ---- the properties ---------------------------------------------------------------------------

    [Fact]
    public async Task ActivityToken_ListsOnlyThatActivitysPhotos_NeverAnothersAndNotEvenPrivateOnesAreHidden()
    {
        // Two properties in one honest request:
        //   • a PRIVATE released activity's photos DO serve on its share link (2 rows on track 42), and
        //   • activity 99's photo (id 3) never leaks — the token names 42, the read cannot be pointed elsewhere.
        SignInWith(MintActivityLink(new TrackId(PrivateActivityId)));

        var result = await _controller.Photos(CancellationToken.None);

        var page = Assert.IsType<PhotoListResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, p => Assert.Equal(PrivateActivityId, p.TrackId));
    }

    [Fact]
    public async Task PrivateActivity_ServesPhotos_WithNoFriendGraphInThePicture_TheSingleUserProperty()
    {
        // Stated as its own test because it is the property this whole convergence exists to protect: an
        // owner who has no friends at all — indeed no friendship service wired — still shares their private
        // ride's photos. The read is a question about ONE row's owner + lifecycle, never about a social graph.
        // (There is no "signed-in friend visits the link" variant to assert here: a /s/{token} request is
        // always the share:anon principal, never an authenticated friend — the endpoint has no other caller.)
        Assert.Empty(_db.Friendships);   // anti-vacuity: prove the "no friends" premise actually holds

        SignInWith(MintActivityLink(new TrackId(PrivateActivityId)));

        var page = Assert.IsType<PhotoListResponse>(
            Assert.IsType<OkObjectResult>(await _controller.Photos(CancellationToken.None)).Value);
        Assert.NotEmpty(page.Items);
    }

    [Theory]
    [InlineData(LifecycleStates.Trashed)]   // in the trash
    [InlineData(LifecycleStates.InWork)]    // parked, or awaiting review (the reason differs; the gate does not)
    public async Task NonReleasedActivity_404sThePhotoList_EvenThoughThePhotoRowsStillExist(string lifecycle)
    {
        // THE regression the impersonation gate was added to prevent (ShareReadControllerTests documents it):
        // trashing or parking a shared ride must cut its photos off the link, exactly as it removes the
        // activity from the share PAGE — the bytes surviving on disk must not keep the list alive. Released is
        // the only visible state (LifecycleStates.All = InWork/Released/Trashed), so both non-released ones 404.
        var act = await _db.Activities.SingleAsync(a => a.TrackId == new TrackId(PrivateActivityId));
        act.Lifecycle = lifecycle;
        await _db.SaveChangesAsync();

        SignInWith(MintActivityLink(new TrackId(PrivateActivityId)));

        Assert.IsType<NotFoundResult>(await _controller.Photos(CancellationToken.None));
    }

    [Fact]
    public async Task RevokedToken_ServesNothing()
    {
        SignInWith(MintActivityLink(new TrackId(PrivateActivityId), revokedAt: DateTime.UtcNow.AddMinutes(-1)));

        Assert.IsType<NotFoundResult>(await _controller.Photos(CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredToken_ServesNothing()
    {
        SignInWith(MintActivityLink(new TrackId(PrivateActivityId), expiresAt: DateTime.UtcNow.AddSeconds(-1)));

        Assert.IsType<NotFoundResult>(await _controller.Photos(CancellationToken.None));
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static Photo Photo(int id, TrackId trackId) => new()
    {
        PhotoId = new PhotoId(id), TrackId = trackId, UserId = Owner,
        OnMap = true, Lat = 47.1, Lng = 8.2, ContentHash = $"hash{id:D12}",
    };

    /// <summary>Inserts a live (or deliberately dead) activity share link and returns its row id — the value
    /// that rides in the share principal's token-id claim.</summary>
    private int MintActivityLink(TrackId trackId, DateTime? expiresAt = null, DateTime? revokedAt = null)
    {
        var link = new ShareLink
        {
            TokenHash = ShareTokens.Hash(ShareTokens.New()),
            Kind = ShareKindNames.Activity,
            TrackId = trackId,
            UserId = Owner,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(7),
            RevokedAt = revokedAt,
        };
        _db.ShareLinks.Add(link);
        _db.SaveChanges();
        return link.ShareLinkId;
    }

    /// <summary>Puts the anonymous share principal for <paramref name="tokenId"/> on the ambient context —
    /// the exact identity ShareLinkAuthenticationHandler mints. Note it is NOT the owner: impersonation, if
    /// any, happens only transiently inside WidenAsync.</summary>
    private void SignInWith(int tokenId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ShareLinkDefaults.ViewerUserId),
            new Claim(ShareLinkDefaults.TokenIdClaim, tokenId.ToString(CultureInfo.InvariantCulture)),
        ], ShareLinkDefaults.AuthenticationScheme);
        _http.User = new ClaimsPrincipal(identity);
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>The list path never opens image bytes, so the store is a no-op — its presence only satisfies
    /// the PhotoQueryService constructor.</summary>
    private sealed class NullPhotoImageStore : IPhotoImageStore
    {
        public Task PutAsync(PhotoId photoId, PhotoTier tier, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => Task.CompletedTask;
        public Stream? OpenRead(PhotoId photoId, PhotoTier tier) => null;
        public string GetPhysicalPath(PhotoId photoId, PhotoTier tier) => "/dev/null";
        public Task DeleteAsync(PhotoId photoId, CancellationToken ct = default) => Task.CompletedTask;
        public IEnumerable<int> EnumerateStoredIds() => Array.Empty<int>();
    }
}
