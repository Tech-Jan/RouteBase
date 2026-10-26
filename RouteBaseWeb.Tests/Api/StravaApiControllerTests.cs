using RouteBaseWeb.Api;
using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Models.Strava;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Pins the <see cref="StravaApiController"/> contract: import-all's 401 (Strava-unauthenticated) /
/// 202-with-enqueued-payload / 409-slot-held / 409-queue-full-releases-slot state machine over a REAL
/// <see cref="OperationQueue"/>, status's authenticated/athlete short-circuit, and auth-url's
/// credential validation, redirect-URI derivation (request host vs. localhost ASPNETCORE_URLS/urls
/// fallbacks) and per-call CSRF state nonce (PS-02). Strava itself is a fake — never the network;
/// wire key sets live in StravaApiWireShapeTests.
/// </summary>
public class StravaApiControllerTests
{
    private const string UserId = "strava-user";

    private readonly FakeStravaAuthService _auth = new();
    private readonly ActivityImportProgress _progress = new(TimeProvider.System);
    private readonly OperationQueue _queue = new(TimeProvider.System);
    private readonly FakeSession _session = new();

    private StravaApiController NewController(string scheme = "https", string host = "routebase.example")
    {
        var controller = new StravaApiController(
            _auth, _progress, _queue, new TestCurrentUserService(UserId),
            NullLogger<StravaApiController>.Instance);
        var http = new DefaultHttpContext { Session = _session };
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs) dict[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static StravaAuthUrlRequest ValidAuthUrlRequest() =>
        new() { ClientId = "cid", ClientSecret = "sec" };

    /// <summary>Consume the queue's first item so its Type/UserId/Payload can be asserted —
    /// the channel is not enumerable without dequeuing.</summary>
    private async Task<OperationQueue.WorkItem> DequeueOneAsync()
    {
        await using var e = _queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        return e.Current;
    }

    // ─── POST /api/strava/import-all ───

    [Fact]
    public async Task ImportAll_NotAuthenticatedWithStrava_Returns401AndReservesNothing()
    {
        var result = await NewController().ImportAll(null);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var err = Assert.IsType<ApiError>(unauthorized.Value);
        Assert.Equal(ApiErrorCodes.Unauthorized, err.Code);
        Assert.Equal(0, _queue.PendingCountFor(UserId));
        Assert.True(_queue.TryReserveExclusiveSlot(UserId)); // slot was never taken
    }

    [Fact]
    public async Task ImportAll_Authenticated_Queues202AndEnqueuesStravaPayloadWithAfter()
    {
        _auth.Authenticated = true;
        var after = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var result = await NewController().ImportAll(new ImportAllRequest { After = after });

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<StravaImportQueuedResponse>(accepted.Value);
        Assert.Equal("Import queued in background", body.Message);
        Assert.Equal(1, _queue.PendingCountFor(UserId));
        Assert.Equal(OperationQueue.OperationType.ActivityImport, _queue.NextPendingTypeFor(UserId));

        var item = await DequeueOneAsync();
        Assert.Equal(UserId, item.UserId);
        var payload = Assert.IsType<ActivityImportPayload>(item.Payload);
        Assert.Equal(UserId, payload.UserId);
        Assert.Equal("strava", payload.ProviderId);
        Assert.Equal(after, payload.After);
    }

    [Fact]
    public async Task ImportAll_NoBody_EnqueuesNullAfter()
    {
        _auth.Authenticated = true;

        var result = await NewController().ImportAll(null);

        Assert.IsType<AcceptedResult>(result);
        var payload = Assert.IsType<ActivityImportPayload>((await DequeueOneAsync()).Payload);
        Assert.Null(payload.After);
    }

    [Fact]
    public async Task ImportAll_SlotAlreadyHeld_Returns409ConflictBodyAndDoesNotEnqueueAgain()
    {
        _auth.Authenticated = true;
        var controller = NewController();
        Assert.IsType<AcceptedResult>(await controller.ImportAll(null));

        var second = await controller.ImportAll(null);

        var conflict = Assert.IsType<ConflictObjectResult>(second);
        var body = Assert.IsType<StravaImportConflictBody>(conflict.Value);
        Assert.Equal("Import is already running", body.Message);
        Assert.Equal(ApiErrorCodes.Conflict, body.Code);
        Assert.NotNull(body.Status); // caller's live progress snapshot rides along
        Assert.Equal(1, _queue.PendingCountFor(UserId)); // no second work item
    }

    [Fact]
    public async Task ImportAll_QueueFull_Returns409AndReleasesTheSlot()
    {
        for (var i = 0; i < 100; i++) // channel capacity — fill it with another user's items
            Assert.True(_queue.TryEnqueue(new OperationQueue.WorkItem
            {
                Type = OperationQueue.OperationType.HeatmapGeneration,
                UserId = "other-user"
            }));
        _auth.Authenticated = true;

        var result = await NewController().ImportAll(null);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var err = Assert.IsType<ApiError>(conflict.Value);
        Assert.Equal(ApiErrorCodes.Conflict, err.Code);
        Assert.Equal("Operation queue is full", err.Message);
        Assert.Equal(0, _queue.PendingCountFor(UserId));
        // The failure path must release the reservation, or the user is locked out until restart.
        Assert.True(_queue.TryReserveExclusiveSlot(UserId));
    }

    // ─── GET /api/strava/status ───

    [Fact]
    public async Task Status_NotAuthenticated_ReportsFalseAndNeverFetchesAthlete()
    {
        _auth.Authenticated = false;
        _auth.Athlete = new StravaAthlete { Id = 99 }; // would leak if the short-circuit broke

        var ok = Assert.IsType<OkObjectResult>(await NewController().GetStatus());

        var body = Assert.IsType<StravaStatusResponse>(ok.Value);
        Assert.False(body.IsAuthenticated);
        Assert.Null(body.AthleteId);
    }

    [Fact]
    public async Task Status_Authenticated_ReportsAthleteId()
    {
        _auth.Authenticated = true;
        _auth.Athlete = new StravaAthlete { Id = 12345 };

        var ok = Assert.IsType<OkObjectResult>(await NewController().GetStatus());

        var body = Assert.IsType<StravaStatusResponse>(ok.Value);
        Assert.True(body.IsAuthenticated);
        Assert.Equal(12345L, body.AthleteId);
    }

    [Fact]
    public async Task Status_AuthenticatedButAthleteUnavailable_ReportsNullAthleteId()
    {
        _auth.Authenticated = true;
        _auth.Athlete = null;

        var ok = Assert.IsType<OkObjectResult>(await NewController().GetStatus());

        var body = Assert.IsType<StravaStatusResponse>(ok.Value);
        Assert.True(body.IsAuthenticated);
        Assert.Null(body.AthleteId);
    }

    // ─── POST /api/strava/auth-url ───

    [Theory]
    [InlineData("", "sec")]
    [InlineData("   ", "sec")]
    [InlineData("cid", "")]
    [InlineData("cid", "   ")]
    public async Task AuthUrl_BlankCredential_Returns400AndSavesNothing(string clientId, string clientSecret)
    {
        var request = new StravaAuthUrlRequest { ClientId = clientId, ClientSecret = clientSecret };

        var result = await NewController().GetAuthUrl(request, Config());

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(ApiErrorCodes.BadRequest, Assert.IsType<ApiError>(bad.Value).Code);
        Assert.Empty(_auth.SavedCredentials);
        Assert.False(_session.TryGetValue(SessionKeys.StravaOAuthState, out _)); // no nonce minted
    }

    [Fact]
    public async Task AuthUrl_NonLocalhost_SavesCredentialsAndDerivesRedirectFromRequest()
    {
        var ok = Assert.IsType<OkObjectResult>(
            await NewController(scheme: "https", host: "routebase.example").GetAuthUrl(ValidAuthUrlRequest(), Config()));

        var body = Assert.IsType<StravaAuthUrlResponse>(ok.Value);
        Assert.Equal(_auth.LastBuiltUrl, body.AuthUrl); // returns exactly what the auth service built
        Assert.Equal(("cid", "sec"), Assert.Single(_auth.SavedCredentials));
        Assert.Equal("https://routebase.example/KeysAndData", _auth.LastRedirectUri);
        Assert.Equal("cid", _auth.LastClientId);
    }

    [Fact]
    public async Task AuthUrl_StoresFreshCsrfStateInSessionAndPassesItToAuthService()
    {
        var controller = NewController();

        Assert.IsType<OkObjectResult>(await controller.GetAuthUrl(ValidAuthUrlRequest(), Config()));

        var state = _session.GetString(SessionKeys.StravaOAuthState);
        Assert.NotNull(state);
        Assert.Matches("^[0-9A-F]{32}$", state); // 16 random bytes, hex
        Assert.Equal(state, _auth.LastState);    // the URL carries the same nonce the session verifies

        // A second call mints a NEW nonce (PS-02: fresh per auth attempt) and the session tracks it.
        Assert.IsType<OkObjectResult>(await controller.GetAuthUrl(ValidAuthUrlRequest(), Config()));
        Assert.NotEqual(state, _auth.LastState, StringComparer.Ordinal);
        Assert.Equal(_session.GetString(SessionKeys.StravaOAuthState), _auth.LastState);
    }

    [Fact]
    public async Task AuthUrl_Localhost_PicksHttpEntryFromAspNetCoreUrls()
    {
        var config = Config(("ASPNETCORE_URLS", "https://localhost:7054; http://localhost:5099/"));

        await NewController(host: "localhost").GetAuthUrl(ValidAuthUrlRequest(), config);

        // https entry skipped, trailing slash trimmed before /KeysAndData
        Assert.Equal("http://localhost:5099/KeysAndData", _auth.LastRedirectUri);
    }

    [Fact]
    public async Task AuthUrl_Localhost_FallsBackToUrlsKeyThenDefaultPort()
    {
        await NewController(host: "localhost").GetAuthUrl(
            ValidAuthUrlRequest(), Config(("urls", "http://localhost:9999")));
        Assert.Equal("http://localhost:9999/KeysAndData", _auth.LastRedirectUri);

        await NewController(host: "localhost").GetAuthUrl(ValidAuthUrlRequest(), Config());
        Assert.Equal("http://localhost:5054/KeysAndData", _auth.LastRedirectUri);
    }

    [Fact]
    public async Task AuthUrl_Localhost_OnlyHttpsConfigured_FallsBackToDefaultHttpPort()
    {
        var config = Config(("ASPNETCORE_URLS", "https://localhost:7054"));

        await NewController(host: "localhost").GetAuthUrl(ValidAuthUrlRequest(), config);

        Assert.Equal("http://localhost:5054/KeysAndData", _auth.LastRedirectUri);
    }

    // ─── fakes ───

    /// <summary>Records credential saves and auth-URL builds; import/token members throw —
    /// the controller must never touch them.</summary>
    private sealed class FakeStravaAuthService : IStravaAuthService
    {
        public bool Authenticated;
        public StravaAthlete? Athlete;
        public readonly List<(string ClientId, string ClientSecret)> SavedCredentials = new();
        public string? LastRedirectUri, LastClientId, LastState, LastBuiltUrl;

        public Task<bool> IsAuthenticatedAsync() => Task.FromResult(Authenticated);
        public Task<StravaAthlete?> GetAthleteAsync() => Task.FromResult(Athlete);

        public string GetAuthorizationUrl(string redirectUri, string? clientId, string? state = null)
        {
            LastRedirectUri = redirectUri;
            LastClientId = clientId;
            LastState = state;
            return LastBuiltUrl = $"https://fake.strava.test/oauth?redirect={redirectUri}&state={state}";
        }

        public Task SaveCredentialsAsync(string clientId, string clientSecret)
        {
            SavedCredentials.Add((clientId, clientSecret));
            return Task.CompletedTask;
        }

        public Task<StravaTokenResponse?> ExchangeCodeForTokenAsync(string code) => throw new NotSupportedException();
        public Task DisconnectAsync() => throw new NotSupportedException();
        public Task<string?> GetSavedClientIdAsync() => throw new NotSupportedException();
        public Task<string?> GetValidAccessTokenAsync() => throw new NotSupportedException();
    }

    /// <summary>Dictionary-backed ISession — the controller stores the OAuth CSRF nonce here.</summary>
    private sealed class FakeSession : ISession
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        public bool IsAvailable => true;
        public string Id => "test-session";
        public IEnumerable<string> Keys => _store.Keys;
        public void Clear() => _store.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _store.Remove(key);
        public void Set(string key, byte[] value) => _store[key] = value;
        public bool TryGetValue(string key, out byte[] value) => _store.TryGetValue(key, out value!);
    }
}
