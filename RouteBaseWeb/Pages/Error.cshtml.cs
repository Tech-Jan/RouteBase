using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages;

// AllowAnonymous (PI-03): the exception handler re-executes failed requests here, and the app has one
// anonymous HTML surface — /s/{token}. Under the RequireAuthenticatedUser fallback policy, an anonymous
// share visitor who hit a server error was 302'd into the identity-provider login instead of seeing the
// error page. The page renders only a trace id — nothing to protect.
[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    private readonly ILogger<ErrorModel> _logger;

    public ErrorModel(ILogger<ErrorModel> logger)
    {
        _logger = logger;
    }

    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }

    // The exception handler re-executes failed POSTs as POSTs (antiforgery is opted out for this page) —
    // without this, exactly the errors most worth correlating rendered with no trace id.
    public void OnPost() => OnGet();
}

