using RouteBaseWeb.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace RouteBaseWeb.Tests.Auth;

/// <summary>
/// CC-01: Forbid()/Challenge() on an /api/* route must answer with a status code, not a 302 to an HTML
/// page — fetch() follows the redirect and reads the resulting 200 as success, so a refused write was
/// being shown to the user as saved. Page navigations must still redirect.
/// </summary>
public class ApiCookieRedirectEventsTests
{
    private static (DefaultHttpContext Ctx, RedirectContext<CookieAuthenticationOptions> Redirect) Build(
        string path, string redirectUri)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var redirect = new RedirectContext<CookieAuthenticationOptions>(
            ctx, scheme, new CookieAuthenticationOptions(), new AuthenticationProperties(), redirectUri);
        return (ctx, redirect);
    }

    [Fact]
    public async Task ApiPath_AccessDenied_Returns403NotRedirect()
    {
        var (ctx, redirect) = Build("/api/RacesApi/1/participants", "/Auth/AccessDenied");

        await ApiCookieRedirectEvents.Answer(redirect, StatusCodes.Status403Forbidden);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.False(ctx.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public async Task PagePath_AccessDenied_StillRedirects()
    {
        var (ctx, redirect) = Build("/Activities", "/Auth/AccessDenied");

        await ApiCookieRedirectEvents.Answer(redirect, StatusCodes.Status403Forbidden);

        Assert.Equal(StatusCodes.Status302Found, ctx.Response.StatusCode);
        Assert.Equal("/Auth/AccessDenied", ctx.Response.Headers["Location"]);
    }

    [Fact]
    public async Task ApiPath_Login_Returns401NotRedirect()
    {
        var (ctx, redirect) = Build("/api/activities/5/versions", "/Auth/Login");

        await ApiCookieRedirectEvents.Answer(redirect, StatusCodes.Status401Unauthorized);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.False(ctx.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public async Task PagePath_Login_StillRedirects()
    {
        var (ctx, redirect) = Build("/Races/Replay", "/Auth/Login");

        await ApiCookieRedirectEvents.Answer(redirect, StatusCodes.Status401Unauthorized);

        Assert.Equal(StatusCodes.Status302Found, ctx.Response.StatusCode);
        Assert.Equal("/Auth/Login", ctx.Response.Headers["Location"]);
    }

    [Theory]
    [InlineData("/api", true)]
    [InlineData("/api/races", true)]
    [InlineData("/API/races", true)]          // path matching is case-insensitive
    [InlineData("/apikeys", false)]           // segment boundary: not an /api/* route
    [InlineData("/Activities/Trash", false)]
    public void IsApiRequest_MatchesOnSegmentBoundary(string path, bool expected)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;

        Assert.Equal(expected, ApiCookieRedirectEvents.IsApiRequest(ctx.Request));
    }

    // Decision 8: CSRF's only mitigation on the /api mutation surface is this cookie's SameSite policy —
    // pin it explicitly so a future edit can't silently widen it to None.
    [Fact]
    public void Configure_PinsSameSiteLax()
    {
        var options = new CookieAuthenticationOptions();

        ApiCookieRedirectEvents.Configure(options);

        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
    }
}
