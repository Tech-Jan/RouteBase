using System.Globalization;
using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages;

public class ActivitiesModel : PageModel
{
    private readonly IActivityQueryService _activityService;
    private readonly IMapTilerService _mapTilerService;
    private readonly IVisibilityService _visibilityService;
    private readonly IUserService _userService;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<ActivitiesModel> _logger;

    public ActivitiesModel(
        IActivityQueryService activityService,
        IMapTilerService mapTilerService,
        IVisibilityService visibilityService,
        IUserService userService,
        ICurrentUserService currentUser,
        ILogger<ActivitiesModel> logger)
    {
        _activityService = activityService;
        _mapTilerService = mapTilerService;
        _visibilityService = visibilityService;
        _userService = userService;
        _currentUser = currentUser;
        _logger = logger;
    }

    public List<string> ActivityTypes { get; set; } = new();
    public string? MapTilerApiKey { get; set; }

    // Multi-user support
    public string CurrentUserId { get; set; } = string.Empty;
    public List<AppUser> VisibleUsers { get; set; } = new();
    public Dictionary<string, string> UserDisplayNames { get; set; } = new(StringComparer.Ordinal);

    public int TotalCount { get; set; }
    public int TotalPages { get; set; }

    /// <summary>Count of all visible activities (unfiltered) — the grand-total denominator for the grid count.</summary>
    public int UnfilteredTotal { get; set; }

    /// <summary>All TrackIds matching the current filters (across all pages).</summary>
    public List<int> AllFilteredIds { get; set; } = new();

    /// <summary>
    /// TrackIds matching the owner filter only (no date/type/column predicates) — the map's display
    /// universe: the overview map draws this set and dims it down to <see cref="AllFilteredIds"/>.
    /// Equals <see cref="AllFilteredIds"/> when no column filter is active.
    /// </summary>
    public List<int> OwnerScopedIds { get; set; } = new();

    /// <summary>How many of <see cref="AllFilteredIds"/> the current user owns — the "Export GPX (N)" count.</summary>
    public int OwnedFilteredCount { get; set; }

    /// <summary>
    /// Owner combo keyword options (Me/Others/Everyone) for the grid bootstrap. Always populated so the
    /// Owner column is shown even for users with no friends — public activities from non-friends are
    /// visible, so the column (defaulting to "Me") is what lets them be filtered out / opted into.
    /// </summary>
    public IReadOnlyList<OwnerOption> OwnerOptions { get; } = new[]
    {
        new OwnerOption("__me__", "Me"),
        new OwnerOption("__others__", "Others"),
        new OwnerOption("__everyone__", "Everyone"),
    };

    public sealed record OwnerOption(string Value, string Label);

