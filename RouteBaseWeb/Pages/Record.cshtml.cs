using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages;

[Authorize]
public class RecordModel : PageModel
{
    private readonly IRecordingService _recordingService;
    private readonly IVisibilityService _visibilityService;
    private readonly ILiveSessionDirectory _liveSessions;

    private readonly IAutoReviewService _autoReview;

    public RecordModel(
        IRecordingService recordingService,
        IVisibilityService visibilityService,
        ILiveSessionDirectory liveSessions,
        IAutoReviewService autoReview)
    {
        _recordingService = recordingService;
        _visibilityService = visibilityService;
        _liveSessions = liveSessions;
        _autoReview = autoReview;
    }

    public RecordingSession? ActiveSession { get; set; }

    /// <summary>Everyone streaming right now (you + friends) — pick any of them onto one LiveView map.</summary>
    public List<LiveSessionInfo> LiveSessions { get; set; } = [];
    public List<RecentSessionView> RecentSessions { get; set; } = [];

    /// <summary>Auto rides awaiting a decision (pending + rejected-to-trash) — rendered as cards, not table
    /// rows, so the minimap survives on a phone. The sessions table below them carries everything else.</summary>
    public List<Activity> ReviewActivities { get; set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        // Sequential on purpose: these services share the request-scoped DbContext (EF Core forbids
        // concurrent ops).
        var userId = _visibilityService.GetCurrentUserId();
        ActiveSession  = await _recordingService.GetActiveSessionAsync(userId);
        RecentSessions = await _recordingService.GetRecentSessionsWithReviewAsync(userId);
        LiveSessions   = await _liveSessions.GetVisibleAsync();
        ReviewActivities = await _autoReview.GetReviewQueueAsync();

        return Page();
    }
}
