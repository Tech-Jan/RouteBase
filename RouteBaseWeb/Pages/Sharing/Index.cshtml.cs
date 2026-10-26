using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages.Sharing;

/// <summary>
/// "Shared right now" — every live share link the viewer owns, across all four kinds.
///
/// A shell with no data of its own: the rows come from GET /api/sharelinks/mine, rendered by
/// pages/sharing-inventory.ts. Client-rendered on purpose, unlike the card pages — expiry is time-based, so
/// a tab left open overnight would otherwise show links that died hours ago, and the module re-fetches
/// after every revoke rather than keeping a second render path in Razor.
/// </summary>
public class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
