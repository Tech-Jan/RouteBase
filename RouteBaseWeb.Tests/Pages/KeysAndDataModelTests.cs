using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Pages;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Models.Strava;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// Pins the Strava OAuth callback state check (PS-02): a code is only exchanged when it
/// arrives with the CSRF nonce previously stored in the session.
/// </summary>
public class KeysAndDataModelTests
{
    private static (KeysAndDataModel model, FakeStravaAuth auth, ISession session) CreateModel(
        FakeDbReset? dbReset = null)
    {
        var auth = new FakeStravaAuth();
        var httpContext = new DefaultHttpContext { Session = new FakeSession() };
        var pageContext = new PageContext { HttpContext = httpContext };
        var model = new KeysAndDataModel(auth, dbReset ?? new FakeDbReset(), new FakeMapTiler(),
            NullLogger<KeysAndDataModel>.Instance)
        {
            PageContext = pageContext,
            TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider()),
        };
        return (model, auth, httpContext.Session);
    }

    /// <summary>The type-DELETE confirmation must be verified SERVER-side: the client-JS-only guard
    /// left the irreversible multi-table hard delete one stray resubmission away.</summary>
    [Fact]
    public async Task OnPostResetDatabase_WithoutConfirmationText_DoesNotDelete()
    {
        var reset = new FakeDbReset();
        var (model, _, _) = CreateModel(reset);

        var result = await model.OnPostResetDatabaseAsync(delActivities: true);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(0, reset.ResetCount);
    }

    [Fact]
    public async Task OnPostResetDatabase_WithConfirmationText_Deletes()
    {
        var reset = new FakeDbReset();
        var (model, _, _) = CreateModel(reset);

        var result = await model.OnPostResetDatabaseAsync(delActivities: true, confirmationText: "DELETE");

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(1, reset.ResetCount);
    }

    [Fact]
    public async Task OnGet_CodeWithoutValidState_DoesNotExchange()
    {
        var (model, auth, _) = CreateModel();

        var result = await model.OnGetAsync(code: "attacker-code", state: "whatever");

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(0, auth.ExchangeCount);
    }

    [Fact]
    public async Task OnGet_CodeWithMatchingState_Exchanges()
    {
        var (model, auth, session) = CreateModel();
        session.SetString(SessionKeys.StravaOAuthState, "nonce-123");

        var result = await model.OnGetAsync(code: "real-code", state: "nonce-123");

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(1, auth.ExchangeCount);
        Assert.Null(session.GetString(SessionKeys.StravaOAuthState)); // single-use — removed
    }

    [Fact]
    public async Task OnGet_MismatchedState_DoesNotExchange()
    {
        var (model, auth, session) = CreateModel();
        session.SetString(SessionKeys.StravaOAuthState, "expected");

        var result = await model.OnGetAsync(code: "attacker-code", state: "forged");

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(0, auth.ExchangeCount);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeStravaAuth : IStravaAuthService
    {
        public int ExchangeCount { get; private set; }

        public Task<StravaTokenResponse?> ExchangeCodeForTokenAsync(string code)
        {
            ExchangeCount++;
            return Task.FromResult<StravaTokenResponse?>(new StravaTokenResponse());
        }

        public string GetAuthorizationUrl(string redirectUri, string? clientId, string? state = null) => "https://strava/authorize";
        public Task<bool> IsAuthenticatedAsync() => Task.FromResult(false);
        public Task<StravaAthlete?> GetAthleteAsync() => Task.FromResult<StravaAthlete?>(null);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<string?> GetSavedClientIdAsync() => Task.FromResult<string?>(null);
        public Task SaveCredentialsAsync(string clientId, string clientSecret) => Task.CompletedTask;
        public Task<string?> GetValidAccessTokenAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakeDbReset : IDatabaseResetService
    {
        public int ResetCount;
        public Task ResetDatabaseAsync(DeleteOptions opts)
        {
            ResetCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMapTiler : IMapTilerService
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);
        public Task<bool> SaveApiKeyAsync(string apiKey) => Task.FromResult(true);
        public Task<bool> HasApiKeyAsync() => Task.FromResult(false);
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>(StringComparer.Ordinal);
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

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
