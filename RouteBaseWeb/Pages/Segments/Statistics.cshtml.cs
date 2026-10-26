using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages.Segments;

/// <summary>
/// The canonical segment statistics page. The loading sequence now lives in
/// <see cref="ISegmentDetailViewBuilder"/> so this page and the anonymous share view produce identical
/// data from one code path.
///
/// Every property below is a pass-through onto <see cref="View"/>, keeping the names the markup already
/// binds to — that is what let the extraction land without editing a single Razor expression.
/// </summary>
public class StatisticsModel(
    ISegmentDetailViewBuilder viewBuilder,
    AuthenticatedSegmentViewSource viewSource,
    ICurrentUserService currentUser) : PageModel
{
    public SegmentDetailView? View { get; private set; }

    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    public Segment? Segment => View?.Segment;
    public bool IsOwner => View?.IsOwner ?? false;
    public SegmentViewSurfaces Surfaces => View?.Surfaces ?? SegmentViewSurfaces.ReadOnlyShare;
    public List<SegmentEffortDetailed> DetailedEfforts => View?.DetailedEfforts ?? [];
    public SegmentEffortTotals? EffortTotals => View?.EffortTotals;
    public IReadOnlyList<ShapeRectDto> ShapeRects => View?.ShapeRects ?? [];
    public Heatmap? Heatmap => View?.Heatmap;
    public string MapTilerApiKey => View?.MapTilerApiKey ?? string.Empty;
    public int DefaultMatchThreshold => View?.DefaultMatchThreshold ?? SegmentAnalysis.DefaultMatchThresholdPercent;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Id.HasValue)
            return RedirectToPage("/Segments/Index");

        View = await viewBuilder.BuildAsync(new SegmentId(Id.Value), viewSource, new SegmentViewOptions(
            IncludeMapTilerKey: true,
            IncludeHeatmap: true,
            SettingsUserId: currentUser.GetUserId(),
            Surfaces: SegmentViewSurfaces.ForStatisticsPage), HttpContext.RequestAborted);

        if (View is null)
            return NotFound();

        return Page();
    }
}
