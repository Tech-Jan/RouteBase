using System.Globalization;
using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

[ApiController]
[Route("api/statistics")]
public class StatisticsApiController : ControllerBase
{
    private readonly IStatisticsService _statistics;
    private readonly IVisibilityService _visibility;
    private readonly IUserService _userService;
    private readonly ICurrentUserService _currentUser;

    public StatisticsApiController(
        IStatisticsService statistics,
        IVisibilityService visibility,
        IUserService userService,
        ICurrentUserService currentUser)
    {
        _statistics  = statistics;
        _visibility  = visibility;
        _userService = userService;
        _currentUser = currentUser;
    }

    /// <summary>
    /// GET /api/statistics/summary — overall totals + per-raw-type breakdown for the filter header
    /// (owner/type/date). Drives the summary KPI box and the by-type boxes.
    /// Wire: <c>?f.owner.in=__me__&amp;f.type.in=cycling&amp;f.date.from=2025-01-01&amp;f.date.to=2025-06-30</c>.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType<StatisticsSummary>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary([FromQuery] StatisticsRequest request)
    {
        var filter = await ResolveFilterAsync(request);
        var summary = await _statistics.GetSummaryAsync(filter);
        return Ok(summary);
    }

    /// <summary>
    /// GET /api/statistics/cumulative — per-day, per-raw-type totals for the cumulative chart (line/multiline/bar).
    /// Same filter header as <see cref="GetSummary"/>.
    /// </summary>
    [HttpGet("cumulative")]
    [ProducesResponseType<List<CumulativeTypePoint>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCumulative([FromQuery] StatisticsRequest request)
    {
        var filter = await ResolveFilterAsync(request);
        var result = await _statistics.GetCumulativeByTypeAsync(filter);
        return Ok(result);
    }

    // Resolves the shared filter header into a StatisticsFilter: owner combo → user ids (via the visible-user
    // name map), raw types from f.type.in, and the local date range from f.date.from/.to. An absent owner filter
    // means no constraint — the client always sends its owner state (default Me lives client-side + in the URL).
    private async Task<StatisticsFilter> ResolveFilterAsync(StatisticsRequest request)
    {
        var filters = request.Filters;

        var currentUserId  = _currentUser.GetUserId();
        var visibleUserIds = await _visibility.GetVisibleUserIdsAsync();
        var users          = await _userService.GetUsersByIdsAsync(visibleUserIds);
        var names          = OwnerLabels.ToNameMap(users);

        var ownerFilter = filters.Items.TryGetValue("owner", out var of) ? of : null;
        var owner = OwnerFilterResolver.Resolve(ownerFilter, currentUserId, names);

        var types = filters.Items.TryGetValue("type", out var tf) && tf.In.Count > 0 ? tf.In : null;

        var visibilities = filters.Items.TryGetValue("visibility", out var vf) && vf.In.Count > 0 ? vf.In : null;

        DateTime? from = null, to = null;
        if (filters.Items.TryGetValue("date", out var df))
        {
            from = ParseDate(df.From);
            to   = ParseDate(df.To);
        }

        return new StatisticsFilter(
            Types: types,
            From: from,
            To: to,
            OwnerUserIds: owner.NoConstraint ? null : owner.UserIds,
            OwnerMatchNone: owner.MatchNone,
            Visibilities: visibilities);
    }

    private static DateTime? ParseDate(string? raw)
        => DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
