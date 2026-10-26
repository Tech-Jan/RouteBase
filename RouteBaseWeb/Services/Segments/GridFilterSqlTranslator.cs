using System.Globalization;
using RouteBaseWeb.Helpers;
using RouteBaseWeb.Helpers.Grid;
using RouteBaseWeb.Models.GridFilter;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// Renders one adopted grid <see cref="ColumnFilter"/> to a parameterised SQL predicate, mirroring the EF-side
/// <see cref="GridColumn{TEntity}"/> semantics exactly: the same range parsing + display→storage scaling
/// (<see cref="GridColumnSqlSpec.Scale"/>), the same <c>LIKE … ESCAPE '\'</c> wildcards, and NULL-never-matches
/// (free via SQL three-valued logic). The physical column is taken from the registry spec — the allowlist — so
/// nothing here can address an unlisted column.
///
/// Handles Text / Combo / MultiSelect / NumberRange. Date columns are rendered by <c>DateRangeSqlFilter</c>
/// (shared with the document dateRange filter) — see <c>CandidateSqlFilters</c>.
/// </summary>
public static class GridFilterSqlTranslator
{
    /// <summary>
    /// Renders the predicate for <paramref name="value"/> on <paramref name="spec"/>, or null when it yields no
    /// usable constraint. <paramref name="p"/> registers a value and returns its positional "{n}" placeholder.
    /// Any predicate carrying a top-level OR is parenthesised, so the result is safe inside an AND chain.
    /// </summary>
    public static string? Translate(GridColumnSqlSpec spec, ColumnFilter value, Func<object, string> p)
    {
        // Physical columns get the candidate query's `a.` alias; an aggregate spec (e.g. photos) is a full
        // scalar expression already qualified with `a`, used verbatim.
        var col = spec.IsRawExpression ? spec.Column : "a." + spec.Column;
        return spec.Kind switch
        {
            GridColumnKind.NumberRange => NumberRange(col, spec.Scale, value, p),
            GridColumnKind.Text        => TextLike(col, value.Text, p),
            GridColumnKind.MultiSelect => InSet(col, value.In, p),
            GridColumnKind.Combo       => Combo(col, value, p),
            _ => null,   // DateRange → DateRangeSqlFilter; None never reaches here (not in the allowlist).
        };
    }

    /// <summary>Multi-range numeric predicate: OR of each (min,max) closed range, bounds scaled to storage
    /// units. Mirrors <c>NumberColumn.ApplyFilter</c> + <c>WhereInRanges</c> (reversed/unbounded dropped).</summary>
    private static string? NumberRange(string col, double scale, ColumnFilter value, Func<object, string> p)
    {
        var ranges = new List<(double? Min, double? Max)>();

        foreach (var (min, max) in GridFilterParsing.ParseRanges(value.Range))
            ranges.Add((Scale(min, scale), Scale(max, scale)));

        var legacyMin = ParseScaled(value.Min, scale);
        var legacyMax = ParseScaled(value.Max, scale);
        if (legacyMin.HasValue || legacyMax.HasValue)
            ranges.Add((legacyMin, legacyMax));

        var ors = new List<string>();
        foreach (var (min, max) in ranges)
        {
            if (min.HasValue && max.HasValue && min > max) continue;   // reversed → drop (defensive)

            var parts = new List<string>();
            if (min.HasValue) parts.Add($"{col} >= {p(min.Value)}");
            if (max.HasValue) parts.Add($"{col} <= {p(max.Value)}");
            if (parts.Count == 0) continue;                            // fully unbounded → skip (else matches NULLs)

            ors.Add(parts.Count == 1 ? parts[0] : "(" + string.Join(" AND ", parts) + ")");
        }

        return Or(ors);
    }

    /// <summary>Wildcard text predicate. Positive <c>|</c>-terms OR together; negated terms (leading
    /// <c>!</c> / <c>!=</c>) are exclusions AND'd on top, NULL-safe so a NULL column is included by a
    /// negation. Mirrors <c>WhereTextLike</c> / <c>BuildTextLikePredicate</c>.</summary>
    private static string? TextLike(string col, string? pattern, Func<object, string> p)
    {
        var positives = new List<string>();
        var negatives = new List<string>();
        foreach (var raw in GridFilterParsing.SplitOrTerms(pattern))
        {
            var (negated, term) = GridFilterParsing.StripNegation(raw);
            if (term.Length == 0) continue; // bare `!` → no constraint
            var likePat = p(GridFilterExtensions.ToLikePattern(term));
            if (negated)
                negatives.Add($"({col} IS NULL OR {col} NOT LIKE {likePat} ESCAPE '\\')");
            else
                positives.Add($"{col} LIKE {likePat} ESCAPE '\\'");
        }

        var pos = Or(positives);
        var neg = And(negatives);
        if (pos is null) return neg;
        if (neg is null) return pos;
        return $"({pos} AND {neg})";
    }

    /// <summary>Exact set membership (<c>IN</c>). NULL never matches IN. Mirrors <c>WhereInSet</c>.</summary>
    private static string? InSet(string col, IReadOnlyList<string>? values, Func<object, string> p)
    {
        if (values is null || values.Count == 0) return null;
        return $"{col} IN ({string.Join(",", values.Select(v => p(v)))})";
    }

    /// <summary>Combo: wildcard text OR exact set, either half optional. Mirrors <c>WhereTextLikeOrInSet</c>.</summary>
    private static string? Combo(string col, ColumnFilter value, Func<object, string> p)
    {
        var like = TextLike(col, value.Text, p);
        var inSet = InSet(col, value.In, p);
        if (like is null) return inSet;
        if (inSet is null) return like;
        return $"({like} OR {inSet})";
    }

    /// <summary>Joins OR-terms, parenthesising when more than one so the result is AND-chain safe.</summary>
    private static string? Or(IReadOnlyList<string> terms) => terms.Count switch
    {
        0 => null,
        1 => terms[0],
        _ => "(" + string.Join(" OR ", terms) + ")",
    };

    /// <summary>Joins AND-terms, parenthesising when more than one so the result is OR/AND-chain safe.</summary>
    private static string? And(IReadOnlyList<string> terms) => terms.Count switch
    {
        0 => null,
        1 => terms[0],
        _ => "(" + string.Join(" AND ", terms) + ")",
    };

    private static double? Scale(double? v, double scale) => v.HasValue ? v.Value * scale : null;

    // SC-02: reject NaN/±Infinity (TryParse accepts them; a non-finite SQL param throws at bind time).
    private static double? ParseScaled(string? raw, double scale)
        => double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
           && double.IsFinite(v) ? v * scale : null;
}
