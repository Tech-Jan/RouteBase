using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <summary>
/// Backs the Statistics page. Owns its queries (the cumulative progression used to live on the since-deleted
/// DashboardService, whose only caller was this page). Every query runs through the same
/// visibility-scoped <see cref="BuildQuery"/> so owner/type/date filtering is applied consistently.
/// </summary>
public class StatisticsService : IStatisticsService
{
    private readonly RouteBaseDbContext _db;
    private readonly IVisibilityService _visibility;

    // Null activity types are surfaced under this label in the summary breakdown.
    private const string UnknownType = "Unknown";

    public StatisticsService(RouteBaseDbContext db, IVisibilityService visibility)
    {
        _db         = db;
        _visibility = visibility;
    }

    // Shared base query: visible activities → owner → exact raw type → local-date range.
    // Owner match-none short-circuits to an empty set so summaries/cumulative come back zeroed/empty.
    private IQueryable<Activity> BuildQuery(StatisticsFilter filter)
    {
        var query = _visibility.VisibleActivities(_db.Activities);
        if (filter.OwnerMatchNone)
            return query.Where(_ => false);
        if (filter.OwnerUserIds is not null)
            query = query.WhereInSet(a => a.UserId, filter.OwnerUserIds);
        query = query.WhereInSet(a => a.ActivityType, filter.Types);
        query = query.WhereInSet(a => a.Visibility, filter.Visibilities);
        // End day inclusive: advance the upper bound one day (matches the grid's date convention).
        return query.WhereDateInRange(a => a.StartDateTimeLocal, filter.From, filter.To?.AddDays(1));
    }

    public async Task<StatisticsSummary> GetSummaryAsync(StatisticsFilter filter)
    {
        // One grouped query over the filtered set → per-raw-type aggregates.
        var groups = await BuildQuery(filter)
            .GroupBy(a => a.ActivityType)
            .Select(g => new TypeAggregate
            {
                Type       = g.Key,
                Count      = g.Count(),
                Distance   = g.Sum(a => a.TotalDistance ?? 0),
                Ascent     = g.Sum(a => a.TotalAscent ?? 0),
                Descent    = g.Sum(a => a.TotalDescent ?? 0),
                TimeTotal  = g.Sum(a => a.TimeTotal ?? 0),
                TimeMoving = g.Sum(a => a.TimeMoving ?? 0),
                SpeedSum   = g.Sum(a => a.SpeedAvg ?? 0),
            })
            .ToListAsync();

        var byType = groups
            .Select(g => new TypeStatistics { Type = g.Type ?? UnknownType, Stats = ToStatistics(g) })
            .OrderBy(t => t.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Overall = sum of groups. AvgSpeed is a count-weighted mean (sum of per-activity speeds / count),
        // which equals the average over all activities and stays consistent with the per-type numbers.
        var overall = ToStatistics(new TypeAggregate
        {
            Count      = groups.Sum(g => g.Count),
            Distance   = groups.Sum(g => g.Distance),
            Ascent     = groups.Sum(g => g.Ascent),
            Descent    = groups.Sum(g => g.Descent),
            TimeTotal  = groups.Sum(g => g.TimeTotal),
            TimeMoving = groups.Sum(g => g.TimeMoving),
            SpeedSum   = groups.Sum(g => g.SpeedSum),
        });

        return new StatisticsSummary { Overall = overall, ByType = byType };
    }

    public async Task<List<CumulativeTypePoint>> GetCumulativeByTypeAsync(StatisticsFilter filter)
    {
        // Pull only the aggregated columns. Bucketing by calendar date is done in memory (SQLite date
        // handling differs from the in-app local/UTC fallback). Prefer the local start time so year-over-year
        // lines align to the athlete's own calendar.
        var rows = await BuildQuery(filter)
            .Where(a => a.StartDateTimeLocal != null || a.StartDateTimeUtc != null)
            .Select(a => new
            {
                a.StartDateTimeLocal,
                a.StartDateTimeUtc,
                a.ActivityType,
                Distance = a.TotalDistance ?? 0,
                Ascent   = a.TotalAscent ?? 0,
                Moving   = a.TimeMoving ?? 0,
            })
            .ToListAsync();

        var byKey = new Dictionary<(DateTime Date, string Type), CumulativeTypePoint>();
        foreach (var r in rows)
        {
            // Latent NRE closed (H2 precedent): a row with neither timestamp is skipped, not thrown on.
            if ((r.StartDateTimeLocal ?? r.StartDateTimeUtc) is not DateTime start) continue;
            var dt   = start.Date;
            var type = r.ActivityType ?? "";
            var key  = (dt, type);
            if (!byKey.TryGetValue(key, out var pt))
            {
                pt = new CumulativeTypePoint { Date = dt.ToWallClockIso(), Type = type };
                byKey[key] = pt;
            }
            pt.DistanceKm    += r.Distance / 1000.0;
            pt.ElevationM    += r.Ascent;
            pt.MovingTimeMin += r.Moving / 60.0;
            pt.Count         += 1;
        }

        return byKey.Values.OrderBy(p => p.Date, StringComparer.Ordinal)
            .ThenBy(p => p.Type, StringComparer.Ordinal).ToList();
    }

    private static ActivityStatistics ToStatistics(TypeAggregate g) => new()
    {
        TotalActivities = g.Count,
        TotalDistance   = g.Distance,
        TotalAscent     = g.Ascent,
        TotalDescent    = g.Descent,
        TotalTime       = g.TimeTotal,
        TotalMovingTime = g.TimeMoving,
        AverageSpeed    = g.Count > 0 ? g.SpeedSum / g.Count : 0,
    };

    // Flat carrier for the grouped aggregate: also used to sum groups into the overall row.
    private sealed class TypeAggregate
    {
        public string? Type { get; init; }
        public int Count { get; init; }
        public double Distance { get; init; }
        public double Ascent { get; init; }
        public double Descent { get; init; }
        public double TimeTotal { get; init; }
        public double TimeMoving { get; init; }
        public double SpeedSum { get; init; }
    }
}
