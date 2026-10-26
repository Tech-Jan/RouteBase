using RouteBaseWeb.Services;
using RouteBaseWeb.Services.ErrorLog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages.Admin;

/// <summary>
/// /Admin/Errors — read/manage the Error_Log diagnostic store (OBS-04). Server-rendered, zero JS
/// (paging/filtering/actions are all GET/POST forms). Gated per-request by <see cref="IAdminGate"/>
/// (Admin:Users config list, shared with the sidebar nav entry) — 404s for non-admins, no
/// existence leak. Auth itself is already forced by the global FallbackPolicy.
/// </summary>
public class ErrorsModel : PageModel
{
    private const int PageSize = 50;

    private readonly ErrorLogStore _store;
    private readonly IAdminGate _adminGate;

    public ErrorsModel(ErrorLogStore store, IAdminGate adminGate)
    {
        _store = store;
        _adminGate = adminGate;
    }

    [BindProperty(SupportsGet = true)]
    public string Level { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string Source { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public IReadOnlyList<ErrorLogEntry> Rows { get; private set; } = Array.Empty<ErrorLogEntry>();
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; } = 1;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!_adminGate.IsAdminUser()) return NotFound();

        TotalCount = await _store.CountAsync(Level, Source);
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
        P = Math.Clamp(P, 1, TotalPages);
        Rows = await _store.GetPageAsync(Level, Source, P, PageSize);
        return Page();
    }

    public async Task<IActionResult> OnPostPruneAsync()
    {
        if (!_adminGate.IsAdminUser()) return NotFound();
        await _store.PruneAsync(ErrorLogRetention.MaxAge, ErrorLogRetention.MaxRows);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        if (!_adminGate.IsAdminUser()) return NotFound();
        await _store.ClearAllAsync();
        return RedirectToPage();
    }

    /// <summary>Category minus its namespace prefix, e.g. "RouteBaseWeb.Api.RecordingApiController"
    /// → "RecordingApiController". Full string stays available for the title attribute.</summary>
    public static string ShortCategory(string category)
    {
        var i = category.LastIndexOf('.');
        return i >= 0 && i < category.Length - 1 ? category[(i + 1)..] : category;
    }
}
