using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// Boots the REAL Program.cs — services, auth registration, middleware order, schema init — in SERVER
/// mode (no <c>appsettings.Local.json</c> next to the test assembly, so <c>LocalMode.Enabled</c> is
/// false and the Authentik OIDC stack registers). The database and Data Protection key ring are created
/// fresh under the test bin's <c>database/</c> dir; requests run against the in-memory test server.
/// </summary>
public class ServerModeAppFactory : WebApplicationFactory<Program>
{
    /// <summary>Where a challenge would send the browser. A 302 pointing here proves the OIDC stack
    /// answered (i.e. the request was NOT auto-signed-in). Never resolved — no test follows it.</summary>
    public const string AuthorizationEndpoint = "https://authentik.test/oauth2/authorize";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program.cs hard-requires <exeDir>/wwwroot and resolves everything else against
        // AppContext.BaseDirectory; the SDK copies the real wwwroot here, but an empty dir is the
        // contract, so guarantee it.
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "wwwroot"));

        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // The committed appsettings.json ships ClientId "" (the real value is injected on the
            // server); OIDC options validation rejects an empty ClientId on first challenge.
            ["Authentik:ClientId"] = "routebase-tests",
        }));

        // The OIDC handler fetches {Authority}/.well-known/openid-configuration on first challenge, and
        // tests run offline: pin a static metadata document so a challenge builds its redirect with no
        // network I/O. Program.cs's registration (schemes, events, config reads) still runs untouched.
        builder.ConfigureTestServices(services =>
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration
                    {
                        Issuer = "https://authentik.test/",
                        AuthorizationEndpoint = AuthorizationEndpoint,
                        TokenEndpoint = "https://authentik.test/oauth2/token",
                    })));
    }

    /// <summary>Client that surfaces the challenge redirect instead of following it into the fake IdP.</summary>
    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
