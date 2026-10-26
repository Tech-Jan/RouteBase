using System.Collections;
using RouteBaseWeb.Api;
using RouteBaseWeb.Hubs;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Recording;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Sharing;
using RouteBaseWeb.Services.Tracks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Tests.Helpers;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the AQ-S1 contract addition on <see cref="RecordingApiController"/>: the server's
/// <c>lastSequence</c> is echoed in the active-sessions payload and the append response, so a phone
/// resyncs its clientSequence after a reconnect/409-adopt instead of restarting from 0 (data-loss fix).
/// Real SQLite + blob-backed reader so the append pipeline runs exactly as in production.
/// </summary>
public class RecordingApiControllerTests : IAsyncLifetime
{
    private const string UserId = "test-user";

    private string _connStr = null!;
    private RouteBaseDbContext _context = null!;
    private ServiceProvider _provider = null!;
    private RecordingService _service = null!;
    // Same instance the service stages through — the LV-01/LV-02 tests read timestamp precision from the
    // staging layer that owns it (RecordingStagingStore.ToEpoch), not from the wire DTO (perf-11).
    private RecordingStagingStore _stagingStore = null!;
    // The real registry, shared with the controller — the append response's `watched` flag reads it.
    private readonly LiveViewerRegistry _viewers = new(() => DateTime.UtcNow);

    public async Task InitializeAsync()
    {
        var dbName = $"recording_api_{Guid.NewGuid():N}";
        _connStr = $"Data Source=file:{dbName}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_connStr).Options;

        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        var schema = new DbSchemaManager(_context);
        await schema.EnsureActivityBboxTableAsync();
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureRecordingStagingTableAsync();

        _context.Users.Add(new AppUser
        {
            UserId = UserId, DisplayName = "Test User", Email = "test@test.com", IsAdmin = false,
        });
        await _context.SaveChangesAsync();

        _provider = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddDbContext<RouteBaseDbContext>(o => o.UseSqlite(_connStr))
            .AddScoped<ITrackObjectStore, TrackObjectStore>()
            .AddScoped<TrackContentStore>()
            .AddScoped<RecordingStagingStore>()
            .BuildServiceProvider();
        var reader = new ContentTrackReader(
            new TrackReaderCache(64L * 1024 * 1024), new SingleFlight(),
            _provider.GetRequiredService<IServiceScopeFactory>());
        var content = new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System);
        _stagingStore = new RecordingStagingStore(_context);
        var importService = new ActivityImportService(_context, content, NullLogger<ActivityImportService>.Instance);
        var commandVis   = new TestVisibilityService(UserId);
        var commandInval = new UserCacheInvalidator();
        var commandService = new ActivityCommandService(
            _context, commandVis, commandInval,
            NullLogger<ActivityCommandService>.Instance,
            TestLifecycle.For(_context, commandVis, commandInval));

        // The controller and the append pipeline share this one service instance (⇒ one scoped context),
        // so the controller's post-append GetSessionAsync reads the entity the append just tracked.
        _service = new RecordingService(
            _context, importService, _stagingStore, reader, commandService,
            new RecordingFinalizationCoordinator(new SingleFlight(), _provider.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<RecordingService>.Instance, TimeProvider.System);
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        return _context.DisposeAsync().AsTask();
    }

    private RecordingApiController Controller() => new(
        _service,
        new ShareLinkService(_context, new TestVisibilityService(UserId), _service,
            new RouteBaseWeb.Services.Races.RaceAccess(_context, new TestVisibilityService(UserId)), TimeProvider.System),
        new RecordingFinalizationCoordinator(new SingleFlight(), _provider.GetRequiredService<IServiceScopeFactory>()),
        new TestCurrentUserService(UserId),
        new TestVisibilityService(UserId),
        new RecordingRateLimiter(() => DateTime.UtcNow),
        new NoOpHubContext(),
        new TestLiveSessionDirectory(),
        _viewers,
        NullLogger<RecordingApiController>.Instance,
        TimeProvider.System);

    private static StartRecordingRequest StartRequest() => new()
    {
        ActivityType = "Run", TrackName = "Test Run", Visibility = "friends", ClientVersion = "test-1.0",
    };