    // --- Filter bind properties ---
    // Multi-select activity-type filter. Empty = "All Types". Each checked type arrives as a repeated
    // "SelectedActivityTypes" query-string value.
    [BindProperty(SupportsGet = true)]
    public List<string> SelectedActivityTypes { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public DateTime? StartDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? EndDate { get; set; }

    // Query-string projections of the date filters. QueryStringValueProvider parses bound dates with
    // InvariantCulture, so anything we echo into a URL or hidden input must be written the same way —
    // a view-side CurrentCulture ToString would round-trip 543 years off under th-TH.
    public string? StartDateParam => StartDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    public string? EndDateParam => EndDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // --- Sort bind properties ---
    [BindProperty(SupportsGet = true)]
    public string SortColumn { get; set; } = "date";

    [BindProperty(SupportsGet = true)]
    public string SortDirection { get; set; } = SortDirections.Desc;

    // --- Pagination bind properties ---
    [BindProperty(SupportsGet = true, Name = "Page")]
    public int PageNumber { get; set; } = 1;

    // Default to a value at the clamp ceiling so the "All" option is selected by default.
    [BindProperty(SupportsGet = true)]
    public int PageSize { get; set; } = 10000;

    // --- Optional columns (comma-separated, stored in cookie) ---
    [BindProperty(SupportsGet = true)]
    public string? ExtraColumns { get; set; }

    // --- Owner filter ---
    [BindProperty(SupportsGet = true)]
    public string? OwnerUserId { get; set; }

    // --- Per-column filter-row values (client grid filter row) ---
    // Type and date filters reuse SelectedActivityTypes / StartDate / EndDate above so the filter row
    // and the top panel manipulate the same params. The genuinely new per-column filters (name, distance,
    // ascent, …) arrive in the generic f.<key>.<suffix> format (plus legacy aliases) and are bound here so
    // hard-reload URLs are server-filtered with no flash.
    [BindProperty(SupportsGet = true)]
    [ModelBinder(typeof(GridFilterModelBinder))]
    public GridFilterCollection GridFilters { get; set; } = new();

    /// <summary>First page rows for the client-rendered grid bootstrap.</summary>
    public List<ActivityRowDto> Rows { get; set; } = new();

    public async Task OnGetAsync()
    {
        // No try/catch wrapper (GL-05): a load failure must surface via the framework error page,
        // not silently render an empty "you have no activities" grid. Cookie restore + clamps below
        // can't throw for normal input.

        // Restore extra columns from cookie if not provided in query string
        if (string.IsNullOrEmpty(ExtraColumns) && Request.Cookies.TryGetValue("activityExtraColumns", out var cookieVal))
            ExtraColumns = cookieVal;

        // Clamp page size
        PageSize = PageSize is >= 10 and <= 10000 ? PageSize : 10000;
        PageNumber = Math.Max(1, PageNumber);

        CurrentUserId = _currentUser.GetUserId();

        // Resolve display names for every visible user (self + friends). Used for the Owner column
        // labels and the Owner filter's name search. The Owner column is always shown (see OwnerOptions),
        // so this runs even for a single, friend-less user.
        var visibleUserIds = await _visibilityService.GetVisibleUserIdsAsync();
        VisibleUsers = await _userService.GetUsersByIdsAsync(visibleUserIds);
        UserDisplayNames = OwnerLabels.ToNameMap(VisibleUsers);

        ActivityTypes = await _activityService.GetActivityTypesAsync();
        // The key feeds _MapTilerKeyBootstrap only. It no longer gates a settings read: the layouts
        // deliver #appSettings on every page, so a missing MapTiler credential cannot suppress the
        // basemap preference here the way it used to.
        MapTilerApiKey = await _mapTilerService.GetApiKeyAsync();

        // Owner column lives in identity space — resolve its combo (Me/Others/Everyone + name search)
        // to a UserId set so the server first-render matches the client grid's owner filter. The grid's
        // "home" state is always "Me": when no owner filter is supplied (a fresh load), default to the
        // current user so the server-rendered first page matches the client default (no flash of
        // everyone-then-me) and non-friends' visible public activities stay hidden until opted into. An
        // explicit Everyone selection arrives as f.owner.in=__everyone__, so it hydrates into a filter
        // here and is honoured rather than overridden.
        ColumnFilter? ownerFilter = GridFilters.Items.TryGetValue("owner", out var of) ? of : null;
        if (ownerFilter is null || ownerFilter.IsEmpty)
            ownerFilter = new ColumnFilter { In = { OwnerFilterResolver.Me } };

        var owner = OwnerFilterResolver.Resolve(ownerFilter, CurrentUserId, UserDisplayNames);

        // All matching IDs for the "all activities" map scope (SELECT track_ID only — no rows or GPS loaded).
        // Materialized first so the paged query reuses its .Count as the total match count instead of
        // re-running a byte-identical COUNT — one aggregate per load, not two (perf-5).
        AllFilteredIds = (await _activityService.GetFilteredActivityIdsAsync(
            activityType: null, StartDate, EndDate, OwnerUserId,
            activityTypes: SelectedActivityTypes, filters: GridFilters,
            ownerUserIds: owner.NoConstraint ? null : owner.UserIds,
            ownerMatchNone: owner.MatchNone)).Select(t => t.Value).ToList();

        var (items, total) = await _activityService.GetPagedActivitiesAsync(
            activityType: null,
            StartDate,
            EndDate,
            SortColumn,
            SortDirection,
            PageNumber,
            PageSize,
            OwnerUserId,
            activityTypes: SelectedActivityTypes,
            filters: GridFilters,
            ownerUserIds: owner.NoConstraint ? null : owner.UserIds,
            ownerMatchNone: owner.MatchNone,
            knownCount: AllFilteredIds.Count);

        TotalCount = total;
        TotalPages = (int)Math.Ceiling(total / (double)PageSize);
        UnfilteredTotal = await _activityService.GetVisibleActivityCountAsync();

        // Owner-scoped universe for the map (mirrors the grid API's ownerIds; see OwnerScopedIds docs).
        // On a fresh load (no URL column filters) it equals AllFilteredIds — skip the extra query.
        var hasColumnFilters = StartDate.HasValue || EndDate.HasValue
            || SelectedActivityTypes.Count > 0
            || GridFilters.Items.Any(kv => kv.Key != "owner" && !kv.Value.IsEmpty);
        OwnerScopedIds = hasColumnFilters
            ? (await _activityService.GetFilteredActivityIdsAsync(
                activityType: null, startDate: null, endDate: null, OwnerUserId,
                activityTypes: null, filters: null,
                ownerUserIds: owner.NoConstraint ? null : owner.UserIds,
                ownerMatchNone: owner.MatchNone)).Select(t => t.Value).ToList()
            : AllFilteredIds;

        // Owned subset of the filtered set — the "Export GPX (N)" count (see ActivityGridResponse).
        // Mirrors the grid API exactly, including its free path when the owner filter is already just us.
        OwnedFilteredCount = owner.UserIds is [var onlyOwner] && string.Equals(onlyOwner, CurrentUserId, StringComparison.Ordinal)
            ? AllFilteredIds.Count
            : owner.MatchNone
                ? 0
                : (await _activityService.GetFilteredActivityIdsAsync(
                    activityType: null, StartDate, EndDate, ownerUserId: null,
                    activityTypes: SelectedActivityTypes, filters: GridFilters,
                    ownerUserIds: [CurrentUserId])).Count;

        // Enrich the display-name map with any owners present on this page that aren't friends
        // (e.g. non-friends whose public activities are visible) so the Owner column shows real names
        // instead of blanks once "Everyone" is selected.
        await EnrichOwnerNamesAsync(items);

        // Project the first page to row DTOs for the client grid bootstrap. Photo counts batched (one query).
        var photoCounts = await _activityService.GetPhotoCountsAsync(items.Select(a => a.TrackId).ToList());
        Rows = items.Select(a => ActivityRowDto.From(
            a, CurrentUserId, ResolveOwner(a) ?? "", photoCounts.GetValueOrDefault(a.TrackId))).ToList();

        _logger.LogInformation("Loaded {Count}/{Total} activities (page {Page})", items.Count, total, PageNumber);
    }

    // Helper: builds a query-string URL that keeps all current params but overrides specific ones
    public string BuildUrl(object overrides)
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["StartDate"]            = StartDateParam,
            ["EndDate"]              = EndDateParam,
            ["SortColumn"]           = SortColumn,
            ["SortDirection"]        = SortDirection,
            ["Page"]                 = PageNumber.ToString(CultureInfo.InvariantCulture),
            ["PageSize"]             = PageSize.ToString(CultureInfo.InvariantCulture),
            ["OwnerUserId"]          = OwnerUserId,
        };

