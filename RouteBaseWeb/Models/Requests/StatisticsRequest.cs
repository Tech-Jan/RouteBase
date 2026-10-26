using RouteBaseWeb.Models.GridFilter;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Models.Requests;

/// <summary>
/// Query parameters for the Statistics-page data endpoints (<c>GET /api/statistics/summary</c> and
/// <c>/cumulative</c>). The whole filter header — owner combo, raw activity types, date range — arrives in the
/// shared <c>f.&lt;key&gt;.&lt;suffix&gt;</c> grid-filter format (<c>f.owner.in</c>, <c>f.type.in</c>,
/// <c>f.date.from</c>/<c>.to</c>) and is bound into <see cref="Filters"/>. The controller resolves the owner
/// combo to user ids and pulls types/dates out into a <see cref="StatisticsFilter"/>.
/// </summary>
public class StatisticsRequest
{
    [ModelBinder(typeof(GridFilterModelBinder))]
    public GridFilterCollection Filters { get; set; } = new();
}
