using System.Globalization;
using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services.Segments.Rules;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// Translates a rule document's non-spatial <see cref="TrackFilter"/>s into parameterised SQL
/// predicates applied while loading candidate activities (before the bbox/GPS geometry stages).
/// Lives beside the matcher so the <c>Rules/</c> core stays SQL-free.
/// </summary>
public interface ICandidateSqlFilter
{
    /// <summary>Render this filter as a SQL boolean expression. <paramref name="p"/> is the matcher's
    /// positional-parameter helper: it registers a value and returns its "{n}" placeholder.</summary>
    string ToSql(Func<object, string> p);
}

public static class CandidateSqlFilters
{
    /// <summary>
    /// Build the ordered filter list for a candidate query. Exactly one owner filter is always present,
    /// resolving owner precedence: document <see cref="OwnerFilter"/> → legacy <c>match_owner_ids</c> CSV
    /// → own-only. Date-range / activity-type filters follow when present in the document. Absorbs the
    /// legacy owner-CSV parse the matcher used to do inline.
    /// </summary>
    public static List<ICandidateSqlFilter> Build(
        IReadOnlyList<TrackFilter> docFilters, string? legacyOwnerCsv, string currentUserId)
    {
        var filters = new List<ICandidateSqlFilter>();

        // Owner precedence: the doc's owner filter wins, then the legacy column CSV, then own-only.
        var docOwner = docFilters.OfType<OwnerFilter>().FirstOrDefault();
        IReadOnlyList<string> ownerIds =
            docOwner != null        ? docOwner.UserIds
          : legacyOwnerCsv != null  ? ParseOwnerCsv(legacyOwnerCsv)   // "" → empty → match nobody
                                    : new[] { currentUserId };         // legacy null → own-only
        filters.Add(new OwnerSqlFilter(ownerIds));

        foreach (var f in docFilters)
        {
            switch (f)
            {
                case DateRangeFilter dr:
                    DateOnly? from = dr.TryParseFrom(out var fv) ? fv : null;
                    DateOnly? to   = dr.TryParseTo(out var tv) ? tv : null;
                    if (from != null || to != null)
                        filters.Add(new DateRangeSqlFilter(from, to));
                    break;
                case ActivityTypeFilter at when at.Types.Count > 0:
                    filters.Add(new ActivityTypeSqlFilter(at.Types));
                    break;
                case GridFiltersFilter gf:
                    AddGridFilters(filters, gf);
                    break;
            }
        }

        return filters;
    }

    /// <summary>
    /// Translates each adopted grid column filter into a WHERE predicate via the column-registry allowlist.
    /// Unknown / non-filterable keys (e.g. <c>owner</c>) resolve no spec and are silently ignored — they never
    /// reach a column. Date columns reuse <see cref="DateRangeSqlFilter"/> (shared with the doc dateRange
    /// filter); everything else goes through <see cref="GridFilterSqlTranslator"/>.
    /// </summary>
    private static void AddGridFilters(List<ICandidateSqlFilter> filters, GridFiltersFilter gf)
    {
        foreach (var (key, value) in gf.Filters)
        {
            if (value is null || value.IsEmpty) continue;
            if (!ActivityGridColumns.Instance.TryGetSqlSpec(key, out var spec)) continue;   // allowlist

            if (spec.Kind == GridColumnKind.DateRange)
            {
                DateOnly? from = TryParseGridDate(value.From);
                DateOnly? to = TryParseGridDate(value.To);
                if (from != null || to != null)
                    filters.Add(new DateRangeSqlFilter(from, to));
            }
            else
            {
                filters.Add(new GridColumnSqlFilter(spec, value));
            }
        }
    }

    /// <summary>Grid date bounds arrive as "yyyy-MM-dd"; lenient parse mirrors the EF-side <c>DateColumn</c>.</summary>
    private static DateOnly? TryParseGridDate(string? raw)
        => DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateOnly.FromDateTime(d)
            : null;

    /// <summary>Parse a legacy <c>match_owner_ids</c> CSV; an empty string → empty list (match nobody).</summary>
    private static List<string> ParseOwnerCsv(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>Owner scope: no ids → match nobody (<c>1 = 0</c>); otherwise <c>a.user_id IN (…)</c>.</summary>
internal sealed class OwnerSqlFilter : ICandidateSqlFilter
{
    private readonly IReadOnlyList<string> _userIds;
    public OwnerSqlFilter(IReadOnlyList<string> userIds) => _userIds = userIds;

    public string ToSql(Func<object, string> p) =>
        _userIds.Count == 0 ? "1 = 0" : $"a.user_id IN ({string.Join(",", _userIds.Select(id => p(id)))})";
}

/// <summary>Exact activity-type multi-select: <c>a.activity_type IN (…)</c>.</summary>
internal sealed class ActivityTypeSqlFilter : ICandidateSqlFilter
{
    private readonly IReadOnlyList<string> _types;
    public ActivityTypeSqlFilter(IReadOnlyList<string> types) => _types = types;

    public string ToSql(Func<object, string> p) =>
        $"a.activity_type IN ({string.Join(",", _types.Select(t => p(t)))})";
}

/// <summary>
/// Local start-date range on <c>start_datetime_loc</c>. The column is TEXT; startup normalisation
/// (<c>DbSchemaManager.NormalizeDateTimeTextFormatsAsync</c>) converts any legacy ISO
/// "yyyy-MM-ddTHH:mm:ss" rows to the canonical space-separated form, and the <c>datetime()</c>
/// wrapper here is belt-and-braces so the range stays format-proof even for a row written before the
/// next startup. End-day inclusive via +1 day, matching the activities grid. A NULL or unparseable
/// <c>start_datetime_loc</c> yields NULL and is excluded whenever a bound is present.
/// </summary>
internal sealed class DateRangeSqlFilter : ICandidateSqlFilter
{
    private readonly DateOnly? _from;
    private readonly DateOnly? _to;
    public DateRangeSqlFilter(DateOnly? from, DateOnly? to) { _from = from; _to = to; }

    public string ToSql(Func<object, string> p)
    {
        var clauses = new List<string>();
        if (_from is { } f)
            clauses.Add($"datetime(a.start_datetime_loc) >= {p(AtMidnight(f))}");
        if (_to is { } t)
            // SC-05: DateOnly.MaxValue (9999-12-31) overflows AddDays(1); clamp (losing that final day's
            // window is acceptable). A validated "9999-12-31" bound would otherwise throw at query time.
            clauses.Add($"datetime(a.start_datetime_loc) <= {p(AtMidnight(t == DateOnly.MaxValue ? t : t.AddDays(1)))}");
        return string.Join(" AND ", clauses);
    }

    private static string AtMidnight(DateOnly d) =>
        d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00";
}

/// <summary>One adopted grid column filter, rendered via <see cref="GridFilterSqlTranslator"/>. A value that
/// yields no predicate (e.g. an unparseable range) becomes a no-op <c>1 = 1</c>, mirroring the EF path which
/// simply skips it — the AND chain stays well-formed.</summary>
internal sealed class GridColumnSqlFilter : ICandidateSqlFilter
{
    private readonly GridColumnSqlSpec _spec;
    private readonly ColumnFilter _value;
    public GridColumnSqlFilter(GridColumnSqlSpec spec, ColumnFilter value) { _spec = spec; _value = value; }

    public string ToSql(Func<object, string> p) => GridFilterSqlTranslator.Translate(_spec, _value, p) ?? "1 = 1";
}
