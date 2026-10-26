using System.Net;
using System.Text;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Credentials;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins the no-token path after the SV-03 swap from a shared scoped context to
/// <see cref="IDbContextFactory{TContext}"/>. Uses a shared-cache in-memory SQLite DB with a
/// keep-alive connection; the factory opens a fresh short-lived context per call, as in production.
/// </summary>
public class StravaAuthServiceTests : IAsyncLifetime
{
    private SqliteConnection _keepAlive = null!;
    private TestDbFactory _factory = null!;

    // One key ring per fixture: MakeSut's writes and the raw-row assertions must share it.
    private readonly ICredentialProtector _protector = new CredentialProtector(
        new EphemeralDataProtectionProvider(NullLoggerFactory.Instance),
        NullLogger<CredentialProtector>.Instance);

    public async Task InitializeAsync()
    {
        var connectionString = $"Data Source=file:strava_auth_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        await _keepAlive.OpenAsync(); // keeps the in-memory DB alive for the fixture's lifetime

        _factory = new TestDbFactory(connectionString);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task GetValidAccessTokenAsync_NoToken_ReturnsNull()
    {
        var sut = new StravaAuthService(
            _factory,
            new NoHttpClientFactory(),
            NullLogger<StravaAuthService>.Instance,
            new FakeCurrentUser("no-token-user"),
            _protector,
            new TestClock(T0));

        var token = await sut.GetValidAccessTokenAsync();

        Assert.Null(token);
    }

    // Regression: after an expired token is refreshed on a separate DbContext, the re-read must
    // return the NEW access token — not EF's stale identity-map copy that caused a 401 until reconnect.
    [Fact]
    public async Task GetValidAccessTokenAsync_ExpiredToken_ReturnsRefreshedTokenNotStale()
    {
        await SeedCredentialsAsync();
        await SeedTokenAsync(accessToken: "old-expired", refreshToken: "old-refresh",
            expiresAt: T0.UtcDateTime.AddHours(-1), athleteId: 12345);

        var sut = MakeSut(RefreshResponse(accessToken: "new-access", refreshToken: "new-refresh"));

        var token = await sut.GetValidAccessTokenAsync();

        Assert.Equal("new-access", token);
    }

    // Regression: refresh responses carry no athlete object — the refresh must preserve the existing
    // AthleteId, not clobber it to 0.
    [Fact]
    public async Task GetValidAccessTokenAsync_Refresh_PreservesAthleteId()
    {
        await SeedCredentialsAsync();
        await SeedTokenAsync(accessToken: "old-expired", refreshToken: "old-refresh",
            expiresAt: T0.UtcDateTime.AddHours(-1), athleteId: 12345);

        var sut = MakeSut(RefreshResponse(accessToken: "new-access", refreshToken: "new-refresh"));
        await sut.GetValidAccessTokenAsync();

        await using var db = _factory.CreateDbContext();
        var row = await db.AppCredentials.SingleAsync(c => c.Type == CredentialTypes.StravaToken);
        Assert.Equal(12345, row.AthleteId);
        // Decision ⑥: rows are encrypted at rest — the raw column is a DP payload, not the token.
        Assert.True(_protector.IsProtected(row.AccessToken!));
        Assert.True(_protector.IsProtected(row.RefreshToken!));
        Assert.Equal("new-access", _protector.Unprotect(row.AccessToken!));
        Assert.Equal("new-refresh", _protector.Unprotect(row.RefreshToken!));
    }

    // Regression: a row with NULL expires_at (legacy/partial write) must be treated as "expiry unknown →
    // refresh to learn it". The lifted null comparison silently skipped the refresh, and the cache entry
    // was stored with Expiry = DateTime.MinValue — so every call paid the semaphore + DB read and the
    // never-refreshed token ended in unrecoverable 401s until reconnect.
    [Fact]
    public async Task GetValidAccessTokenAsync_NullExpiresAt_RefreshesInsteadOfTrustingForever()
    {
        await SeedCredentialsAsync();
        await SeedTokenAsync(accessToken: "unknown-expiry", refreshToken: "old-refresh",
            expiresAt: null, athleteId: 12345);

        var sut = MakeSut(RefreshResponse(accessToken: "new-access", refreshToken: "new-refresh"));

        var token = await sut.GetValidAccessTokenAsync();

        Assert.Equal("new-access", token);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private const string TestUser = "refresh-user";

    /// <summary>Fixed fake now (K2 seam): expiry math runs against this, not the wall clock.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private StravaAuthService MakeSut(string refreshJson, TimeProvider? clock = null) =>
        new(_factory, new StubHttpClientFactory(refreshJson),
            NullLogger<StravaAuthService>.Instance, new FakeCurrentUser(TestUser), _protector,
            clock ?? new TestClock(T0));

    private static string RefreshResponse(string accessToken, string refreshToken)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds();
        // No "athlete" key — matches Strava's real refresh response.
        return $$"""
        {"token_type":"Bearer","access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","expires_at":{{expiresAt}},"expires_in":21600}
        """;
    }

    private async Task SeedCredentialsAsync()
    {
        await using var db = _factory.CreateDbContext();
        db.Users.Add(new AppUser { UserId = TestUser }); // AppCredentials.user_id FK
        // Secret seeded ENCRYPTED (the post-migration steady state) so the refresh flow exercises
        // the real decrypt-on-read path; legacy-plaintext passthrough is pinned in CredentialProtectionTests.
        db.AppCredentials.Add(new AppCredentials
        {
            Type = CredentialTypes.StravaCredentials, UserId = TestUser,
            ClientId = "client-1", ClientSecret = _protector.Protect("secret-1"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedTokenAsync(string accessToken, string refreshToken, DateTime? expiresAt, long athleteId)
    {
        await using var db = _factory.CreateDbContext();
        db.AppCredentials.Add(new AppCredentials
        {
            Type = CredentialTypes.StravaToken, UserId = TestUser,
            AccessToken = accessToken, RefreshToken = refreshToken,
            ExpiresAt = expiresAt, AthleteId = athleteId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class TestDbFactory(string connectionString) : IDbContextFactory<RouteBaseDbContext>
    {
        public RouteBaseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(connectionString).Options);
    }

    private sealed class FakeCurrentUser(string userId) : ICurrentUserService
    {
        public string GetUserId() => userId;
        public string? TryGetUserId() => userId;
        public string? GetDisplayName() => null;
        public bool IsAdmin() => false;
        public void SetUserIdOverride(string id) { }
        public IDisposable BeginUserScope(string userId) => NoopUserScope.Instance;
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Returns a canned refresh response for any request (only the token endpoint is hit here).
    private sealed class StubHttpClientFactory(string json) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(json));
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
