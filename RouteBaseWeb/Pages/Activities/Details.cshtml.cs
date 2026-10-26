using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Activities;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages.Activities;

/// <summary>
/// The canonical activity detail page.
///
/// All loading lives in <see cref="IActivityDetailViewBuilder"/> so the share view renders from the same
/// code path rather than a second copy that would drift. The properties below are pass-throughs onto
/// <see cref="View"/>, kept so the existing markup binds unchanged.
/// </summary>
public class DetailsModel(
    IActivityDetailViewBuilder viewBuilder,
    ICurrentUserService currentUser) : PageModel
{
    /// <summary>The built view. Null only before OnGetAsync, or when the activity was not found.</summary>
    public ActivityDetailView? View { get; private set; }

    public Activity? Activity => View?.Activity;
    public List<GpsData> GpsData => View?.GpsData ?? [];
    public List<Split> Splits => View?.Splits ?? [];
    public bool IsOwner => View?.IsOwner ?? false;
    public ActivityViewSurfaces Surfaces => View?.Surfaces ?? ActivityViewSurfaces.ReadOnlyShare;

    /// <summary>The open edit session this ride is CHECKED OUT into, if any — rendered as the data-resume-*
    /// attributes edit-mode-entry.ts reads. Null means the ride is free: the page offers no resume and the
    /// client makes no request at all.</summary>
    public EditResumeHint? ResumeHint => View?.ResumeHint;

    /// <summary>What the ride's revision list amounts to, without reading it — one indexed seek. Server-rendered
    /// so the history panel can skip its page-load fetch on a never-edited ride, which is most of them.</summary>
    public TrackHistoryFacts HistoryFacts => View?.HistoryFacts ?? new TrackHistoryFacts(0, false);

    public string MapTilerApiKey => View?.MapTilerApiKey ?? string.Empty;

    /// <summary>
    /// True if the activity has usable heart rate data — either the summary avg column is set, or at
    /// least one GPS point has a non-zero HR value. Used to guard the HR chart card.
    /// </summary>
    public bool HasHeartRateData => View?.HasHeartRateData ?? false;

    // OnGetAsync returns NotFound() when the build yields null, so the page never renders without
    // a View — the throw names that contract instead of forgiving it.
    private ActivityDetailView RenderedView =>
        View ?? throw new InvalidOperationException("OnGetAsync 404s before the page renders without a View");

    public object GetGpsTrackForJavaScript() => RenderedView.GetGpsTrackForJavaScript();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        View = await viewBuilder.BuildAsync(id, new ActivityViewOptions(
            IncludeOwnerData: true,
            IncludeMapTilerKey: true,
            SettingsUserId: currentUser.GetUserId(),
            Surfaces: ActivityViewSurfaces.ForDetailsPage,
            // The one route that may open the owner's own awaiting-review / trashed ride (review mode).
            AllowOwnerNonReleased: true));

        return View is null ? NotFound() : Page();
    }
}
