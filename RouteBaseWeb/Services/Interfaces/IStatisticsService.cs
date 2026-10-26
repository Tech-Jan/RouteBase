using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Owns the Statistics-page queries. Each method takes a fully-resolved <see cref="StatisticsFilter"/>
/// (owner ids, raw types, date range) and applies the same visibility-scoped base query.
/// </summary>
public interface IStatisticsService
{
    /// <summary>Overall totals + per-raw-type breakdown for the filtered activity set (one grouped query).</summary>
    Task<StatisticsSummary> GetSummaryAsync(StatisticsFilter filter);

    /// <summary>
    /// Per-day, per-raw-type activity totals (daily increments) for the filtered set, ordered by date.
    /// One fetch serves both the aggregate line and the per-type multiline chart modes.
    /// </summary>
    Task<List<CumulativeTypePoint>> GetCumulativeByTypeAsync(StatisticsFilter filter);
}
