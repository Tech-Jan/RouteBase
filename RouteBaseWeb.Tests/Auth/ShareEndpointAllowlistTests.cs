using System.Reflection;
using RouteBaseWeb.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Xunit;

namespace RouteBaseWeb.Tests.Auth;

/// <summary>
/// The share read surface is a FIXED, reviewed list.
///
/// This enumerates every controller action and page model in the app and asserts that exactly the expected
/// set admits a share principal. Adding an endpoint to the share surface therefore cannot happen quietly —
/// it fails here until someone updates the list, which is the point: each entry is a deliberate decision
/// about what an anonymous link holder may read.
/// </summary>
public class ShareEndpointAllowlistTests
{
    /// <summary>
    /// Everything reachable with a share token. Keep alphabetical; each entry needs a reason.
    /// </summary>
    private static readonly string[] ExpectedShareReachable =
    [
        // The share view itself.
        "RouteBaseWeb.Pages.Share.ShareViewModel",
        // Photo list + rendition bytes for the granted activity, mounted under /s/{token}/api.
        "RouteBaseWeb.Api.ShareReadController",
        // Owner-only effort tracks + per-point performance for the granted segment, mounted under
        // /s/{token}/api/segment. Reads go through ShareSegmentReader, which has no ambient identity and
        // no visibility service — so no third party's effort can reach it.
        "RouteBaseWeb.Api.ShareSegmentReadController",
        // Live-recording map poll (status + GPS points) for the granted track, mounted under
        // /s/{token}/api/recording. Keyed by the grant's track id (never a route param), resolves the one
        // RecordingSession for it, and serves only the map — no rider identity, no friends' data.
        "RouteBaseWeb.Api.ShareRecordingReadController",
        // Redacted race replay (course + checkpoints + field) for the granted race, mounted under
        // /s/{token}/api/race. Keyed by the grant's race id (never a route param). Reads go through
        // ShareRaceReader, which has no ambient identity and projects into a DTO family with no entrant
        // name/bib/category/gender/club property — and which drops every real rider whose ride the grant's
        // owner does not own.
        "RouteBaseWeb.Api.ShareRaceReadController",
    ];

    private static Assembly WebAssembly => typeof(ShareLinkDefaults).Assembly;

