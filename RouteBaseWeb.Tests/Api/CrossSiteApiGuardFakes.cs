using Microsoft.AspNetCore.Mvc;
using RouteBaseWeb.Api;

// Test doubles for CrossSiteApiGuardFilter.Applies, which scopes by controller namespace. They live in the
// TEST assembly, so the production-assembly scan in ApiSurfaceGuardTests never sees them and MVC never maps a
// route to them — they exist only to feed the pure predicate a controller in / outside RouteBaseWeb.Api.

namespace RouteBaseWeb.Api
{
    /// <summary>In the api namespace: exercises the api-scoped and opt-out branches of the guard.</summary>
    public sealed class GuardProbeApiController : ControllerBase
    {
        [HttpPost("probe")] public IActionResult Mutating() => Ok();

        [HttpPost("probe")]
        [AllowCrossSiteBrowserRequests]
        public IActionResult OptedOut() => Ok();
    }
}

namespace RouteBaseWeb.Api.Internal
{
    using Microsoft.AspNetCore.Mvc;

    /// <summary>In a SUB-namespace of the api surface (PI-03): the guard must still cover it — an exact
    /// namespace match would let a future controller filed here bypass the cross-site guard.</summary>
    public sealed class GuardProbeSubNamespaceApiController : ControllerBase
    {
        [HttpPost("probe")] public IActionResult Mutating() => Ok();
    }
}

namespace RouteBaseWeb.Tests.Api.Fakes
{
    /// <summary>Outside the api namespace: the guard's namespace scope must exclude it.</summary>
    public sealed class GuardProbeNonApiController : ControllerBase
    {
        [HttpPost("probe")] public IActionResult Mutating() => Ok();
    }
}