        // Apply overrides using reflection
        foreach (var prop in overrides.GetType().GetProperties())
        {
            var key   = prop.Name;
            var value = prop.GetValue(overrides)?.ToString();
            dict[key] = value;
        }

        var parts = dict
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}")
            .ToList();

        // The multi-select type filter is a repeated query-string key, so it can't live in the scalar dict.
        parts.AddRange(SelectedActivityTypes
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => $"SelectedActivityTypes={Uri.EscapeDataString(t)}"));

        // Carry per-column filter values (f.*) and the ExtraColumns selection verbatim so paging/jumping
        // doesn't silently drop them (GL-06). Skip any key already emitted from the scalar dict above.
        parts.AddRange(Request.Query
            .Where(kv => (kv.Key.StartsWith("f.", StringComparison.Ordinal) || kv.Key == "ExtraColumns") && !dict.ContainsKey(kv.Key))
            .SelectMany(kv => kv.Value.Select(v => $"{kv.Key}={Uri.EscapeDataString(v ?? "")}")));

        return $"?{string.Join("&", parts)}";
    }

    // Resolves the per-row owner label for the grid bootstrap. Returns null for unknown/legacy owners;
    // the call site coalesces to "" to keep ActivityRowDto's non-null owner contract (GL-07).
    private string? ResolveOwner(Activity a) => OwnerLabels.Label(a.UserId, CurrentUserId, UserDisplayNames);

    // Looks up display names for any owners on the loaded page that aren't already known (non-friends whose
    // public activities are visible) and merges them into UserDisplayNames for label resolution.
    private async Task EnrichOwnerNamesAsync(IEnumerable<Activity> items)
    {
        var unknownIds = items
            .Select(a => a.UserId)
            .OfType<string>()
            .Where(id => id.Length > 0 && id != CurrentUserId && !UserDisplayNames.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknownIds.Count == 0) return;

        var extra = await _userService.GetUsersByIdsAsync(unknownIds);
        foreach (var u in extra)
            UserDisplayNames[u.UserId] = OwnerLabels.DisplayName(u);
    }
}

// Mirrors TS ActivitiesGridBootstrap (overview/overview-types.ts, over the generated contract).
// Serialized camelCase into the #activitiesGridBootstrap island on Activities.cshtml, and read by BOTH
// overview/activities-grid.ts (rows, filters, sort) and overview/activities-page-init.ts (the map's id
// universe) — it is the page's only server→client channel now that the window shim is gone.
public class ActivitiesGridBootstrap
{
    // Same five-field shape the API's POST /api/activities/grid serves — one definition
    // (Models/ActivityContracts.cs), so the page bootstrap and the API can never drift apart.
    public ActivityGridResponse InitialData { get; set; } =
        new(Rows: [], FilteredIds: [], OwnerIds: [], Total: 0, UnfilteredTotal: 0, OwnedFilteredCount: 0);
    // Owner column always shown ("Me" default) so non-friends' visible public activities can be filtered out.
    public bool MultiUser { get; set; }
    public List<string> TypeOptions { get; set; } = new();
    public IReadOnlyList<ActivitiesModel.OwnerOption> OwnerOptions { get; set; } = new List<ActivitiesModel.OwnerOption>();
    public string[] ExtraColumns { get; set; } = [];
    public GridSort Sort { get; set; } = new();
    public int PageSize { get; set; }
}

public class GridSort
{
    public string Column { get; set; } = "date";
    public string Direction { get; set; } = SortDirections.Desc;
}