    /// <summary>Types carrying an [Authorize] that names the ShareLinkRead policy or the ShareLink scheme.</summary>
    private static IEnumerable<Type> ShareReachableTypes() =>
        WebAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) || typeof(PageModel).IsAssignableFrom(t))
            .Where(AdmitsShareLink);

    private static bool AdmitsShareLink(Type type)
    {
        var attributes = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>(inherit: true)));

        return attributes.Any(a =>
            a.Policy == ShareLinkDefaults.ReadPolicy
            || (a.AuthenticationSchemes?.Contains(ShareLinkDefaults.AuthenticationScheme, StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public void ShareReachableEndpoints_ExactlyMatchTheAllowlist()
    {
        var actual = ShareReachableTypes()
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var expected = ExpectedShareReachable.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.True(actual.SequenceEqual(expected),
            "The share-reachable endpoint set changed. Every entry is a deliberate decision about what an "
            + "anonymous link holder may read — update ExpectedShareReachable only after confirming the new "
            + "endpoint re-asserts the grant covers the requested resource.\n"
            + $"  actual:   [{string.Join(", ", actual)}]\n"
            + $"  expected: [{string.Join(", ", expected)}]");
    }

    [Fact]
    public void Allowlist_IsNotVacuous()
    {
        // Guards the reflection above: if the predicate silently stopped matching (renamed constant,
        // changed attribute shape), the set-equality test would pass only while the allowlist was empty.
        Assert.NotEmpty(ShareReachableTypes());
    }

    [Fact]
    public void MintController_IsNotShareReachable()
    {
        // A share principal must never be able to mint further links from the link it arrived on.
        Assert.DoesNotContain(
            typeof(RouteBaseWeb.Api.ShareLinksController).FullName,
            ShareReachableTypes().Select(t => t.FullName), StringComparer.Ordinal);
    }

    /// <summary>
    /// READ-only, enforced rather than assumed.
    ///
    /// The set-equality test above pins which TYPES admit a share principal — it says nothing about the
    /// verbs on them. Adding <c>[HttpPost]</c> to ShareReadController passed every other test in the
    /// slice while being immediately reachable with a share token: the policy contains nothing that says
    /// "GET only", so read-only was a property of which attributes the two actions happened to carry.
    /// This makes it a property the compiler-plus-test pair actually holds.
    /// </summary>
    [Fact]
    public void EveryShareReachableAction_IsAGetAndNothingElse()
    {
        var mutating = new[]
        {
            typeof(HttpPostAttribute), typeof(HttpPutAttribute),
            typeof(HttpDeleteAttribute), typeof(HttpPatchAttribute),
        };

        foreach (var type in ShareReachableTypes())
        {
            // PageModel handlers are named by convention (OnGet/OnPost/…), controllers by attribute.
            if (typeof(PageModel).IsAssignableFrom(type))
            {
                foreach (var handler in type.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                                        | BindingFlags.DeclaredOnly)
                             .Where(m => m.Name.StartsWith("On", StringComparison.Ordinal)))
                    Assert.True(
                        handler.Name.StartsWith("OnGet", StringComparison.Ordinal),
                        $"{type.Name}.{handler.Name} is share-reachable and is not a GET handler. "
                        + "The share surface is read-only; a share token must not reach a mutation.");
                continue;
            }

            foreach (var action in type.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                                   | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName))
            {
                var attrs = action.GetCustomAttributes(inherit: true).Select(a => a.GetType()).ToArray();
                Assert.False(
                    attrs.Any(mutating.Contains),
                    $"{type.Name}.{action.Name} is share-reachable and carries a mutating verb. "
                    + "The share surface is read-only; a share token must not reach a mutation.");
                Assert.True(
                    attrs.Contains(typeof(HttpGetAttribute)),
                    $"{type.Name}.{action.Name} is share-reachable but carries no [HttpGet]. Every action "
                    + "on the share surface must declare GET explicitly, so an un-attributed action "
                    + "(which MVC would route for ANY verb) cannot slip in.");
            }
        }
    }

    /// <summary>
    /// The route-parameter NAME is a contract, not a convention. The auth handler reads the token from
    /// the route value named <see cref="ShareLinkDefaults.RouteTokenKey"/>, and the rate limiter keys off
    /// that same value's presence. An endpoint whose template names the parameter differently fails
    /// closed for auth (no route value → 404) — but silently falls out of every limiter tier first.
    /// </summary>
    [Fact]
    public void EveryShareReachableRoute_NamesTheRouteTokenKey()
    {
        var tokenParameter = "{" + ShareLinkDefaults.RouteTokenKey;

        foreach (var type in ShareReachableTypes())
        {
            if (typeof(PageModel).IsAssignableFrom(type)) continue; // page routes live in .cshtml, below

            var templates = type.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(r => r.Template).ToArray();
            Assert.True(templates.Length > 0, $"{type.Name} carries no [Route] template.");
            Assert.All(templates, t =>
                Assert.Contains(tokenParameter, t, StringComparison.Ordinal));
        }

        // The share page's template is its @page directive.
        var view = ReadRepoFile(Path.Combine("RouteBaseWeb", "Pages", "Share", "View.cshtml"));
        var pageDirective = view[..view.IndexOf('\n', StringComparison.Ordinal)];
        Assert.StartsWith("@page", pageDirective, StringComparison.Ordinal); // anti-vacuity: we did read the directive line
        Assert.Contains(tokenParameter, pageDirective, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativeToRoot)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RouteBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, relativeToRoot);
        Assert.True(File.Exists(path), $"repo file not found: {relativeToRoot}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void NoOwnerMutationControllerIsShareReachable()
    {
        // Spot-check the controllers that would hurt most, by name rather than by the generic sweep above,
        // so the intent survives a future refactor of the sweep.
        var reachable = ShareReachableTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "ActivitiesApiController", "TrashApiController", "TrackEditApiController",
                     "ReviewApiController", "PhotosApiController", "SegmentsApiController",
                     "ApiKeyController", "RacesApiController", "HeatmapsApiController",
                 })
            Assert.DoesNotContain(forbidden, reachable);
    }
}
