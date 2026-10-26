using RouteBaseWeb.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace RouteBaseWeb.Tests.Auth;

/// <summary>
/// CA-01: an API-client request (X-Api-Key present) must get a plain 401 on challenge,
/// not a 302 redirect to the OIDC identity provider.
/// </summary>
public class ApiClientChallengeGateTests
{
    [Fact]
    public void ShortCircuit_WithApiKeyHeader_Returns401AndTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "bogus";

        var result = ApiClientChallengeGate.ShortCircuit(context);

        Assert.True(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public void ShortCircuit_WithoutHeader_ReturnsFalseAndLeavesStatus()
    {
        var context = new DefaultHttpContext();

        var result = ApiClientChallengeGate.ShortCircuit(context);

        Assert.False(result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }
}