    private static RecordingPointsBatch Batch(int count, int seq)
    {
        var baseUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var points = Enumerable.Range(0, count).Select(i => new RecordingGpsPoint
        {
            Lat = 47.0 + i * 0.00004, Lng = 11.0, Altitude = 1000.0 + i, Speed = 5.0,
            TimestampUtc = baseUtc.AddSeconds(seq * 100 + i),
        }).ToList();
        return new RecordingPointsBatch { Points = points, ClientSequence = seq };
    }

    private static object Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o)!;

    /// <summary>The append response body, as the named DTO it must be — never an anonymous object (LV-01).</summary>
    private static AppendPointsResponse Body(IActionResult result) =>
        Assert.IsType<AppendPointsResponse>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task GetActiveSessions_IncludesLastSequenceOfLastAcceptedBatch()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        Assert.NotNull(session);
        // Accept a batch through the service (no hub), advancing LastSequence to 5.
        await _service.AppendPointsAsync(session!.SessionId, Batch(3, seq: 5), UserId);

        var result = await Controller().GetActiveSessions();

        var ok = Assert.IsType<OkObjectResult>(result);
        var first = Assert.IsAssignableFrom<IEnumerable>(ok.Value).Cast<object>().Single();
        Assert.Equal(5, Prop(first, "LastSequence"));
    }

    [Fact]
    public async Task AppendPoints_DuplicateSeq_ReturnsInsertedZeroAndLastSequence()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        Assert.NotNull(session);
        // First batch accepted via the service (advances LastSequence to 1, fires no hub).
        await _service.AppendPointsAsync(session!.SessionId, Batch(3, seq: 1), UserId);

        // Replay the same sequence through the controller: dedup path → 0 inserted, no SignalR broadcast.
        var result = await Controller().AppendPoints(session.SessionId, Batch(3, seq: 1));

        var body = Body(result);
        Assert.Equal(0, body.Inserted);
        Assert.Equal(1, body.LastSequence); // >= the seq the client sent → phone can resync
    }

    [Fact]
    public async Task AppendPoints_AcceptedBatch_EchoesAdvancedLastSequence()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        Assert.NotNull(session);

        var result = await Controller().AppendPoints(session!.SessionId, Batch(4, seq: 7));

        var body = Body(result);
        Assert.Equal(4, body.Inserted);
        Assert.Equal(7, body.LastSequence);
    }

    // ── The upload-cadence battery gate: the append response tells the phone whether anyone is watching ──

    [Fact]
    public async Task AppendPoints_ResponseIsANamedDto_NotAnAnonymousObject()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);

        var ok = Assert.IsType<OkObjectResult>(await Controller().AppendPoints(session!.SessionId, Batch(1, seq: 1)));

        Assert.IsType<AppendPointsResponse>(ok.Value); // convention: every API response body is a named DTO
    }

    [Fact]
    public async Task AppendPoints_NoLiveViewer_ReturnsWatchedFalse()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);

        var body = Body(await Controller().AppendPoints(session!.SessionId, Batch(2, seq: 1)));

        Assert.False(body.Watched); // → the phone drops to the idle upload cadence (battery)
    }

    [Fact]
    public async Task AppendPoints_LiveViewerJoined_ReturnsWatchedTrue()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        _viewers.Join(session!.SessionId, "conn-a");   // what RecordingHub.JoinSession does after its visibility check

        var body = Body(await Controller().AppendPoints(session.SessionId, Batch(2, seq: 1)));

        Assert.True(body.Watched);
    }

    [Fact]
    public async Task AppendPoints_ViewerLeft_ReturnsWatchedFalseAgain()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        _viewers.Join(session!.SessionId, "conn-a");
        _viewers.LeaveAll("conn-a");                   // the hub's OnDisconnectedAsync (closed tab)

        var body = Body(await Controller().AppendPoints(session.SessionId, Batch(2, seq: 1)));

        Assert.False(body.Watched);
    }

    [Fact]
    public async Task AppendPoints_ViewerOnAnotherSession_DoesNotMarkThisOneWatched()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        _viewers.Join("someone-elses-session", "conn-a");

        var body = Body(await Controller().AppendPoints(session!.SessionId, Batch(2, seq: 1)));

        Assert.False(body.Watched);
    }

    // ── LV-01: the catch-up cursor is the dense staging seq, never the timestamp ─────────────────

    private static List<LivePointDto> Points(IActionResult result) =>
        Assert.IsType<List<LivePointDto>>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task GetPoints_AfterSeq_ReturnsOnlyLaterPoints()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        await _service.AppendPointsAsync(session!.SessionId, Batch(5, seq: 1), UserId);

        var points = Points(await Controller().GetPoints(session.SessionId, after: 2));

        Assert.Equal([3L, 4L], points.Select(p => p.Seq));
    }

    [Fact]
    public async Task GetPoints_NoCursor_ReturnsTheWholeHistoryFromSeqZero()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        await _service.AppendPointsAsync(session!.SessionId, Batch(3, seq: 1), UserId);

        var points = Points(await Controller().GetPoints(session.SessionId, after: null));

        Assert.Equal([0L, 1L, 2L], points.Select(p => p.Seq)); // after ?? -1, and seq starts at 0
    }

    [Fact]
    public async Task AppendPoints_SubSecondPoints_AllPersistAndCarryDistinctSeq()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        var baseUtc = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var batch = new RecordingPointsBatch
        {
            ClientSequence = 1,
            // Three fixes inside ONE wall-clock second: staging truncates ts_utc to whole seconds, so all
            // three land with an identical TsEpoch. Only `seq` still separates them.
            Points = [.. Enumerable.Range(0, 3).Select(i => new RecordingGpsPoint
            {
                Lat = 47.0 + i * 0.00001, Lng = 11.0, TimestampUtc = baseUtc.AddMilliseconds(i * 300),
            })],
        };
        await Controller().AppendPoints(session!.SessionId, batch);

        // Read at the staging layer that OWNS the truncation (RecordingStagingStore), not the wire DTO —
        // `ts` no longer rides the LivePointDto after perf-11, but the LV-01 contract still holds here.
        var staged = await _stagingStore.GetPointsAsync(session.TrackId);

        Assert.Equal(3, staged.Count);
        Assert.Equal([0L, 1L, 2L], staged.Select(p => p.Seq));       // dense ordinals, distinct
        Assert.Single(staged.Select(p => p.TsEpoch).Distinct());     // the ts collapsed to one second — seq saved them
    }

    // ── LV-02: the inbound DateTime Kind boundary ────────────────────────────────────────────────
    //
    // The two consumers of the inbound value are Kind-sensitive: RecordingRequestValidator compares raw
    // ticks against DateTime.UtcNow, and RecordingStagingStore.ToEpoch calls ToUniversalTime(). The bug
    // only *manifests* on a non-UTC server (where a Kind=Local value is 1-2 h off UtcNow → 400), so the
    // pins below assert the Kind normalization itself — which is timezone-independent and fails today.

    [Fact]
    public void RecordingGpsPoint_KindlessTimestamp_IsAssumedUtcNotServerLocal()
    {
        var point = new RecordingGpsPoint
        {
            TimestampUtc = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Unspecified),
        };

        Assert.Equal(DateTimeKind.Utc, point.TimestampUtc.Kind);
        Assert.Equal(new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc), point.TimestampUtc);
    }

    [Fact]
    public void RecordingGpsPoint_OffsetSuffixedTimestamp_IsConvertedToTheSameInstantInUtc()
    {
        // What System.Text.Json hands us for "2025-01-01T12:00:00+02:00": Kind=Local, shifted to
        // server-local. The same instant, expressed differently — it must not be re-interpreted.
        var wire = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)).LocalDateTime;

        var point = new RecordingGpsPoint { TimestampUtc = wire };

        Assert.Equal(DateTimeKind.Utc, point.TimestampUtc.Kind);
        Assert.Equal(new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc), point.TimestampUtc);
    }

    [Fact]
    public async Task AppendPoints_OffsetSuffixedTimestamp_IsAcceptedAndStoredAsTheSameInstant()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        var wire = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)).LocalDateTime;

        var result = await Controller().AppendPoints(session!.SessionId, new RecordingPointsBatch
        {
            ClientSequence = 1,
            Points = [new RecordingGpsPoint { Lat = 47.0, Lng = 11.0, TimestampUtc = wire }],
        });

        Assert.IsType<OkObjectResult>(result); // not a 400 from the plausibility window
        // Stored as the same instant in UTC (12:00+02:00 == 10:00Z), truncated to whole epoch seconds by
        // staging — asserted at the layer that owns the timestamp, not the wire DTO (perf-11).
        var staged = Assert.Single(await _stagingStore.GetPointsAsync(session.TrackId));
        var expectedEpoch = (long)(new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;
        Assert.Equal(expectedEpoch, staged.TsEpoch);
    }

    // ── LV-03 / PR-05: no deferred LINQ iterator in a response body ─────

    // A session that exists but is not visible to the caller answers the SAME 404 as one that does not
    // exist — the api/ surface's no-oracle convention (DeleteSession, ShareLinks, TrackEdit all state
    // it). A 403 here confirmed a private session id exists and that its owner is recording right now.
    [Fact]
    public async Task GetSession_And_GetPoints_ForAForeignPrivateSession_Are404_Never403()
    {
        _context.Users.Add(new AppUser
        {
            UserId = "other-user", DisplayName = "Other", Email = "other@t.com", IsAdmin = false,
        });
        _context.Activities.Add(new Activity   // the session's Activity shell (RS-02: FK target)
        {
            TrackId = new TrackId(990), UserId = "other-user", ActivityType = "Run", Visibility = "private",
        });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = "foreign-private", TrackId = new TrackId(990), UserId = "other-user",
            Status = "recording", ActivityType = "Run", Visibility = "private",
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
        });
        await _context.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(await Controller().GetSession("foreign-private"));
        Assert.IsType<NotFoundResult>(await Controller().GetPoints("foreign-private", after: null));
    }

    [Fact]
    public async Task GetPoints_ReturnsAMaterializedList()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        await _service.AppendPointsAsync(session!.SessionId, Batch(2, seq: 1), UserId);

        var ok = Assert.IsType<OkObjectResult>(await Controller().GetPoints(session.SessionId, after: null));

        Assert.IsAssignableFrom<ICollection<LivePointDto>>(ok.Value);
    }

    [Fact]
    public async Task GetLiveSessions_ReturnsAMaterializedList()
    {
        var ok = Assert.IsType<OkObjectResult>(await Controller().GetLiveSessions());

        Assert.IsAssignableFrom<ICollection<LiveSessionDto>>(ok.Value);
    }

    // ── perf-1: a mid-ride live-share catch-up (afterSeq < 0) is strided; incremental polls are not ──

    [Fact]
    public async Task GetLivePoints_MidRideCatchUp_IsStridedToTheOverviewBudget_KeepingTheTail()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        Assert.NotNull(session);
        // Stage 10_001 points — > 2× the 5_000 catch-up budget, so the stride actually reduces (a real
        // long ride reaches this: the production max is a 25k-point session).
        await _service.AppendPointsAsync(session!.SessionId, Batch(4000, seq: 1), UserId);
        await _service.AppendPointsAsync(session.SessionId, Batch(4000, seq: 2), UserId);
        await _service.AppendPointsAsync(session.SessionId, Batch(2001, seq: 3), UserId);

        // A late opener pulls the whole staged history (afterSeq = -1) → strided down, true tail retained.
        var catchUp = await _service.GetLivePointsAsync(session, afterSeq: -1);
        Assert.InRange(catchUp.Count, 5000, 5100);          // ~5_000, not the full 10_001 (the perf-1 win)
        Assert.Equal(0L, catchUp[0].Seq!.Value);            // starts at the first staged point
        Assert.Equal(10_000L, catchUp[^1].Seq!.Value);      // the head is kept → the client's cursor won't lag

        // An incremental poll (afterSeq >= 0) returns its delta unstrided — the live tail stays full-res.
        var incremental = await _service.GetLivePointsAsync(session, afterSeq: 0);
        Assert.Equal(10_000, incremental.Count);            // seqs 1..10_000, every one
    }

    private RecordingApiController Controller(string userId, RecordingRateLimiter limiter) => new(
        _service,
        new ShareLinkService(_context, new TestVisibilityService(userId), _service,
            new RouteBaseWeb.Services.Races.RaceAccess(_context, new TestVisibilityService(userId)), TimeProvider.System),
        new RecordingFinalizationCoordinator(new SingleFlight(), _provider.GetRequiredService<IServiceScopeFactory>()),
        new TestCurrentUserService(userId),
        new TestVisibilityService(userId),
        limiter,
        new NoOpHubContext(),
        new TestLiveSessionDirectory(),
        _viewers,
        NullLogger<RecordingApiController>.Instance,
        TimeProvider.System);

    /// <summary>RS-04 addendum: a non-owner probing a sessionId (viewer-visible via sessions/live) must
    /// not consume the owner's per-session rate window — otherwise anyone who can watch the live session
    /// can 429-starve the recording phone for as long as they keep posting.</summary>
    [Fact]
    public async Task AppendPoints_NonOwner_DoesNotConsumeOwnersRateWindow()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        Assert.NotNull(session);

        var limiter = new RecordingRateLimiter(() => DateTime.UtcNow);
        var intruder = await Controller("intruder", limiter).AppendPoints(session!.SessionId, Batch(1, seq: 1));
        Assert.IsType<NotFoundObjectResult>(intruder);

        // Same 1 s window: the owner's real batch must still be accepted, not 429'd.
        var owner = await Controller(UserId, limiter).AppendPoints(session.SessionId, Batch(3, seq: 1));
        Assert.Equal(3, Body(owner).Inserted);
    }

    // ── AND-24: phone mints a public /s/{token} live-share for its own active session ──────────────────
    // These pin the deliberately-narrow seam. The MintPolicy invariant itself (an ApiKey cannot mint via
    // /api/sharelinks) is pinned separately by ShareLinkDefaultDenyTests and is NOT touched by this feature.

    [Fact]
    public async Task ShareLive_ForOwnActiveSession_MintsALiveLinkKeyedOnTheTrackId()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        Assert.NotNull(session);

        var result = await Controller().ShareLive(session!.SessionId, CancellationToken.None);

        var body = Assert.IsType<ShareLinkMintResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.StartsWith("/s/", body.Url, StringComparison.Ordinal);                 // relative, never an absolute (spoofable-host) URL

        // The ONLY thing this endpoint may persist is a "live" link on the ride's track — never activity/segment.
        var link = Assert.Single(await _context.ShareLinks.ToListAsync());
        Assert.Equal(ShareKindNames.Live, link.Kind);
        Assert.Equal(session.TrackId, link.TrackId);
        Assert.Null(link.SegmentId);
        Assert.Null(link.RevokedAt);
    }

    [Fact]
    public async Task ShareLive_ForUnknownSession_Is404_AndMintsNothing()
    {
        var result = await Controller().ShareLive("no-such-session", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Empty(await _context.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task ShareLive_ForSomeoneElsesSession_Is404_AndMintsNothing()
    {
        // Owned by UserId; the caller is a different principal (as an intruder's leaked key would be).
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        Assert.NotNull(session);

        var result = await Controller("intruder", new RecordingRateLimiter(() => DateTime.UtcNow))
            .ShareLive(session!.SessionId, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);   // 404, never 403 — no existence oracle
        Assert.Empty(await _context.ShareLinks.ToListAsync());
    }

    [Fact]
    public async Task ShareLive_ForAnInactiveSession_Is409_AndMintsNothing()
    {
        var session = await _service.StartSessionAsync(StartRequest(), UserId);
        Assert.NotNull(session);

        session!.Status = RecordingSessionStatus.Finalized;   // ride already over — not mintable
        await _context.SaveChangesAsync();

        var result = await Controller().ShareLive(session.SessionId, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Empty(await _context.ShareLinks.ToListAsync());
    }
}

/// <summary>No-op SignalR hub context — the AQ-S1 tests exercise only paths that do not broadcast
/// (dedup append + active-sessions read); it exists solely to satisfy the controller constructor.</summary>
file sealed class NoOpHubContext : IHubContext<RecordingHub>
{
    public IHubClients Clients { get; } = new NoOpClients();
    public IGroupManager Groups { get; } = new NoOpGroups();
}

file sealed class NoOpClients : IHubClients
{
    private static readonly IClientProxy Proxy = new NoOpProxy();
    public IClientProxy All => Proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
    public IClientProxy Client(string connectionId) => Proxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
    public IClientProxy Group(string groupName) => Proxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
    public IClientProxy User(string userId) => Proxy;
    public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
}

file sealed class NoOpProxy : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

file sealed class NoOpGroups : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
