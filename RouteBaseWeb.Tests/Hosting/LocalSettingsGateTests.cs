using RouteBaseWeb.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// A5: appsettings.Local.json merges into the main configuration ONLY when the file itself enables
/// LocalMode. On a server (LocalMode off), dropping the file next to the binary must influence nothing —
/// otherwise a single file write could silently redirect Authentik:*, Admin:Users, or connection strings
/// while authentication stays on and nobody notices.
/// </summary>
[Collection(AppPipelineCollection.Name)]
public class LocalSettingsGateTests
{
    private const string EvilAuthority = "https://evil.example/";

    [Theory]
    [InlineData("""{ "LocalMode": { "Enabled": false }, "Authentik": { "Authority": "https://evil.example/" }, "Canary": "from-local-json" }""")]
    [InlineData("""{ "Authentik": { "Authority": "https://evil.example/" }, "Canary": "from-local-json" }""")]
    public async Task ServerMode_DroppedLocalJson_KeysAreNotVisibleInConfig(string localJson)
    {
        try
        {
            LocalSettingsFile.Write(localJson);
            await using var factory = new ServerModeAppFactory();

            var config = factory.Services.GetRequiredService<IConfiguration>();
            Assert.Null(config["Canary"]);
            Assert.NotEqual(EvilAuthority, config["Authentik:Authority"], StringComparer.Ordinal);

            // And the mode itself stayed off: no local scheme, server auth intact.
            Assert.False(factory.Services.GetRequiredService<LocalModeOptions>().Enabled);
            var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
            Assert.Null(await schemes.GetSchemeAsync(LocalModeOptions.AuthenticationScheme));
        }
        finally
        {
            LocalSettingsFile.Delete();
        }
    }

    [Fact]
    public void LocalMode_EnabledFile_DoesMerge_IntoMainConfig()
    {
        // The positive control: the gate must not decay into "never load the file" — the desktop
        // artifact's Local.json legitimately carries Urls/Memory overrides that must keep applying.
        try
        {
            LocalSettingsFile.Write("""{ "LocalMode": { "Enabled": true }, "Canary": "from-local-json" }""");
            using var factory = new LocalModeAppFactory();

            var config = factory.Services.GetRequiredService<IConfiguration>();
            Assert.Equal("from-local-json", config["Canary"]);
            Assert.True(factory.Services.GetRequiredService<LocalModeOptions>().Enabled);
        }
        finally
        {
            LocalSettingsFile.Delete();
        }
    }
}
