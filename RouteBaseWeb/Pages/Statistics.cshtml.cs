using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages;

/// <summary>
/// Statistics page: ships only the bootstrap the client widgets need (activity types, owner options,
/// multi-user flag, has-any-activities). All stats are fetched client-side via the filter header
/// (see statistics/*.ts) — this model runs no aggregate queries.
/// </summary>
public class StatisticsModel : PageModel
{
    private readonly IActivityQueryService _activityService;
    private readonly IVisibilityService _visibility;
    private readonly IUserService _userService;
    private readonly ICurrentUserService _currentUser;

    public StatisticsModel(
        IActivityQueryService activityService,
        IVisibilityService visibility,
        IUserService userService,
        ICurrentUserService currentUser)
    {
        _activityService = activityService;
        _visibility      = visibility;
        _userService     = userService;
        _currentUser     = currentUser;
    }

    public List<string> ActivityTypes { get; set; } = new();
    public bool MultiUser { get; set; }
    public IReadOnlyList<OwnerOption> OwnerOptions { get; set; } = Array.Empty<OwnerOption>();
    public bool HasAnyActivities { get; set; }

    public sealed record OwnerOption(string Value, string Label);

    public async Task OnGetAsync()
    {
        ActivityTypes = await _activityService.GetActivityTypesAsync();

        var currentUserId  = _currentUser.GetUserId();
        var visibleUserIds = await _visibility.GetVisibleUserIdsAsync();
        MultiUser = visibleUserIds.Count > 1;

        // Per-user owner options (current excluded) — the Me/Others/Everyone tokens are added client-side.
        var users = await _userService.GetUsersByIdsAsync(visibleUserIds);
        OwnerOptions = users
            .Where(u => u.UserId != currentUserId)
            .Select(u => new OwnerOption(u.UserId, OwnerLabels.DisplayName(u)))
            .OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        HasAnyActivities = await _activityService.GetVisibleActivityCountAsync() > 0;
    }
}

/// <summary>
/// The #statisticsBootstrap island, named (F1) so IslandSchemasDocumentTransformer carries it into
/// the OpenAPI doc and the TS type is generated instead of hand-mirrored. Wire shape unchanged:
/// BootstrapJson camelCases to multiUser/typeOptions/ownerOptions/hasAnyActivities.
/// </summary>
public sealed record StatisticsBootstrap(
    bool MultiUser,
    IReadOnlyList<string> TypeOptions,
    IReadOnlyList<StatisticsModel.OwnerOption> OwnerOptions,
    bool HasAnyActivities);
