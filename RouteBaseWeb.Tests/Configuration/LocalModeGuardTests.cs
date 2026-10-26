using RouteBaseWeb.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RouteBaseWeb.Tests.Configuration;

/// <summary>
/// Localhost mode disables authentication, so its guard rails are load-bearing: the switch must be
/// immune to environment/CLI (only the physical file may enable it), and a non-loopback bind must be
/// classified as unsafe so the app can refuse to start.
/// </summary>
public class LocalModeGuardTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("http://localhost:5054", true)]
    [InlineData("http://127.0.0.1:5054", true)]
    [InlineData("http://[::1]:5054", true)]
    [InlineData("http://localhost:5054;http://127.0.0.1:5099", true)]
    [InlineData("http://0.0.0.0:5054", false)]
    [InlineData("http://+:5054", false)]
    [InlineData("http://*:5054", false)]
    [InlineData("http://192.168.1.10:5054", false)]
    [InlineData("http://example.com:5054", false)]
    [InlineData("http://localhost:5054;http://0.0.0.0:80", false)] // one non-loopback entry poisons the set
    public void IsLoopbackOnly_ClassifiesBindAddresses(string? urls, bool expected)
        => Assert.Equal(expected, LocalModeGuard.IsLoopbackOnly(urls));

    // Kestrel binds addresses from MORE sources than the `urls` key: a Kestrel:Endpoints:*:Url section
    // and the ASPNETCORE_HTTP(S)_PORTS shorthands (which mean http://*:port — always non-loopback) never
    // populate `urls`, so a guard that reads only that key waves a LAN-exposed no-auth instance through.
    [Fact]
    public void IsLoopbackOnly_Config_RejectsNonLoopbackKestrelEndpoint()
    {
        var config = BuildConfig(new(StringComparer.Ordinal)
        {
            ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:5000",
        });

        Assert.False(LocalModeGuard.IsLoopbackOnly(config));
    }

    [Fact]
    public void IsLoopbackOnly_Config_AcceptsLoopbackKestrelEndpoints()
    {
        var config = BuildConfig(new(StringComparer.Ordinal)
        {
            ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:5000",
            ["Kestrel:Endpoints:Alt:Url"] = "http://localhost:5001",
        });

        Assert.True(LocalModeGuard.IsLoopbackOnly(config));
    }

    [Theory]
    [InlineData("HTTP_PORTS")]
    [InlineData("HTTPS_PORTS")]
    public void IsLoopbackOnly_Config_RejectsAmbientPortBindings(string key)
    {
        var config = BuildConfig(new(StringComparer.Ordinal) { [key] = "5000" });

        Assert.False(LocalModeGuard.IsLoopbackOnly(config));
    }

    [Fact]
    public void IsLoopbackOnly_Config_ChecksTheUrlsKeyToo()
    {
        Assert.False(LocalModeGuard.IsLoopbackOnly(BuildConfig(new(StringComparer.Ordinal) { ["urls"] = "http://0.0.0.0:5054" })));
        Assert.True(LocalModeGuard.IsLoopbackOnly(BuildConfig(new(StringComparer.Ordinal) { ["urls"] = "http://localhost:5054" })));
        Assert.True(LocalModeGuard.IsLoopbackOnly(BuildConfig(new(StringComparer.Ordinal))));
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void ReadFromFile_Enables_WhenFilePresent()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.Local.json"),
                "{ \"LocalMode\": { \"Enabled\": true } }");
            Assert.True(LocalModeGuard.ReadFromFile(dir).Enabled);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadFromFile_Disabled_WhenNoFile()
    {
        var dir = CreateTempDir();
        try { Assert.False(LocalModeGuard.ReadFromFile(dir).Enabled); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // The core server-safety property: an environment variable alone can NEVER enable local mode.
    [Fact]
    public void ReadFromFile_IgnoresEnvironmentVariable()
    {
        var dir = CreateTempDir();
        Environment.SetEnvironmentVariable("LocalMode__Enabled", "true");
        try { Assert.False(LocalModeGuard.ReadFromFile(dir).Enabled); }
        finally
        {
            Environment.SetEnvironmentVariable("LocalMode__Enabled", null);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"localmode_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
