using RouteBaseWeb.Pages.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// Pins the open-redirect guard on <see cref="LoginModel.OnPost"/> (PS-01): an external
/// returnUrl must never survive into the OIDC challenge's RedirectUri.
/// </summary>
public class LoginModelTests
{
    private static LoginModel CreateModel() => new() { Url = new FakeUrlHelper() };

    [Fact]
    public void OnPost_ExternalReturnUrl_ChallengesWithIndex()
    {
        var result = Assert.IsType<ChallengeResult>(CreateModel().OnPost("https://evil.example"));
        Assert.Equal("/Index", result.Properties?.RedirectUri);
    }

    [Fact]
    public void OnPost_ProtocolRelativeReturnUrl_ChallengesWithIndex()
    {
        var result = Assert.IsType<ChallengeResult>(CreateModel().OnPost("//evil.example"));
        Assert.Equal("/Index", result.Properties?.RedirectUri);
    }

    [Fact]
    public void OnPost_LocalReturnUrl_IsPreserved()
    {
        var result = Assert.IsType<ChallengeResult>(CreateModel().OnPost("/Activities"));
        Assert.Equal("/Activities", result.Properties?.RedirectUri);
    }

    [Fact]
    public void OnPost_NullReturnUrl_ChallengesWithIndex()
    {
        var result = Assert.IsType<ChallengeResult>(CreateModel().OnPost(null));
        Assert.Equal("/Index", result.Properties?.RedirectUri);
    }

    /// <summary>Minimal IUrlHelper: IsLocalUrl mirrors the framework rule; Page("/Index") resolves via RouteUrl.</summary>
    private sealed class FakeUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } =
            new(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        public string? Action(UrlActionContext actionContext) => null;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) =>
            !string.IsNullOrEmpty(url) &&
            ((url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')))
             || (url.Length > 1 && url[0] == '~' && url[1] == '/'));
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => "/Index";
    }
}
