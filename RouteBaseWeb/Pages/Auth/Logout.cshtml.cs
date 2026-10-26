using RouteBaseWeb.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages.Auth;

public class LogoutModel(LocalModeOptions localMode) : PageModel
{
    public IActionResult OnPost()
        // Local mode registers neither the Cookie nor the OIDC scheme, so SignOut throws ("no sign-out
        // authentication handler is registered") on a direct POST (PI-06). The layout hides the form
        // there, but the endpoint stays routable — make it the no-op redirect it visually is.
        => localMode.Enabled
            ? Redirect("/")
            : SignOut(new AuthenticationProperties { RedirectUri = "/" },
                CookieAuthenticationDefaults.AuthenticationScheme,
                OpenIdConnectDefaults.AuthenticationScheme);
}
