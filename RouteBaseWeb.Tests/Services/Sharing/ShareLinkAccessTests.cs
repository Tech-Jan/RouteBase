using System.Globalization;
using System.Security.Claims;
using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// The grant seam's security properties. Each test names the property it pins; a failure here is a
/// vulnerability, not a cosmetic regression.
/// </summary>
public class ShareLinkAccessTests : IAsyncLifetime
{
    private const string Owner = "owner-user";

    private RouteBaseDbContext _db = null!;
    private FixedHttpContextAccessor _http = null!;
    private CurrentUserService _currentUser = null!;
    private ShareLinkAccess _access = null!;

    /// <summary>
    /// Plain-field accessor. The framework's HttpContextAccessor keeps the context in a static AsyncLocal,
    /// which does NOT flow from xUnit's InitializeAsync into the test body — the context reads back null.
    /// </summary>
    /// <summary>Fixed fake now (K2 seam): expiry/revocation math runs against this, not the wall clock.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock = new(T0);

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:shareaccess_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _db = new RouteBaseDbContext(options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.EnsureCreatedAsync();

        // FKs are enforced in this stack (the bundled e_sqlite3 is built with SQLITE_DEFAULT_FOREIGN_KEYS),
        // and Share_Link cascades off both Activity and Segment — so the parent rows must exist before a
        // link can point at them. Seeding ids 1/42/99, the ones these tests share.
        _db.Users.Add(new AppUser { UserId = Owner, DisplayName = Owner, Email = "o@t.com", IsAdmin = false });
        foreach (var id in new[] { 1, 42, 99 })
        {
            _db.Activities.Add(new Activity { TrackId = new TrackId(id), UserId = Owner, TrackName = $"act{id}" });
            _db.Segments.Add(new Segment { SegmentId = new SegmentId(id), UserId = Owner, SegmentName = $"seg{id}" });
        }
        await _db.SaveChangesAsync();
        // Races cascade off an Activity, so they are seeded after it — same ids, since a race link's
        // resource id lives in its own column and may legitimately collide with a track/segment id.
        foreach (var id in new[] { 1, 42, 99 })
            _db.Races.Add(new Race { RaceId = new RaceId(id), UserId = Owner, RaceName = $"race{id}", CourseTrackId = new TrackId(id) });
        await _db.SaveChangesAsync();

        _http = new FixedHttpContextAccessor { HttpContext = new DefaultHttpContext() };
        _currentUser = new CurrentUserService(_http, new ConfigurationBuilder().Build());
        _access = new ShareLinkAccess(_db, _currentUser, _http, _clock);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ---- helpers ----

    private async Task<string> MintAsync(
        ShareKind kind = ShareKind.Activity,
        int resourceId = 1,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null,
        string owner = Owner)
    {
        var token = ShareTokens.New();
        _db.ShareLinks.Add(new ShareLink
        {
            TokenHash = ShareTokens.Hash(token),
            Kind = kind == ShareKind.Activity ? ShareKindNames.Activity : ShareKindNames.Segment,
            TrackId = kind == ShareKind.Activity ? new TrackId(resourceId) : (TrackId?)null,
            SegmentId = kind == ShareKind.Segment ? new SegmentId(resourceId) : (SegmentId?)null,
            UserId = owner,
            CreatedAt = T0.UtcDateTime,
            ExpiresAt = expiresAt ?? T0.UtcDateTime.AddDays(7),
            RevokedAt = revokedAt,
        });
        await _db.SaveChangesAsync();
        return token;
    }

    /// <summary>Puts a share principal for <paramref name="tokenId"/> on the ambient HttpContext.</summary>
    private void SignInAsShare(int tokenId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ShareLinkDefaults.ViewerUserId),
            new Claim(ShareLinkDefaults.TokenIdClaim, tokenId.ToString(CultureInfo.InvariantCulture)),
        ], ShareLinkDefaults.AuthenticationScheme);
        _http.HttpContext!.User = new ClaimsPrincipal(identity);
    }

    private async Task<int> TokenIdOfAsync(string token) =>
        (await _access.ResolveAsync(token, default))!.TokenId;

    // ---- resolve ----

    [Fact]
    public async Task Resolve_LiveToken_YieldsGrantForTheOwnerAndResource()
    {
        var token = await MintAsync(ShareKind.Activity, resourceId: 42);

        var grant = await _access.ResolveAsync(token, default);

        Assert.NotNull(grant);
        Assert.Equal(ShareKind.Activity, grant!.Kind);
        Assert.Equal(42, grant.ResourceId);
        Assert.Equal(Owner, grant.OwnerUserId);
    }

    [Fact]
    public async Task Resolve_UnknownToken_IsNull()
        => Assert.Null(await _access.ResolveAsync(ShareTokens.New(), default));

    [Fact]
    public async Task Resolve_LiveToken_YieldsLiveRecordingGrantForTheTrack()
    {
        // A "live" row reuses the track_id column but must resolve to a LiveRecording grant, never Activity.
        var token = ShareTokens.New();
        _db.ShareLinks.Add(new ShareLink
        {
            TokenHash = ShareTokens.Hash(token),
            Kind = ShareKindNames.Live,
            TrackId = new TrackId(42),
            SegmentId = null,
            UserId = Owner,
            CreatedAt = T0.UtcDateTime,
            ExpiresAt = T0.UtcDateTime.AddDays(1),
        });
        await _db.SaveChangesAsync();

        var grant = await _access.ResolveAsync(token, default);

        Assert.NotNull(grant);
        Assert.Equal(ShareKind.LiveRecording, grant!.Kind);
        Assert.Equal(42, grant.ResourceId);
        Assert.Equal(Owner, grant.OwnerUserId);
        // Covers is kind-aware: a live grant does not open the activity detail page for the same id.
        Assert.True(grant.Covers(ShareKind.LiveRecording, 42));
        Assert.False(grant.Covers(ShareKind.Activity, 42));
    }

    [Fact]
    public async Task Resolve_RaceToken_YieldsRaceGrantForTheRace_NotTheCourseActivity()
    {
        // A race row keys on race_id. Its id space overlaps the track id space, so the resolver must read
        // the RIGHT column — a race grant must never open the course Activity's detail page.
        var token = ShareTokens.New();
        _db.ShareLinks.Add(new ShareLink
        {
            TokenHash = ShareTokens.Hash(token),
            Kind = ShareKindNames.Race,
            RaceId = new RaceId(42),
            UserId = Owner,
            CreatedAt = T0.UtcDateTime,
            ExpiresAt = T0.UtcDateTime.AddDays(1),
        });
        await _db.SaveChangesAsync();

        var grant = await _access.ResolveAsync(token, default);

        Assert.NotNull(grant);
        Assert.Equal(ShareKind.Race, grant!.Kind);
        Assert.Equal(42, grant.ResourceId);
        Assert.True(grant.Covers(ShareKind.Race, 42));
        Assert.False(grant.Covers(ShareKind.Activity, 42));
        Assert.False(grant.Covers(ShareKind.Race, 99));
    }

    [Fact]
    public async Task Resolve_RaceRowWithNoRaceId_IsNull()
    {
        // kind=race but the id sits in someone else's column → malformed, grants nothing.
        _db.ShareLinks.Add(new ShareLink
        {
            TokenHash = ShareTokens.Hash("race-x"),
            Kind = ShareKindNames.Race,
            RaceId = null,
            TrackId = new TrackId(42),   // a race row must never key on a track
            UserId = Owner,
            CreatedAt = T0.UtcDateTime,
            ExpiresAt = T0.UtcDateTime.AddDays(1),
        });
        await _db.SaveChangesAsync();

        // Through the PK path, like the live case below: ResolveAsync shape-checks the token before it
        // hashes anything, so a non-token string would pass this assertion without ever reading the row.
        var row = await _db.ShareLinks.FirstAsync();
        SignInAsShare(row.ShareLinkId);

        Assert.Null(await _access.CurrentAsync(default));
    }

    [Fact]
    public async Task Resolve_LiveRowWithNoTrackId_IsNull()
    {
        // kind=live but no track_id → malformed, grants nothing (same rule as the activity/segment mismatch).
        _db.ShareLinks.Add(new ShareLink
        {
            TokenHash = ShareTokens.Hash("live-x"),
            Kind = ShareKindNames.Live,
            TrackId = null,
            SegmentId = new SegmentId(42),   // a live row must never key on a segment
            UserId = Owner,
            CreatedAt = T0.UtcDateTime,
            ExpiresAt = T0.UtcDateTime.AddDays(1),
        });
        await _db.SaveChangesAsync();

        var row = await _db.ShareLinks.FirstAsync();
        SignInAsShare(row.ShareLinkId);

        Assert.Null(await _access.CurrentAsync(default));
    }

    [Fact]
    public async Task Resolve_MalformedToken_IsNull_WithoutTouchingTheDatabase()
    {
        // Property: unbounded attacker input never reaches SHA-256 or a query.
        Assert.Null(await _access.ResolveAsync("", default));
        Assert.Null(await _access.ResolveAsync("short", default));
        Assert.Null(await _access.ResolveAsync(new string('a', 5000), default));
        Assert.Null(await _access.ResolveAsync(new string('!', ShareTokens.TokenLength), default));
    }

    [Fact]
    public async Task Resolve_ExpiredToken_IsNull()
    {
        var token = await MintAsync(expiresAt: T0.UtcDateTime.AddSeconds(-1));

        Assert.Null(await _access.ResolveAsync(token, default));
    }

    [Fact]
    public async Task Resolve_RevokedToken_IsNull_OnTheSameTick()
    {
        // Property: revocation takes effect immediately. There is deliberately no grant cache, so there
        // is no window in which a revoked link still works.
        var token = await MintAsync();
        Assert.NotNull(await _access.ResolveAsync(token, default));

        var row = await _db.ShareLinks.FirstAsync();
        row.RevokedAt = T0.UtcDateTime;
        await _db.SaveChangesAsync();

        Assert.Null(await _access.ResolveAsync(token, default));
    }

    [Fact]
    public async Task Resolve_RowWhoseKindAndForeignKeyDisagree_IsNull()
    {
        // A malformed row grants nothing rather than defaulting to some resource.
        _db.ShareLinks.Add(new ShareLink
        {
            TokenHash = ShareTokens.Hash("x"),
            Kind = ShareKindNames.Activity,
            TrackId = null,           // says activity, carries no activity
            SegmentId = new SegmentId(42),
            UserId = Owner,
            CreatedAt = T0.UtcDateTime,
            ExpiresAt = T0.UtcDateTime.AddDays(1),
        });
        await _db.SaveChangesAsync();

        var row = await _db.ShareLinks.FirstAsync();
        SignInAsShare(row.ShareLinkId);

        Assert.Null(await _access.CurrentAsync(default));
    }

    // ---- widening ----

    [Fact]
    public async Task Widen_CoveredResource_RunsTheReadAsTheOwner()
    {
        var token = await MintAsync(ShareKind.Activity, resourceId: 42);
        SignInAsShare(await TokenIdOfAsync(token));

        var seenAs = await _access.WidenAsync(ShareKind.Activity, 42,
            () => Task.FromResult<string?>(_currentUser.GetUserId()), default);

        Assert.Equal(Owner, seenAs);
    }

    [Fact]
    public async Task Widen_RestoresTheAnonymousIdentityAfterwards()
    {
        // Property: the widened identity cannot escape into the view render, where Details.cshtml.cs's
        // IsOwner check would then render owner controls on an anonymous page.
        var token = await MintAsync(ShareKind.Activity, resourceId: 42);
        SignInAsShare(await TokenIdOfAsync(token));

        await _access.WidenAsync(ShareKind.Activity, 42,
            () => Task.FromResult<string?>(_currentUser.GetUserId()), default);

        Assert.Equal(ShareLinkDefaults.ViewerUserId, _currentUser.GetUserId());
    }

    [Fact]
    public async Task Widen_RestoresIdentity_EvenWhenTheReadThrows()
    {
        var token = await MintAsync(ShareKind.Activity, resourceId: 42);
        SignInAsShare(await TokenIdOfAsync(token));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _access.WidenAsync<string>(ShareKind.Activity, 42,
                () => throw new InvalidOperationException("read blew up"), default));

        Assert.Equal(ShareLinkDefaults.ViewerUserId, _currentUser.GetUserId());
    }

    [Fact]
    public async Task Widen_DifferentResourceId_DoesNotWiden_AndNeverImpersonates()
    {
        // Property: a grant for track 42 cannot widen track 99 (grant confusion). The read must not run
        // at all — asserted by having it record whether it was invoked.
        var token = await MintAsync(ShareKind.Activity, resourceId: 42);
        SignInAsShare(await TokenIdOfAsync(token));

        var ran = false;
        var result = await _access.WidenAsync(ShareKind.Activity, 99, () =>
        {
            ran = true;
            return Task.FromResult<string?>("leaked");
        }, default);

        Assert.Null(result);
        Assert.False(ran);
        Assert.Equal(ShareLinkDefaults.ViewerUserId, _currentUser.GetUserId());
    }

    [Fact]
    public async Task Widen_WrongKindSameId_DoesNotWiden()
    {
        // An activity token must not open the segment with the same numeric id.
        var token = await MintAsync(ShareKind.Activity, resourceId: 42);
        SignInAsShare(await TokenIdOfAsync(token));

        var ran = false;
        var result = await _access.WidenAsync(ShareKind.Segment, 42, () =>
        {
            ran = true;
            return Task.FromResult<string?>("leaked");
        }, default);

        Assert.Null(result);
        Assert.False(ran);
    }

    [Fact]
    public async Task Widen_WithoutASharePrincipal_DoesNotWiden()
    {
        // A normal (non-share) request carries no grant, so the seam is inert for everyone else.
        await MintAsync(ShareKind.Activity, resourceId: 42);

        var ran = false;
        var result = await _access.WidenAsync(ShareKind.Activity, 42, () =>
        {
            ran = true;
            return Task.FromResult<string?>("leaked");
        }, default);

        Assert.Null(result);
        Assert.False(ran);
    }

    [Fact]
    public async Task Widen_AfterRevocation_DoesNotWiden()
    {
        // The principal was minted while the link was live; revoking must strip its authority mid-session.
        var token = await MintAsync(ShareKind.Activity, resourceId: 42);
        SignInAsShare(await TokenIdOfAsync(token));

        var row = await _db.ShareLinks.FirstAsync();
        row.RevokedAt = T0.UtcDateTime;
        await _db.SaveChangesAsync();

        var fresh = new ShareLinkAccess(_db, _currentUser, _http, _clock);   // new request scope
        var ran = false;
        var result = await fresh.WidenAsync(ShareKind.Activity, 42, () =>
        {
            ran = true;
            return Task.FromResult<string?>("leaked");
        }, default);

        Assert.Null(result);
        Assert.False(ran);
    }
}
