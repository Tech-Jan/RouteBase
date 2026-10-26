using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Mvc.RazorPages;

using RouteBaseWeb.Helpers;

namespace RouteBaseWeb.Pages;

public class IndexModel : PageModel
{
    private readonly IActivityQueryService _activityService;
    private readonly IVisibilityService _visibilityService;
    private readonly IUserService _userService;
    private readonly ICurrentUserService _currentUser;
    private readonly IShareLinkService _shareLinks;
    private readonly ILogger<IndexModel> _logger;

    private readonly TimeProvider _clock;

    public IndexModel(
        IActivityQueryService activityService,
        IVisibilityService visibilityService,
        IUserService userService,
        ICurrentUserService currentUser,
        IShareLinkService shareLinks,
        ILogger<IndexModel> logger,
        TimeProvider clock)
    {
        _clock = clock;
        _activityService = activityService;
        _visibilityService = visibilityService;
        _userService = userService;
        _currentUser = currentUser;
        _shareLinks = shareLinks;
        _logger = logger;
    }

    /// <summary>All visible activities, newest first — sorting/filtering happens client-side
    /// via the reusable controls grid (dashboard/dashboard-grid.ts), so there is no pagination. The same
    /// filtered set feeds the KPI / volume / calendar widgets, so no stats endpoint is involved.</summary>
    public List<Activity> RecentActivities { get; set; } = new();
    public string Greeting { get; set; } = "Good morning";
    public string FormattedDate { get; set; } = "";
    public string CurrentUserId { get; set; } = string.Empty;
    public Dictionary<string, string> UserDisplayNames { get; set; } = new(StringComparer.Ordinal);
    public int TotalActivities { get; set; }

    /// <summary>Photo count per track id — cards without photos skip the photo-strip markup entirely.</summary>
    public Dictionary<TrackId, int> PhotoCounts { get; set; } = new();

    /// <summary>Track ids the VIEWER has EVER shared, mapped to their lifetime open count and whether a
    /// link is live — the cards badge the key, show the value and dim it once sharing ended. Never
    /// populated from a friend's links: whether they published a ride is not ours to advertise.</summary>
    public IReadOnlyDictionary<int, ShareOpens> SharedTrackOpens { get; set; } = new Dictionary<int, ShareOpens>();

    /// <summary>Bootstrap consumed by dashboard/dashboard.ts to drive the sort/filter controls, the cards,
    /// and the KPI / weekly-volume / calendar widgets.</summary>
    public DashboardGridBootstrap GridBootstrap { get; set; } = new();

    public async Task OnGetAsync()
    {
        // Disable caching for this page
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        CurrentUserId = _currentUser.GetUserId();

        // Greeting + date: first-paint fallback only, computed from the server's clock (UTC in the
        // Docker deployment) — dashboard.ts immediately overwrites both from the browser's local clock.
        var now = _clock.GetUtcNow().UtcDateTime;
        Greeting = now.Hour < 12 ? "Good morning" : now.Hour < 17 ? "Good afternoon" : "Good evening";
        FormattedDate = now.ToString("dddd · MMMM d", System.Globalization.CultureInfo.CurrentCulture);

        // Full visible set, newest first — the controls grid filters/sorts it client-side, and the KPI /
        // weekly-volume / calendar widgets aggregate that filtered set (dashboard/dashboard-stats.ts).
        // Sequential on purpose: all queries share the request-scoped DbContext (EF Core forbids concurrent ops).
        RecentActivities = await _activityService.GetAllActivitiesAsync();
        TotalActivities = RecentActivities.Count;

        // One GROUP BY for all cards: the photo strip is only emitted (and only fetches) for
        // activities that actually have photos — otherwise every card scrolled into view would
        // fire an empty /api/photos round-trip.
        PhotoCounts = await _activityService.GetPhotoCountsAsync(
            RecentActivities.Select(a => a.TrackId).ToList());

        // One constant-shaped query for the whole feed — it does NOT take the page's track ids, because
        // this feed is unpaginated and an IN list would scale the SQL with the activity count.
        SharedTrackOpens = await _shareLinks.ShareOpensAsync(
            ShareKindNames.Activity, CurrentUserId, HttpContext.RequestAborted);

        // Display names for visible users (owner labels; only meaningful when friends exist)
        var visibleUserIds = await _visibilityService.GetVisibleUserIdsAsync();
        if (visibleUserIds.Count > 1)
        {
            var users = await _userService.GetUsersByIdsAsync(visibleUserIds);
            UserDisplayNames = OwnerLabels.ToNameMap(users);
        }

        GridBootstrap = BuildGridBootstrap();

        _logger.LogDebug("Dashboard rendered {Count} activities", TotalActivities);
    }

