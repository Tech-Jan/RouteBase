using RouteBaseWeb.Models.Settings;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// R1 — the risk the plan flags as "breaks the app if missed", pinned directly.
///
/// The provider is rendered by <c>_Layout</c>, which is the ERROR PAGE's layout. So it runs while the app
/// is already failing, on the one route whose entire job is to still work when things are broken. Two ways
/// it could take that route down, and both are asserted here:
///
///   1. <c>ICurrentUserService.GetUserId()</c> THROWS when anonymous, and <c>/Error</c> is
///      <c>[AllowAnonymous]</c> precisely so an anonymous share visitor sees the error page instead of a
///      login redirect. Reading identity the throwing way would turn every anonymous error into a blank
///      500 with no trace id.
///   2. A database blip inside the settings read. A handled 500 whose error page ALSO throws is not a
///      handled 500 any more.
///
/// <see cref="RouteBaseWeb.Tests.Hosting.AppSettingsIslandPipelineTests"/> covers the same risk end to end
/// through the real pipeline; this covers it at the unit the fix lives in, where the failure can be
/// arranged deterministically rather than hoped for.
/// </summary>
public class AppSettingsProviderTests
{
    private static AppSettingsProvider Provider(IProfileSettingsService settings, string? userId) =>
        new(settings, new StubCurrentUser(userId), NullLogger<AppSettingsProvider>.Instance);

    [Fact]
    public async Task AnonymousRequest_YieldsDefaults_AndNeverTouchesTheStore()
    {
        var settings = new ThrowingSettings();

        var payload = await Provider(settings, userId: null).GetAsync();

        Assert.Equal(SettingsPayload.Defaults, payload);
        // Not just "did not throw": an anonymous page must do no settings I/O at all. ThrowingSettings
        // would have made a read fail loudly, so a zero count is the assertion that it was never asked.
        Assert.Equal(0, settings.Calls);
    }

    [Fact]
    public async Task StoreThrows_YieldsDefaults_RatherThanPropagating()
    {
        var payload = await Provider(new ThrowingSettings(), userId: "someone").GetAsync();

        Assert.Equal(SettingsPayload.Defaults, payload);
    }

    [Fact]
    public async Task Result_IsMemoizedForTheRequest()
    {
        // Three layouts can render the island and a page uses one of them — but the provider is scoped, so
        // a second read must not re-hit the store if that ever stops being true.
        var settings = new EmptySettings();
        var provider = Provider(settings, userId: "someone");

        var first = await provider.GetAsync();
        var callsAfterFirst = settings.Calls;
        var second = await provider.GetAsync();

        Assert.True(callsAfterFirst > 0, "the first read should have hit the store");
        Assert.Equal(callsAfterFirst, settings.Calls);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task EmptyStore_YieldsDefaults()
    {
        // The payload is assembled field by field from four separate reads, so "no rows" must reassemble
        // into exactly the defaults — not into a payload that is merely non-null.
        var payload = await Provider(new EmptySettings(), userId: "someone").GetAsync();

        Assert.Equal(SettingsPayload.Defaults, payload);
    }

    private sealed class StubCurrentUser(string? userId) : ICurrentUserService
    {
        public string GetUserId() => userId ?? throw new InvalidOperationException("anonymous");
        public string? TryGetUserId() => userId;
        public string? GetDisplayName() => null;
        public bool IsAdmin() => false;
        public void SetUserIdOverride(string id) { }
        public IDisposable BeginUserScope(string id) => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    /// <summary>Every read fails — stands in for a database blip mid-render.</summary>
    private sealed class ThrowingSettings : IProfileSettingsService
    {
        public int Calls { get; private set; }

        public Task<T?> GetSettingAsync<T>(string category, string key, string? userId = null, T? defaultValue = default)
            => Fail<T?>();
        public Task<string?> GetSettingAsync(string category, string key, string? userId = null) => Fail<string?>();
        public Task<bool> SetSettingAsync(string c, string k, object v, string u) => Fail<bool>();
        public Task SetSettingsAsync(IReadOnlyList<(string Category, string Key, object Value)> s, string u) => Fail<bool>();
        // Reads only — no fake here stores anything a delete could remove.
        public Task<bool> DeleteSettingAsync(string category, string key, string userId) =>
            Task.FromResult(false);

        public Task<Dictionary<string, string>> GetCategorySettingsAsync(string category, string? userId = null)
            => Fail<Dictionary<string, string>>();

        private Task<T> Fail<T>()
        {
            Calls++;
            throw new InvalidOperationException("simulated store failure");
        }
    }

    /// <summary>A store with no rows — the state after the seed was deleted.</summary>
    private sealed class EmptySettings : IProfileSettingsService
    {
        public int Calls { get; private set; }

        public Task<T?> GetSettingAsync<T>(string category, string key, string? userId = null, T? defaultValue = default)
        {
            Calls++;
            return Task.FromResult(defaultValue);
        }
        public Task<string?> GetSettingAsync(string category, string key, string? userId = null)
        {
            Calls++;
            return Task.FromResult<string?>(null);
        }
        public Task<bool> SetSettingAsync(string c, string k, object v, string u)
            => Task.FromResult(true);
        public Task SetSettingsAsync(IReadOnlyList<(string Category, string Key, object Value)> s, string u)
            => Task.CompletedTask;
        // Reads only — no fake here stores anything a delete could remove.
        public Task<bool> DeleteSettingAsync(string category, string key, string userId) =>
            Task.FromResult(false);

        public Task<Dictionary<string, string>> GetCategorySettingsAsync(string category, string? userId = null)
        {
            Calls++;
            return Task.FromResult(new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }
}
