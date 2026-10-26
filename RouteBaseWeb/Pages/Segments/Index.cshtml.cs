using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using RouteBaseWeb.Helpers;

namespace RouteBaseWeb.Pages.Segments;

public class IndexModel : PageModel
{
    private readonly ISegmentCrud _segmentCrud;
    private readonly ISegmentEfforts _segmentEfforts;
    private readonly IVisibilityService _visibilityService;
    private readonly IUserService _userService;
    private readonly ICurrentUserService _currentUser;
    private readonly IShareLinkService _shareLinks;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ISegmentCrud segmentCrud,
        ISegmentEfforts segmentEfforts,
        IVisibilityService visibilityService,
        IUserService userService,
        ICurrentUserService currentUser,
        IShareLinkService shareLinks,
        ILogger<IndexModel> logger)
    {
        _segmentCrud = segmentCrud;
        _segmentEfforts = segmentEfforts;
        _visibilityService = visibilityService;
        _userService = userService;
        _currentUser = currentUser;
        _shareLinks = shareLinks;
        _logger = logger;
    }

    /// <summary>Safety cap on cards rendered at once. Minimaps are lazy, so a few thousand shells is cheap.</summary>
    private const int MaxCards = 2000;

    public List<Segment> Segments { get; set; } = new();
    public Dictionary<SegmentId, int> EffortCounts { get; set; } = new();
    /// <summary>Per-segment aggregate effort totals (all efforts, unfiltered) for the card "/ Total" readouts.</summary>
    public Dictionary<SegmentId, SegmentEffortTotals> EffortTotals { get; set; } = new();
    public string CurrentUserId { get; set; } = string.Empty;
    public Dictionary<string, string> UserDisplayNames { get; set; } = new(StringComparer.Ordinal);
    public bool MultiUser { get; set; }

    public int TotalSegments { get; set; }

    /// <summary>Segment ids the VIEWER has EVER shared, mapped to their lifetime open count and whether a
    /// link is live — the cards badge the key, show the value and dim it once sharing ended. Never
    /// populated from a friend's links: whether they published a segment is not ours to advertise.</summary>
    public IReadOnlyDictionary<int, ShareOpens> SharedSegmentOpens { get; set; } = new Dictionary<int, ShareOpens>();

    /// <summary>Bootstrap consumed by overview/segments-grid.ts to drive the sort/filter controls + cards.</summary>
    public SegmentsGridBootstrap GridBootstrap { get; set; } = new();

    public async Task OnGetAsync()
    {
        // Load all visible segments — sorting/filtering happens client-side over the full set.
        var all = await _segmentCrud.GetAllSegmentsAsync();
        Segments = all.Count > MaxCards ? all.Take(MaxCards).ToList() : all;
        TotalSegments = all.Count;

        if (Segments.Count > 0)
        {
            var segmentIds = Segments.Select(s => s.SegmentId).ToList();
            EffortCounts = await _segmentEfforts.GetEffortCountsForSegmentsAsync(segmentIds);
            EffortTotals = await _segmentEfforts.GetEffortTotalsForSegmentsAsync(segmentIds);
        }

        CurrentUserId = _currentUser.GetUserId();

        // One constant-shaped query for the whole card list — no id list, so the SQL does not grow with
        // the segment count (MaxCards is 2000).
        SharedSegmentOpens = await _shareLinks.ShareOpensAsync(
            ShareKindNames.Segment, CurrentUserId, HttpContext.RequestAborted);

        var visibleUserIds = await _visibilityService.GetVisibleUserIdsAsync();
        MultiUser = visibleUserIds.Count > 1;
        if (MultiUser)
        {
            var users = await _userService.GetUsersByIdsAsync(visibleUserIds);
            UserDisplayNames = OwnerLabels.ToNameMap(users);
        }

        GridBootstrap = BuildGridBootstrap();

        _logger.LogDebug("Segments page rendered {Count} of {Total} segments", Segments.Count, TotalSegments);
    }

    /// <summary>The owner label shown on a card / used as the grid's owner filter value ("Me" for the viewer).</summary>
    public string? OwnerLabel(Segment s) => OwnerLabels.Label(s.UserId, CurrentUserId, UserDisplayNames);

    private SegmentsGridBootstrap BuildGridBootstrap()
    {
        var rows = Segments.Select(s => new SegmentsGridRow
        {
            Id = s.SegmentId.Value,
            Name = s.SegmentName,
            Kind = s.SegmentKind,
            ActivityType = s.ActivityType,
            CreatedIso = s.CreatedAt.ToWallClockIso(),
            DistanceMeters = s.SegmentDistance,
            AscentMeters = s.SegmentAscent,
            DescentMeters = s.SegmentDescent,
            EffortCount = EffortCounts.TryGetValue(s.SegmentId, out var ec) ? ec : 0,
            Owner = OwnerLabel(s),
            Visibility = s.Visibility,
            IsOwn = s.UserId != null && s.UserId == CurrentUserId,
            IsShared = SharedSegmentOpens.IsSharedNow(s.SegmentId.Value),
            WasShared = SharedSegmentOpens.WasSharedBefore(s.SegmentId.Value),
        }).ToList();

        return new SegmentsGridBootstrap
        {
            Rows = rows,
            MultiUser = MultiUser,
            TypeOptions = OwnerLabels.DistinctSorted(rows.Select(r => r.ActivityType)),
            OwnerOptions = MultiUser
                ? OwnerLabels.DistinctSortedMeFirst(rows.Select(r => r.Owner))
                : new List<string>(),
            VisibilityOptions = OwnerLabels.DistinctSorted(rows.Select(r => r.Visibility)),
        };
    }
}

public class SegmentsGridBootstrap
{
    public List<SegmentsGridRow> Rows { get; set; } = new();
    public bool MultiUser { get; set; }
    public List<string> TypeOptions { get; set; } = new();
    public List<string> OwnerOptions { get; set; } = new();
    public List<string> VisibilityOptions { get; set; } = new();
}

// Mirrors TS SegmentsGridRow (window-globals.d.ts).
public class SegmentsGridRow
{
    public int Id { get; set; }
    public string? Name { get; set; }
    [WireValues(typeof(SegmentKinds))]
    public string Kind { get; set; } = SegmentKinds.Polyline;
    public string? ActivityType { get; set; }
    /// <summary>Round-trip (ISO 8601) creation timestamp, or null for legacy segments.</summary>
    public string? CreatedIso { get; set; }
    public double? DistanceMeters { get; set; }
    public double? AscentMeters { get; set; }
    public double? DescentMeters { get; set; }
    public int EffortCount { get; set; }
    public string? Owner { get; set; }
    /// <summary>private | friends | public. Every pre-2026-07 segment reads "friends": the value was
    /// never written before segment visibility landed, so it sat at the entity's CLR default.</summary>
    [WireValues(typeof(Visibilities))]
    public string? Visibility { get; set; }
    /// <summary>Whether the viewer owns this row — only an owner may change its visibility inline.</summary>
    public bool IsOwn { get; set; }

    /// <summary>Whether the VIEWER has an active share link for this segment — never the row owner's, so a
    /// friend's segment always reads false. Drives the grid's Shared filter, matching the card badge.</summary>
    public bool IsShared { get; set; }

    /// <summary>The viewer shared this segment at some point and no link is live now — the grid's
    /// "Previously shared" state. Mutually exclusive with <see cref="IsShared"/>.</summary>
    public bool WasShared { get; set; }
}
