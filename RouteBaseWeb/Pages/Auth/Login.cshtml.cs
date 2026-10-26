using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages.Auth;

[AllowAnonymous]
public class LoginModel : PageModel
{
    public IActionResult OnGet(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Index");

        return Page();
    }

    public IActionResult OnPost(string? returnUrl = null)
    {
        // Only honor local return URLs — an unvalidated returnUrl is an open-redirect
        // phishing amplifier (attacker link → external site right after OIDC sign-in).
        var redirectUri = (returnUrl != null && Url.IsLocalUrl(returnUrl)) ? returnUrl : Url.Page("/Index");
        return Challenge(
            new AuthenticationProperties { RedirectUri = redirectUri },
            OpenIdConnectDefaults.AuthenticationScheme);
    }
}
