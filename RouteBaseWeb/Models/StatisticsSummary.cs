namespace RouteBaseWeb.Models;

/// <summary>
/// Statistics-page summary: overall totals plus a per-raw-type breakdown. Both use
/// <see cref="ActivityStatistics"/> so the summary KPI box and the by-type boxes share one shape.
/// </summary>
public class StatisticsSummary
{
    /// <summary>Totals across all matching activities (sum of <see cref="ByType"/>).</summary>
    public ActivityStatistics Overall { get; set; } = new();

    /// <summary>One entry per raw activity type present in the filtered set, ordered by type name.</summary>
    public List<TypeStatistics> ByType { get; set; } = new();
}

/// <summary>Aggregated statistics for a single raw activity type. Null types are bucketed as "Unknown".</summary>
public class TypeStatistics
{
    public string Type { get; set; } = "";
    public ActivityStatistics Stats { get; set; } = new();
}