    /// <summary>The owner label used as the grid's owner filter value ("Me" for the viewer).</summary>
    public string? OwnerLabel(Activity a) => OwnerLabels.Label(a.UserId, CurrentUserId, UserDisplayNames);

    private DashboardGridBootstrap BuildGridBootstrap()
    {
        var multiUser = UserDisplayNames.Count > 1;
        var rows = RecentActivities.Select(a => new DashboardGridRow
        {
            Id = a.TrackId.Value,
            Name = a.TrackName,
            ActivityType = a.ActivityType,
            DateIso = a.StartDateTimeLocal.ToWallClockIso(),
            DistanceMeters = a.TotalDistance,
            AscentMeters = a.TotalAscent,
            TimeTotalSeconds = a.TimeTotal,
            TimeMovingSeconds = a.TimeMoving,
            TypeGroup = Helpers.ActivityTypeHelper.GetTypeGroup(a.ActivityType),
            SpeedAvgMs = a.SpeedAvg,
            HeartRateAvg = a.HeartRateAvg,
            Visibility = a.Visibility,
            Owner = OwnerLabel(a),
            IsShared = SharedTrackOpens.IsSharedNow(a.TrackId.Value),
            WasShared = SharedTrackOpens.WasSharedBefore(a.TrackId.Value),
        }).ToList();

        return new DashboardGridBootstrap
        {
            Rows = rows,
            MultiUser = multiUser,
            TypeOptions = OwnerLabels.DistinctSorted(rows.Select(r => r.ActivityType)),
            OwnerOptions = multiUser
                ? OwnerLabels.DistinctSortedMeFirst(rows.Select(r => r.Owner))
                : new List<string>(),
        };
    }
}

public class DashboardGridBootstrap
{
    public List<DashboardGridRow> Rows { get; set; } = new();
    public bool MultiUser { get; set; }
    public List<string> TypeOptions { get; set; } = new();
    public List<string> OwnerOptions { get; set; } = new();
}

// Mirrors TS DashboardGridRow (window-globals.d.ts).
public class DashboardGridRow
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? ActivityType { get; set; }
    /// <summary>Round-trip (ISO 8601) local start timestamp, or null.</summary>
    public string? DateIso { get; set; }
    public double? DistanceMeters { get; set; }
    public double? AscentMeters { get; set; }
    public double? TimeTotalSeconds { get; set; }

    /// <summary>Moving time — what the KPI strip and the volume bars total (the grid's Time column is
    /// <see cref="TimeTotalSeconds"/>; the two differ whenever the ride had stops).</summary>
    public double? TimeMovingSeconds { get; set; }

    /// <summary>Canonical "bike" | "run" | "walk" | "other" group of <see cref="ActivityType"/>, mapped
    /// server-side by <see cref="Helpers.ActivityTypeHelper.GetTypeGroup"/> so the keyword rules have no
    /// TypeScript twin. Drives the volume stacks + calendar day icons.</summary>
    public string TypeGroup { get; set; } = "other";

    public double? SpeedAvgMs { get; set; }
    public double? HeartRateAvg { get; set; }
    [WireValues(typeof(Visibilities))]
    public string Visibility { get; set; } = Visibilities.Default;
    public string? Owner { get; set; }

    /// <summary>Whether the VIEWER has an active share link for this ride — never the row owner's, so a
    /// friend's activity always reads false. Drives the grid's Shared filter, matching the card badge.</summary>
    public bool IsShared { get; set; }

    /// <summary>The viewer shared this ride at some point and no link is live now — the grid's "Previously
    /// shared" state. Mutually exclusive with <see cref="IsShared"/>; both false means never shared.</summary>
    public bool WasShared { get; set; }
}
