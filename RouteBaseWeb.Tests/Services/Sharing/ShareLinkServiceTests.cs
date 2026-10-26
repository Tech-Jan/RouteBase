using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sharing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// Minting, listing and revoking. The properties here are about who may publish what, and about the
/// plaintext token existing exactly once.
/// </summary>
public class ShareLinkServiceTests : IAsyncLifetime
{
    private const string Owner = "owner-user";
    private const string Stranger = "stranger-user";

    private RouteBaseDbContext _db = null!;
    private ShareLinkService _service = null!;
    private DbBackedRecording _recording = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:sharesvc_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _db = new RouteBaseDbContext(options);
        await _db.Database.OpenConnectionAsync();
        await _db.Database.EnsureCreatedAsync();

        foreach (var id in new[] { Owner, Stranger })
            _db.Users.Add(new AppUser { UserId = id, DisplayName = id, Email = $"{id}@t.com", IsAdmin = false });

        // 1/2 owned by Owner; 90 owned by a stranger but PUBLIC — the visibility-vs-ownership case.
        _db.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Owner, TrackName = "mine" });
        _db.Segments.Add(new Segment { SegmentId = new SegmentId(2), UserId = Owner, SegmentName = "my segment" });
        _db.Activities.Add(new Activity
        {
            TrackId = new TrackId(90), UserId = Stranger, TrackName = "theirs", Visibility = "public",
        });
        // Race 3 is the Owner's; race 91 belongs to the stranger but is PUBLIC — the same
        // visibility-vs-ownership case as activity 90, on the race axis.
        _db.Races.Add(new Race { RaceId = new RaceId(3), UserId = Owner, RaceName = "my race", CourseTrackId = new TrackId(1) });
        _db.Races.Add(new Race
        {
            RaceId = new RaceId(91), UserId = Stranger, RaceName = "theirs", CourseTrackId = new TrackId(90), Visibility = "public",
        });
        await _db.SaveChangesAsync();

        _recording = new DbBackedRecording(_db);
        var visibility = new TestVisibilityService(Owner, _db);
        _service = new ShareLinkService(
            _db, visibility, _recording, new RouteBaseWeb.Services.Races.RaceAccess(_db, visibility), TimeProvider.System);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>Real Owned*/Visible* semantics over the test DB, scoped to a fixed user.</summary>
    private sealed class TestVisibilityService(string userId, RouteBaseDbContext db)
        : RouteBaseWeb.Services.Interfaces.IVisibilityService
    {
        public string GetCurrentUserId() => userId;
        public Task<List<string>> GetVisibleUserIdsAsync() => Task.FromResult(new List<string> { userId });
        public Task<bool> CanViewItemAsync(string ownerUserId, string? visibility)
            => Task.FromResult(ownerUserId == userId || visibility == "public");
        // Permissive is deliberate and unreachable here: ShareLinkService's race mint gates on
        // RaceAccess.OwnsRaceAsync (explicit-identity ownership), never CanViewActivityAsync, and no other
        // mint/list/revoke path touches this seam — so no test in this suite can drive its deny branch.
        public Task<bool> CanViewActivityAsync(TrackId trackId) => Task.FromResult(true);
        public IQueryable<Activity> VisibleActivities(IQueryable<Activity> q)
            => q.Where(a => a.UserId == userId || a.Visibility == "public");
        public IQueryable<Segment> VisibleSegments(IQueryable<Segment> q)
            => q.Where(s => s.UserId == userId || s.Visibility == "public");
        public IQueryable<MultiDayTour> VisibleTours(IQueryable<MultiDayTour> q)
            => q.Where(t => t.UserId == userId);
        public IQueryable<Activity> OwnedActivities(IQueryable<Activity> q) => q.Where(a => a.UserId == userId);
        public IQueryable<Segment> OwnedSegments(IQueryable<Segment> q) => q.Where(s => s.UserId == userId);
        public IQueryable<MultiDayTour> OwnedTours(IQueryable<MultiDayTour> q) => q.Where(t => t.UserId == userId);
        public IQueryable<AppCredentials> OwnedCredentials(IQueryable<AppCredentials> q)
            => q.Where(c => c.UserId == userId);
    }

    // ---- ownership ----

    [Fact]
    public async Task Mint_ForAnOwnedActivity_Succeeds()
    {
        var result = await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);

        Assert.Equal(ShareMintOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Token);
        Assert.True(ShareTokens.IsWellFormed(result.Token));
    }

    [Fact]
    public async Task Mint_ForSomeoneElsesPublicActivity_Is404()
    {
        // THE ownership property: being able to SEE a public ride must not let you publish it to the
        // internet on its owner's behalf. Visibility is not permission to share.
        var result = await _service.MintAsync(ShareKindNames.Activity, 90, "7d", Owner, default);

        Assert.Equal(ShareMintOutcome.NotFound, result.Outcome);
        Assert.Null(result.Token);
        Assert.Empty(await _db.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task Mint_ForANonexistentResource_IsTheSame404AsNotYours()
    {
        // Same outcome as the not-yours case, so mint cannot be used to probe which ids exist.
        var missing = await _service.MintAsync(ShareKindNames.Activity, 12345, "7d", Owner, default);
        var notMine = await _service.MintAsync(ShareKindNames.Activity, 90, "7d", Owner, default);

        Assert.Equal(notMine.Outcome, missing.Outcome);
    }

    // ---- expiry is mandatory and closed ----

    [Theory]
    [InlineData("24h", 24)]
    [InlineData("7d", 24 * 7)]
    [InlineData("30d", 24 * 30)]
    public async Task Mint_AppliesThePresetLifetime(string ttl, int expectedHours)
    {
        var before = DateTime.UtcNow;
        var result = await _service.MintAsync(ShareKindNames.Activity, 1, ttl, Owner, default);

        var actualHours = (result.ExpiresAt - before).TotalHours;
        Assert.InRange(actualHours, expectedHours - 1, expectedHours + 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("forever")]
    [InlineData("100y")]
    [InlineData("9999-12-31T23:59:59Z")]   // a timestamp is not a preset name
    public async Task Mint_RejectsAnythingOutsideThePresets(string? ttl)
    {
        // Pins "expiry is mandatory": there is no code path that accepts a caller-chosen instant, so a
        // client cannot request a link that never dies.
        var result = await _service.MintAsync(ShareKindNames.Activity, 1, ttl, Owner, default);

        Assert.Equal(ShareMintOutcome.BadRequest, result.Outcome);
        Assert.Empty(await _db.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task Mint_RejectsAnUnknownKind()
    {
        // The example used to be "race" — which is now a real kind, so the LITERAL went stale while the
        // property (an unrecognised kind is a BadRequest, never a 404) is unchanged. "heatmap" is a real
        // resource in this app that is deliberately NOT shareable, so it stays a meaningful example.
        var result = await _service.MintAsync("heatmap", 1, "7d", Owner, default);
        Assert.Equal(ShareMintOutcome.BadRequest, result.Outcome);
    }

    // ---- the plaintext exists once ----

    [Fact]
    public async Task Mint_StoresOnlyTheHash_NeverThePlaintext()
    {
        var result = await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);
        var token = result.Token!;

        var row = await _db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Equal(ShareTokens.Hash(token), row.TokenHash);
        Assert.NotEqual(token, row.TokenHash, StringComparer.Ordinal);

        // Belt and braces: no column anywhere in the row contains the plaintext.
        var serialized = System.Text.Json.JsonSerializer.Serialize(row);
        Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mint_ProducesADistinctTokenEveryTime()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 5; i++)
        {
            var r = await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);
            Assert.True(tokens.Add(r.Token!));
        }
    }

    // ---- listing never leaks a credential ----

    [Fact]
    public async Task List_ReturnsSummariesWithNoTokenAnywhere()
    {
        var minted = await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);

        var links = await ListLinksAsync(ShareKindNames.Activity, 1, Owner);

        var single = Assert.Single(links);
        Assert.True(single.IsActive);
        var serialized = System.Text.Json.JsonSerializer.Serialize(links);
        Assert.DoesNotContain(minted.Token!, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(ShareTokens.Hash(minted.Token!), serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ShowsOnlyTheCallersOwnLinks()
    {
        await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);

        var strangersView = await ListLinksAsync(ShareKindNames.Activity, 1, Stranger);

        Assert.Empty(strangersView);
    }

    [Fact]
    public async Task List_MarksRevokedAndExpiredLinksInactive()
    {
        await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);
        var row = await _db.ShareLinks.SingleAsync();
        row.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await _db.SaveChangesAsync();

        var links = await ListLinksAsync(ShareKindNames.Activity, 1, Owner);

        Assert.False(Assert.Single(links).IsActive);
    }

    // ---- revoke ----

    [Fact]
    public async Task Revoke_MarksTheLinkRevoked()
    {
        var minted = await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);

        Assert.True(await _service.RevokeAsync(minted.Id, Owner, default));

        var row = await _db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.NotNull(row.RevokedAt);
    }

    [Fact]
    public async Task Revoke_ByANonOwner_FailsAndLeavesTheRowUntouched()
    {
        var minted = await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);

        Assert.False(await _service.RevokeAsync(minted.Id, Stranger, default));

        var row = await _db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Null(row.RevokedAt);
    }

    [Fact]
    public async Task Revoke_IsIdempotent_AndKeepsTheOriginalTimestamp()
    {
        var minted = await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);
        await _service.RevokeAsync(minted.Id, Owner, default);
        var first = (await _db.ShareLinks.AsNoTracking().SingleAsync()).RevokedAt;

        Assert.True(await _service.RevokeAsync(minted.Id, Owner, default));

        Assert.Equal(first, (await _db.ShareLinks.AsNoTracking().SingleAsync()).RevokedAt);
    }

    // ---- mint cap ----

    [Fact]
    public async Task Mint_CapsLiveLinksPerResource_ButRevokingFreesASlot()
    {
        for (var i = 0; i < 10; i++)
            Assert.Equal(ShareMintOutcome.Ok,
                (await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default)).Outcome);

        Assert.Equal(ShareMintOutcome.TooMany,
            (await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default)).Outcome);

        // The cap counts LIVE links, so an owner is never permanently locked out of re-sharing.
        var first = await _db.ShareLinks.OrderBy(s => s.ShareLinkId).FirstAsync();
        await _service.RevokeAsync(first.ShareLinkId, Owner, default);

        Assert.Equal(ShareMintOutcome.Ok,
            (await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default)).Outcome);
    }

    // ---- cascade ----

    [Fact]
    public async Task DeletingTheSharedActivity_RemovesItsLinks()
    {
        // Structural revocation: no service code has to remember to clean these up.
        await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);

        _db.Activities.Remove(await _db.Activities.SingleAsync(a => a.TrackId == new TrackId(1)));
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.ShareLinks.ToListAsync());
    }

    /// <summary>
    /// The segment read surface has landed, so this restores the success case that
    /// Mint_ForASegment_IsRefused_WhileThereIsNoSegmentReadSurface stood in for. That refusal pinned a
    /// deliberately temporary contract — a segment link used to be dead the moment it was pasted — and
    /// its own remarks said to restore this when the read surface existed.
    /// </summary>
    [Fact]
    public async Task Mint_ForAnOwnedSegment_Succeeds()
    {
        var result = await _service.MintAsync(ShareKindNames.Segment, 2, "24h", Owner, default);

        Assert.Equal(ShareMintOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Token);

        var row = Assert.Single(await _db.ShareLinks.ToListAsync());
        Assert.Equal(ShareKindNames.Segment, row.Kind);
        // The kind's OWN column is set and the other stays null — the two-nullable-FK shape is what buys
        // ON DELETE CASCADE for both parents, so a polymorphic resource_id would lose structural revocation.
        Assert.Equal(new SegmentId(2), row.SegmentId);
        Assert.Null(row.TrackId);
        // Only the hash is persisted; the plaintext exists once, in this response.
        Assert.NotEqual(result.Token, row.TokenHash, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Mint_ForASegmentSomeoneElseOwns_Is404_NotAnExistenceOracle()
    {
        // Visibility is not permission to share. Not-yours and not-there must be the same answer.
        var notMine = await _service.MintAsync(ShareKindNames.Segment, 2, "24h", "someone-else", default);
        var missing = await _service.MintAsync(ShareKindNames.Segment, 9999, "24h", "someone-else", default);

        Assert.Equal(ShareMintOutcome.NotFound, notMine.Outcome);
        Assert.Equal(missing.Outcome, notMine.Outcome);
        Assert.Empty(await _db.ShareLinks.ToListAsync());
    }

    /// <summary>
    /// The resolver/grant/cascade plumbing for segments stays correct and tested even though minting is
    /// refused — the refusal is one line in MintAsync, not a removal of ShareKind.Segment.
    /// </summary>
    [Fact]
    public async Task ASegmentRow_StillResolvesToASegmentGrant()
    {
        var grant = new ShareGrant(1, ShareKind.Segment, 2, Owner, DateTime.UtcNow.AddHours(1));

        Assert.True(grant.Covers(ShareKind.Segment, 2));
        Assert.False(grant.Covers(ShareKind.Activity, 2));
    }

    // ---- live recording ----

    private async Task SeedRecordingSessionAsync(TrackId trackId, string userId)
    {
        _db.RecordingSessions.Add(new RecordingSession
        {
            SessionId = $"sess-{trackId}",
            TrackId = trackId,
            UserId = userId,
            Status = RecordingSessionStatus.Recording,
            ActivityType = "Run",
            StartedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Mint_ForALiveRecording_Succeeds_AndKeysOnTheTrackIdColumn()
    {
        await SeedRecordingSessionAsync(new TrackId(1), Owner);

        var result = await _service.MintAsync(ShareKindNames.Live, 1, "24h", Owner, default);

        Assert.Equal(ShareMintOutcome.Ok, result.Outcome);
        var row = Assert.Single(await _db.ShareLinks.ToListAsync());
        Assert.Equal(ShareKindNames.Live, row.Kind);
        Assert.Equal(new TrackId(1), row.TrackId);   // live reuses the activity's track_id
        Assert.Null(row.SegmentId);
    }

    [Fact]
    public async Task Mint_ForALiveRecording_ConsultsTheRecordingServiceSeam()
    {
        // Finding-1: the has-session gate is resolved through IRecordingService, not a raw db read from
        // the sharing slice — so the mint asks the recording context whether a session exists for the track.
        await SeedRecordingSessionAsync(new TrackId(1), Owner);

        var result = await _service.MintAsync(ShareKindNames.Live, 1, "24h", Owner, default);

        Assert.Equal(ShareMintOutcome.Ok, result.Outcome);
        Assert.Equal((new TrackId(1), Owner), _recording.LastAsked);
    }

    [Fact]
    public async Task Mint_LiveForAnOwnedActivityWithNoRecordingSession_Is404()
    {
        // A live-share is only for a real recording. A plain owned activity (no session) cannot be
        // published as "live" — even though the same id would mint an ACTIVITY link fine.
        var live = await _service.MintAsync(ShareKindNames.Live, 1, "24h", Owner, default);
        Assert.Equal(ShareMintOutcome.NotFound, live.Outcome);
        Assert.Empty(await _db.ShareLinks.ToListAsync());

        Assert.Equal(ShareMintOutcome.Ok,
            (await _service.MintAsync(ShareKindNames.Activity, 1, "24h", Owner, default)).Outcome);
    }

    [Fact]
    public async Task Mint_LiveForSomeoneElsesRecording_Is404()
    {
        // Ownership, not visibility: seeing a friend's live ride must not let you publish it.
        await SeedRecordingSessionAsync(new TrackId(90), Stranger); // track 90 is the stranger's public activity

        var result = await _service.MintAsync(ShareKindNames.Live, 90, "24h", Owner, default);

        Assert.Equal(ShareMintOutcome.NotFound, result.Outcome);
        Assert.Empty(await _db.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task LiveAndActivityLinksOnTheSameTrack_AreCountedAndListedSeparately()
    {
        // Both live on track_id, but each kind's cap and management list is scoped to its own kind — an
        // activity's links never crowd out its live-share links.
        await SeedRecordingSessionAsync(new TrackId(1), Owner);
        await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);
        await _service.MintAsync(ShareKindNames.Live, 1, "24h", Owner, default);

        Assert.Single(await ListLinksAsync(ShareKindNames.Activity, 1, Owner));
        Assert.Single(await ListLinksAsync(ShareKindNames.Live, 1, Owner));
    }

    // ---- race ----

    [Fact]
    public async Task Mint_ForAnOwnedRace_Succeeds_AndKeysOnItsOwnRaceIdColumn()
    {
        var result = await _service.MintAsync(ShareKindNames.Race, 3, "7d", Owner, default);

        Assert.Equal(ShareMintOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Token);

        var row = Assert.Single(await _db.ShareLinks.ToListAsync());
        Assert.Equal(ShareKindNames.Race, row.Kind);
        // A race is NOT 1:1 with a track (many races over one course), so it cannot borrow track_id the way
        // "live" does — it needs its own column, and the "exactly one FK set, matching Kind" invariant then
        // covers three columns rather than two.
        Assert.Equal(new RaceId(3), row.RaceId);
        Assert.Null(row.TrackId);
        Assert.Null(row.SegmentId);
        Assert.NotEqual(result.Token, row.TokenHash, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Mint_ForARaceSomeoneElseOwns_Is404_EvenWhenItIsPublic()
    {
        // Race 91 is PUBLIC and the Owner can see it — and still may not publish it. Ownership mints, never
        // visibility, and never the race's edit policy (a collaborator may edit a race, not share it).
        var notMine = await _service.MintAsync(ShareKindNames.Race, 91, "24h", Owner, default);
        var missing = await _service.MintAsync(ShareKindNames.Race, 9999, "24h", Owner, default);

        Assert.Equal(ShareMintOutcome.NotFound, notMine.Outcome);
        Assert.Equal(missing.Outcome, notMine.Outcome);
        Assert.Empty(await _db.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task RaceAndActivityLinks_DoNotShareACapOrAList_EvenWhenTheIdsCollide()
    {
        // Race 3's id space is its own; activity 1 and race 3 are unrelated resources that happen to live
        // in the same table. Each kind's LinksFor reads its OWN column, so neither can crowd the other out.
        await _service.MintAsync(ShareKindNames.Activity, 1, "7d", Owner, default);
        await _service.MintAsync(ShareKindNames.Race, 3, "7d", Owner, default);

        Assert.Single(await ListLinksAsync(ShareKindNames.Activity, 1, Owner));
        Assert.Single(await ListLinksAsync(ShareKindNames.Race, 3, Owner));
        Assert.Empty(await ListLinksAsync(ShareKindNames.Race, 1, Owner));
    }

    [Fact]
    public async Task DeletingARace_StructurallyRevokesItsShareLinks()
    {
        // The whole reason race_id is a real FK column: revocation is the CASCADE, not service code someone
        // has to remember on a delete path. Same guarantee activity and segment links already get.
        await _service.MintAsync(ShareKindNames.Race, 3, "7d", Owner, default);
        Assert.Single(await _db.ShareLinks.ToListAsync());

        _db.Races.Remove(await _db.Races.SingleAsync(r => r.RaceId == new RaceId(3)));
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.ShareLinks.ToListAsync());
    }

    // ---- the dashboard's "is this shared" badge ----

    /// <summary>The listed rows only — most list tests are about what a row says, not about the envelope's
    /// lifetime total (which has its own tests below).</summary>
    private async Task<IReadOnlyList<ShareLinkSummaryDto>> ListLinksAsync(
        string kind, int resourceId, string userId)
        => (await _service.ListAsync(kind, resourceId, userId, default)).Links;

    // ---- the /Sharing inventory: everything that is live right now ----

    [Fact]
    public async Task ActiveLinks_ListEveryKind_NamedByTheResourceTheyPublish()
    {
        // The question the page exists to answer: what can a stranger open at this moment? All four kinds
        // are one list, because the owner's worry is not sorted by kind.
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner);
        await SeedLinkAsync(ShareKindNames.Live, 1, Owner);
        await SeedLinkAsync(ShareKindNames.Segment, 2, Owner);
        await SeedLinkAsync(ShareKindNames.Race, 3, Owner);

        var links = (await _service.ActiveLinksAsync(Owner, default)).Links;

        Assert.Equal(4, links.Count);
        Assert.Equal(
            [ShareKindNames.Activity, ShareKindNames.Live, ShareKindNames.Race, ShareKindNames.Segment],
            links.Select(l => l.Kind).OrderBy(k => k, StringComparer.Ordinal), StringComparer.Ordinal);
        // The name is the point: an id alone tells the owner nothing they can act on.
        Assert.Equal("my segment", Assert.Single(links, l => l.Kind == ShareKindNames.Segment).ResourceName);
        Assert.Equal("my race", Assert.Single(links, l => l.Kind == ShareKindNames.Race).ResourceName);
        Assert.All(links.Where(l => l.ResourceId == 1), l => Assert.Equal("mine", l.ResourceName));
    }

    [Fact]
    public async Task ActiveLinks_ExcludeRevokedAndExpiredOnes()
    {
        // LIVE-only, unlike ListAsync: a revoked link is not something a stranger can open, and listing
        // dead rows here would bury the ones that are actually publishing something.
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, revokedAt: DateTime.UtcNow);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, expiresAt: DateTime.UtcNow.AddSeconds(-1));

        var links = (await _service.ActiveLinksAsync(Owner, default)).Links;

        Assert.Single(links);
    }

    [Fact]
    public async Task ActiveLinks_NeverIncludeSomeoneElsesLink()
    {
        // Owner-scoped in the WHERE clause: whether another user published something is not ours to show.
        await SeedLinkAsync(ShareKindNames.Activity, 90, Stranger);

        Assert.Empty((await _service.ActiveLinksAsync(Owner, default)).Links);
        Assert.Single((await _service.ActiveLinksAsync(Stranger, default)).Links);
    }

    [Fact]
    public async Task ActiveLinks_CarryTheirOpenCountAndExpiry()
    {
        // The leak signal (an unexpected count) and the decision input (how long it still works) are the
        // two numbers a row exists to show.
        var expires = DateTime.UtcNow.AddDays(3);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, expiresAt: expires, opens: 7);

        var link = Assert.Single((await _service.ActiveLinksAsync(Owner, default)).Links);

        Assert.Equal(7, link.OpenCount);
        Assert.Equal(expires, link.ExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ActiveLinks_FallBackToTheIdWhenTheResourceHasNoName()
    {
        // A nameless row still lists. Dropping it would be the one failure this page cannot afford:
        // silently omitting something that IS published.
        _db.Activities.Add(new Activity { TrackId = new TrackId(7), UserId = Owner, TrackName = null });
        await _db.SaveChangesAsync();
        await SeedLinkAsync(ShareKindNames.Activity, 7, Owner);

        var link = Assert.Single((await _service.ActiveLinksAsync(Owner, default)).Links);

        Assert.Equal("#7", link.ResourceName);
    }

    [Fact]
    public async Task ActiveLinks_AreNewestFirst()
    {
        await SeedLinkAsync(ShareKindNames.Segment, 2, Owner);
        await Task.Delay(10);
        await SeedLinkAsync(ShareKindNames.Race, 3, Owner);

        var links = (await _service.ActiveLinksAsync(Owner, default)).Links;

        Assert.Equal(ShareKindNames.Race, links[0].Kind);
    }

    [Fact]
    public async Task ActiveLinks_WithNothingShared_IsEmpty()
        => Assert.Empty((await _service.ActiveLinksAsync(Owner, default)).Links);

    // ---- timestamps survive the SQLite round-trip as UTC ----

    [Fact]
    public async Task ActiveLinks_StampEveryTimestampUtc()
    {
        // CC2-04→AR-02: SQLite hands DateTimes back as Kind-Unspecified. These DTOs are DateTimeOffset
        // now, so the wire always carries an offset — what can still go wrong is the CONVERSION reading
        // Unspecified as server-local (the implicit-cast trap ToUtcOffset exists for). Shipped that way,
        // a link minted at 12:17 CEST would display 10:17 and its 24h countdown read "21 hours". A zero
        // offset after a real read-back is the thing to assert.
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 1);
        var row = await _db.ShareLinks.SingleAsync();
        row.LastOpenedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();   // force a real read-back, not the tracked in-memory entity

        var link = Assert.Single((await _service.ActiveLinksAsync(Owner, default)).Links);

        Assert.Equal(TimeSpan.Zero, link.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, link.ExpiresAt.Offset);
        Assert.Equal(TimeSpan.Zero, link.LastOpenedAt!.Value.Offset);
    }

    [Fact]
    public async Task List_StampsEveryTimestampUtc()
    {
        // Same defect, same fix, on the per-item dialog — it shipped with it before the inventory existed.
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, revokedAt: DateTime.UtcNow, opens: 1);
        var row = await _db.ShareLinks.SingleAsync();
        row.LastOpenedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var link = Assert.Single(await ListLinksAsync(ShareKindNames.Activity, 1, Owner));

        Assert.Equal(TimeSpan.Zero, link.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, link.ExpiresAt.Offset);
        Assert.Equal(TimeSpan.Zero, link.RevokedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, link.LastOpenedAt!.Value.Offset);
    }

    /// <summary>
    /// Seeds a link row directly. The batched query is what is under test, so the rows are built rather
    /// than minted: mint cannot produce an expired row, and its live-kind gate would drag a
    /// RecordingSession into a test that is only about the WHERE clause.
    /// </summary>
    private async Task SeedLinkAsync(
        string kind, int resourceId, string userId,
        DateTime? expiresAt = null, DateTime? revokedAt = null, int opens = 0)
    {
        var now = DateTime.UtcNow;
        _db.ShareLinks.Add(new ShareLink
        {
            TokenHash = ShareTokens.Hash(ShareTokens.New()),
            Kind = kind,
            TrackId = kind is ShareKindNames.Activity or ShareKindNames.Live ? new TrackId(resourceId) : (TrackId?)null,
            SegmentId = kind == ShareKindNames.Segment ? new SegmentId(resourceId) : (SegmentId?)null,
            RaceId = kind == ShareKindNames.Race ? new RaceId(resourceId) : (RaceId?)null,
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = expiresAt ?? now.AddDays(7),
            RevokedAt = revokedAt,
            OpenCount = opens,
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task ShareOpens_ReturnsTracksWithAnActiveActivityLink()
    {
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Equal([1], shared.Keys);
    }

    [Fact]
    public async Task ShareOpens_CountsALiveLinkToo()
    {
        // A live share publishes the same ride's map — the card must badge it exactly like an activity
        // share. Both kinds key on track_id, which is why one query covers them.
        await SeedLinkAsync(ShareKindNames.Live, 1, Owner);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Equal([1], shared.Keys);
    }

    [Fact]
    public async Task ShareOpens_KeepsRevokedAndExpiredLinks_ButMarksThemInactive()
    {
        // The contract this replaces: dead links used to leave the map entirely, count and all. They stay
        // now, because their opens are the ride's view history — but Active must be false, or the card
        // would tell the owner a ride is still public when it is not (the worse of the two errors).
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 3, revokedAt: DateTime.UtcNow);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 4,
            expiresAt: DateTime.UtcNow.AddSeconds(-1));

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Equal(new ShareOpens(7, Active: false), shared[1]);
    }

    [Fact]
    public async Task ShareOpens_IsActiveWhileAnyLinkIsStillLive()
    {
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 3, revokedAt: DateTime.UtcNow);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 4);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.True(shared[1].Active);
    }

    [Fact]
    public async Task ShareOpens_NeverReportsSomeoneElsesLink()
    {
        // THE property: the badge says "I published this", never "they published this". Activity 90 is a
        // stranger's PUBLIC ride, so it is visible to the Owner — visibility must not leak their sharing.
        await SeedLinkAsync(ShareKindNames.Activity, 90, Stranger);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Empty(shared);
    }

    [Fact]
    public async Task ShareOpens_IgnoresRaceLinks()
    {
        // A race keys on race_id and several races can share one course Activity, so a race link says
        // nothing about whether that track is published. Race 3's course is track 1.
        await SeedLinkAsync(ShareKindNames.Race, 3, Owner);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Empty(shared);
    }

    [Fact]
    public async Task ShareOpens_IgnoresAMalformedRowWhoseKindAndFkDisagree()
    {
        // What the Kind clause actually earns: a race/segment row cannot reach the badge via track_id
        // anyway (its FK is elsewhere), but a row whose kind and FK disagree can. ShareLinkAccess.ToGrant
        // treats such a row as granting NOTHING, so the badge has to agree — otherwise the dashboard
        // claims a ride is published by a link that resolves to no access at all.
        _db.ShareLinks.Add(new ShareLink
        {
            TokenHash = ShareTokens.Hash(ShareTokens.New()),
            Kind = ShareKindNames.Segment,
            TrackId = new TrackId(1),
            UserId = Owner,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        });
        await _db.SaveChangesAsync();

        Assert.Empty(await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default));
    }

    [Fact]
    public async Task ShareOpens_ReturnsTheCallersWHOLEActiveSet_NotAPageOfIt()
    {
        // Replaces two tests that pinned an id-list parameter this method no longer takes. That parameter
        // was removed deliberately: EF binds one SQL parameter per element, so passing an unpaginated
        // feed's ids scaled the query text with the activity count and gave the statement cache a
        // different string for every count. Callers now membership-test against the whole set instead,
        // which is what this pins.
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner);
        await SeedLinkAsync(ShareKindNames.Live, 90, Owner);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Equal([1, 90], shared.Keys.OrderBy(i => i));
    }

    [Fact]
    public async Task ShareOpens_WithNoLinksAtAll_IsEmpty()
    {
        Assert.Empty(await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default));
    }

    [Fact]
    public async Task ShareOpens_ForSegments_KeysOnSegmentId()
    {
        // Segment 2 and activity 1 are different rows in different id spaces. Seeding a link for each
        // proves the segment surface reads segment_id and does not spill across on a colliding id.
        await SeedLinkAsync(ShareKindNames.Segment, 2, Owner);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Segment, Owner, default);

        Assert.Equal([2], shared.Keys);
    }

    [Fact]
    public async Task ShareOpens_ForRaces_KeysOnRaceId()
    {
        // Race 3's course is track 1, and track 1 also has its own activity link. A race is not 1:1 with
        // a track, so neither link may be read as the other.
        await SeedLinkAsync(ShareKindNames.Race, 3, Owner);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Race, Owner, default);

        Assert.Equal([3], shared.Keys);
    }

    [Fact]
    public async Task ShareOpens_ForSegmentsAndRaces_StillHideSomeoneElsesLinks()
    {
        // The owner-scoping property is per-surface, not just on the activity branch someone tested first.
        await SeedLinkAsync(ShareKindNames.Segment, 2, Stranger);
        await SeedLinkAsync(ShareKindNames.Race, 91, Stranger);

        Assert.Empty(await _service.ShareOpensAsync(ShareKindNames.Segment, Owner, default));
        Assert.Empty(await _service.ShareOpensAsync(ShareKindNames.Race, Owner, default));
    }

    [Fact]
    public async Task ShareOpens_CarriesTheOpenCount_NotJustMembership()
    {
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 12);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Equal(12, shared[1].Opens);
    }

    [Fact]
    public async Task ShareOpens_SumsEveryLiveLinkForOneResource()
    {
        // An owner may hold several live links for the same ride (different recipients, different TTLs).
        // The card shows ONE badge, so it has to show one number — the total, not whichever link the query
        // happened to see last.
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 3);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 4);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Equal(7, shared[1].Opens);
    }

    [Fact]
    public async Task ShareOpens_AddsALiveShareSOpensToTheActivitySOwn()
    {
        // The badge already treats activity and live as one truth about one ride (they publish the same
        // map). Their counts therefore have to add up, or the number would contradict the badge above it.
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 2);
        await SeedLinkAsync(ShareKindNames.Live, 1, Owner, opens: 5);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Equal(7, shared[1].Opens);
    }

    [Fact]
    public async Task ShareOpens_KeepsARevokedLinksOpensAfterTheLinkIsGone()
    {
        // THE reason this method changed: revoking a link must not reset a ride's view count. The old
        // contract summed active links only, so revoking silently erased nine views.
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 9, revokedAt: DateTime.UtcNow);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 1);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Equal(10, shared[1].Opens);
    }

    [Fact]
    public async Task ShareOpens_KeepsAFinishedLiveSharesOpensOnTheRide()
    {
        // A phone-minted live link dies at its 24h TTL while the ride lives on. Its views belong to the
        // ride: same track_id bucket, so they simply stay — no transfer step, and no reset at expiry.
        await SeedLinkAsync(ShareKindNames.Live, 1, Owner, opens: 5,
            expiresAt: DateTime.UtcNow.AddSeconds(-1));

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Equal(new ShareOpens(5, Active: false), shared[1]);
    }

    [Fact]
    public async Task ShareOpens_NeverCountsSomeoneElsesOpens()
    {
        // The owner-scoping property, restated on the COUNT: a stranger's link to their own public ride
        // must not contribute a number to anyone else's dashboard.
        await SeedLinkAsync(ShareKindNames.Activity, 90, Stranger, opens: 50);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 2);

        var shared = await _service.ShareOpensAsync(ShareKindNames.Activity, Owner, default);

        Assert.Equal([1], shared.Keys);
        Assert.Equal(2, shared[1].Opens);
    }

    [Fact]
    public async Task List_ReportsTheOpenCountAndLastOpen()
    {
        var opened = new DateTime(2026, 7, 24, 9, 30, 0, DateTimeKind.Utc);
        _db.ShareLinks.Add(new ShareLink
        {
            TokenHash = ShareTokens.Hash(ShareTokens.New()),
            Kind = ShareKindNames.Activity,
            TrackId = new TrackId(1),
            UserId = Owner,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            OpenCount = 4,
            LastOpenedAt = opened,
        });
        await _db.SaveChangesAsync();

        var links = await ListLinksAsync(ShareKindNames.Activity, 1, Owner);

        var row = Assert.Single(links);
        Assert.Equal(4, row.OpenCount);
        Assert.Equal(opened, row.LastOpenedAt);
    }

    [Fact]
    public async Task List_TotalOpens_CountsDeadLinksToo()
    {
        // The dialog's total is the card badge's number. Summing only the live rows would make the two
        // disagree the moment a link is revoked, which is exactly when an owner opens this dialog.
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 9, revokedAt: DateTime.UtcNow);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 1);

        var response = await _service.ListAsync(ShareKindNames.Activity, 1, Owner, default);

        Assert.Equal(10, response.TotalOpens);
    }

    [Fact]
    public async Task List_TotalOpens_MergesActivityAndLiveOpens_LikeTheBadge()
    {
        // The badge merges activity + live opens on the same track (ShareOpensAsync — a finished live
        // share's views carry onto the ride). TotalOpens IS the badge's number (the DTO contract), so
        // both dialogs must report the merged sum, while their listed rows stay kind-scoped.
        await SeedLinkAsync(ShareKindNames.Live, 1, Owner, opens: 5, expiresAt: DateTime.UtcNow.AddHours(-1));
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 2);

        var activityDialog = await _service.ListAsync(ShareKindNames.Activity, 1, Owner, default);
        var liveDialog = await _service.ListAsync(ShareKindNames.Live, 1, Owner, default);

        Assert.Equal(7, activityDialog.TotalOpens);
        Assert.Equal(7, liveDialog.TotalOpens);
        Assert.Single(activityDialog.Links, l => l.Kind == ShareKindNames.Activity);
        Assert.Single(liveDialog.Links, l => l.Kind == ShareKindNames.Live);
    }

    [Fact]
    public async Task List_CapsEndedLinks_ButNotActiveOnes_AndStillTotalsThemAll()
    {
        // Opened links are never swept now, so a long-shared ride accumulates dead rows without bound. The
        // list shows the recent ones; the total still covers every one of them.
        for (var i = 0; i < 8; i++)
            await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 1, revokedAt: DateTime.UtcNow);
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner, opens: 2);

        var response = await _service.ListAsync(ShareKindNames.Activity, 1, Owner, default);

        Assert.Equal(6, response.Links.Count);              // 5 ended + the live one
        Assert.Single(response.Links, l => l.IsActive);     // an active link is never hidden
        Assert.Equal(10, response.TotalOpens);              // 8 x 1 + 2, including the capped rows
    }

    [Fact]
    public async Task ShareOpens_ForAnUnknownKind_IsEmpty()
    {
        // Same closed-vocabulary stance as mint: an unrecognised kind resolves to nothing rather than
        // falling through to the track_id branch and badging the wrong surface.
        await SeedLinkAsync(ShareKindNames.Activity, 1, Owner);

        Assert.Empty(await _service.ShareOpensAsync("heatmap", Owner, default));
    }

    /// <summary>
    /// Minimal <see cref="IRecordingService"/> for the mint's has-session gate (Finding-1): answers
    /// <see cref="HasSessionForTrackAsync"/> straight from the test DB — exactly what the real service does —
    /// and records the last (trackId, userId) asked so a test can prove the seam was consulted. Every other
    /// member is unreachable from the mint path.
    /// </summary>
    private sealed class DbBackedRecording(RouteBaseDbContext db) : IRecordingService
    {
        public (TrackId TrackId, string UserId)? LastAsked { get; private set; }

        public Task<bool> HasSessionForTrackAsync(TrackId trackId, string userId, CancellationToken ct = default)
        {
            LastAsked = (trackId, userId);
            return db.RecordingSessions.AnyAsync(s => s.TrackId == trackId && s.UserId == userId, ct);
        }

        public Task<RecordingSession?> StartSessionAsync(StartRecordingRequest r, string u) => throw new NotSupportedException();
        public Task<List<ExternalGpsPoint>?> AppendPointsAsync(string s, RecordingPointsBatch b, string u) => throw new NotSupportedException();
        public Task<RecordingSession?> StopSessionAsync(string s, string u) => throw new NotSupportedException();
        public Task<RecordingSession?> PauseSessionAsync(string s, string u) => throw new NotSupportedException();
        public Task<RecordingSession?> ResumeSessionAsync(string s, string u) => throw new NotSupportedException();
        public Task<RecordingSession?> RetypeSessionAsync(string s, string a, string u) => throw new NotSupportedException();
        public Task<RecordingSession?> AbandonSessionAsync(string s, string u) => throw new NotSupportedException();
        public Task<RecordingSession?> GetActiveSessionAsync(string u) => throw new NotSupportedException();
        public Task<List<RecordingSession>> GetActiveSessionsVisibleToAsync(List<string> ids) => throw new NotSupportedException();
        public Task<RecordingSession?> GetSessionAsync(string s) => throw new NotSupportedException();
        public Task<RecordingSession?> GetLiveShareSessionAsync(TrackId t, string o, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<ExternalGpsPoint>> GetLivePointsAsync(RecordingSession s, long a) => throw new NotSupportedException();
        public Task<List<RecentSessionView>> GetRecentSessionsWithReviewAsync(string u, int limit = 20) => throw new NotSupportedException();
        public Task<List<ExternalGpsPoint>> GetPointsAfterSeqAsync(string s, long a) => throw new NotSupportedException();
        public Task<bool> RetryFinalizeFailedAsync(string s, string u) => throw new NotSupportedException();
        public Task<bool> MarkFinalizeFailedAsync(string s, string u) => throw new NotSupportedException();
        public Task<bool> DeleteAbandonedSessionAsync(string s, string u) => throw new NotSupportedException();
    }
}
