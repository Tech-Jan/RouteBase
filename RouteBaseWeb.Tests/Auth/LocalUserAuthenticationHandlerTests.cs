using System.Security.Claims;
using System.Text.Encodings.Web;
using RouteBaseWeb.Auth;
using RouteBaseWeb.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace RouteBaseWeb.Tests.Auth;

/// <summary>
/// Localhost mode: the handler must authenticate EVERY request as the fixed local user, stamping the same
/// NameIdentifier/sub claims OIDC would — that is what satisfies the global RequireAuthenticatedUser
/// fallback policy with no login and keeps CurrentUserService.GetUserId() (and its ~94 call sites) working.
/// </summary>
public class LocalUserAuthenticationHandlerTests
{
    private static async Task<AuthenticateResult> AuthenticateAsync()
    {
        var handler = new LocalUserAuthenticationHandler(
            new SchemeOptionsMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default);
        var scheme = new AuthenticationScheme(
            LocalModeOptions.AuthenticationScheme, LocalModeOptions.AuthenticationScheme,
            typeof(LocalUserAuthenticationHandler));
        await handler.InitializeAsync(scheme, new DefaultHttpContext());
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task Authenticates_As_Fixed_Local_User()
    {
        var result = await AuthenticateAsync();

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.Equal(LocalModeOptions.UserId, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(LocalModeOptions.UserId, principal.FindFirstValue("sub"));
    }

    [Fact]
    public async Task Supplies_Display_Name_Claims()
    {
        var result = await AuthenticateAsync();

        var principal = result.Principal!;
        Assert.Equal(LocalModeOptions.DisplayName, principal.FindFirstValue("preferred_username"));
        Assert.Equal(LocalModeOptions.DisplayName, principal.FindFirstValue("name"));
    }

    private sealed class SchemeOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        private readonly AuthenticationSchemeOptions _options = new();
        public AuthenticationSchemeOptions CurrentValue => _options;
        public AuthenticationSchemeOptions Get(string? name) => _options;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
