using System.Security.Claims;
using RouteBaseWeb.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins <see cref="CurrentUserService.BeginUserScope"/>'s RESTORING semantics — the share slice's widening
/// primitive. The share page widens to the owner for one read and must stop looking like the owner
/// immediately afterwards: a leaked override would reach Details.cshtml.cs's <c>IsOwner</c> check and render
/// edit/delete/version-history controls on an anonymous page. The exception path matters as much as the
/// happy one, because a read that throws mid-widen must not leave the request impersonating.
/// </summary>
public class CurrentUserScopeTests
{
    private static CurrentUserService NewService(string? authenticatedUserId)
    {
        var accessor = new HttpContextAccessor();
        if (authenticatedUserId is not null)
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, authenticatedUserId)], "Test");
            accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        }
        else
        {
            accessor.HttpContext = new DefaultHttpContext();
        }

        return new CurrentUserService(accessor, new ConfigurationBuilder().Build());
    }

    [Fact]
    public void BeginUserScope_RestoresTheAuthenticatedIdentity_OnDispose()
    {
        var svc = NewService("real-user");

        using (svc.BeginUserScope("owner-user"))
            Assert.Equal("owner-user", svc.GetUserId());

        Assert.Equal("real-user", svc.GetUserId());
    }

    [Fact]
    public void BeginUserScope_RestoresOnTheExceptionPath()
    {
        var svc = NewService("real-user");

        // Explicitly an Action: a block lambda whose only statement throws is convertible to Func<Task>
        // too, and the resulting ambiguity binds the obsolete async overload.
        Action widenThenThrow = () =>
        {
            using (svc.BeginUserScope("owner-user"))
                throw new InvalidOperationException("read blew up mid-widen");
        };

        Assert.Throws<InvalidOperationException>(widenThenThrow);

        Assert.Equal("real-user", svc.GetUserId());
    }

    [Fact]
    public void BeginUserScope_OverAnAnonymousPrincipal_RevertsToThrowing()
    {
        // The share case: an anonymous request widens for one read, then must go back to having NO usable
        // identity — not silently inherit the owner's for the rest of the request.
        var svc = NewService(authenticatedUserId: null);

        using (svc.BeginUserScope("owner-user"))
            Assert.Equal("owner-user", svc.GetUserId());

        Assert.Throws<InvalidOperationException>(() => svc.GetUserId());
    }

    [Fact]
    public void BeginUserScope_Nests_InnermostWins_AndUnwindsInOrder()
    {
        var svc = NewService("real-user");

        using (svc.BeginUserScope("outer"))
        {
            Assert.Equal("outer", svc.GetUserId());
            using (svc.BeginUserScope("inner"))
                Assert.Equal("inner", svc.GetUserId());
            Assert.Equal("outer", svc.GetUserId());
        }

        Assert.Equal("real-user", svc.GetUserId());
    }

    [Fact]
    public void SetUserIdOverride_RemainsOneWay_ForQueueWorkers()
    {
        // OperationScope depends on this: the scope IS the operation, so the override must persist.
        // Documented here so a future "make it symmetric" refactor has to break a test to proceed.
        var svc = NewService("real-user");

        svc.SetUserIdOverride("worker-user");

        Assert.Equal("worker-user", svc.GetUserId());
    }
}
