using RouteBaseWeb.Auth;
using RouteBaseWeb.Configuration;
using RouteBaseWeb.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// AdminGate: server mode gates on the Admin:Users hash list; localhost mode grants admin to whoever is
/// authenticated AS the single local user (it owns the machine), independent of that list — and to no
/// one else (C6). Share-link visitors ride the same pipeline as non-local principals (share:anon), so an
/// "Enabled alone means admin" short-circuit would wave them through every IAdminGate consumer
/// (/Admin/Errors, admin nav).
/// </summary>
public class AdminGateTests
{
    private static AdminGate Create(string userId, string[]? adminUsers, bool localMode)
    {
        var data = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (adminUsers is not null)
            for (var i = 0; i < adminUsers.Length; i++)
                data[$"Admin:Users:{i}"] = adminUsers[i];
        var config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();

        return new AdminGate(config, new TestCurrentUserService(userId), new LocalModeOptions { Enabled = localMode });
    }

    [Fact]
    public void LocalMode_MakesLocalUserAdmin_IgnoringAdminList()
    {
        // No Admin:Users entry at all — the authenticated local user is still admin.
        var gate = Create(LocalModeOptions.UserId, adminUsers: null, localMode: true);

        Assert.True(gate.IsAdminUser());
    }

    [Fact]
    public void LocalMode_SharePrincipal_IsNotAdmin()
    {
        // The C6 pin: a share visitor is authenticated (as share:anon), but is not the local user.
        var gate = Create(ShareLinkDefaults.ViewerUserId, adminUsers: null, localMode: true);

        Assert.False(gate.IsAdminUser());
    }

    [Fact]
    public void LocalMode_ForeignPrincipal_IsNotAdmin()
    {
        var gate = Create("someone-else", adminUsers: ["someone-else"], localMode: true);

        Assert.False(gate.IsAdminUser());
    }

    [Fact]
    public void ServerMode_AdminUser_InList_IsAdmin()
    {
        var gate = Create("u-1", adminUsers: ["u-1"], localMode: false);

        Assert.True(gate.IsAdminUser());
    }

    [Fact]
    public void ServerMode_NonAdminUser_NotInList_IsNotAdmin()
    {
        var gate = Create("u-2", adminUsers: ["u-1"], localMode: false);

        Assert.False(gate.IsAdminUser());
    }

    [Fact]
    public void ServerMode_LocalUserId_NotInList_IsNotAdmin()
    {
        // The fixed local id carries no magic outside local mode.
        var gate = Create(LocalModeOptions.UserId, adminUsers: ["u-1"], localMode: false);

        Assert.False(gate.IsAdminUser());
    }
}
