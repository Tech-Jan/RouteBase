using RouteBaseWeb.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages;

public class ImportModel : PageModel
{
    /// <summary>When set (from Activity Details "Add photos"), photos import straight to this activity.</summary>
    public int? TrackId { get; private set; }

    public void OnGet(int? trackId) => TrackId = trackId;
}
