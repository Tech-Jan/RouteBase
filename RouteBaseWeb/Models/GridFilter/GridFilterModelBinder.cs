using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Primitives;

namespace RouteBaseWeb.Models.GridFilter;

/// <summary>
/// Binds the generic per-column filter-row params into a <see cref="GridFilterCollection"/>. It scans the whole
/// query string (not a single value), so any grid reuses it with no per-column code:
///
/// <list type="bullet">
///   <item><c>f.&lt;key&gt;.text</c> → wildcard text</item>
///   <item><c>f.&lt;key&gt;.in</c> (repeatable) → multi-select membership</item>
///   <item><c>f.&lt;key&gt;.min</c> / <c>f.&lt;key&gt;.max</c> → number range</item>
///   <item><c>f.&lt;key&gt;.from</c> / <c>f.&lt;key&gt;.to</c> → date range</item>
/// </list>
///
/// Legacy aliases (<c>fName</c>, <c>fDistMin/Max</c>, <c>fAscMin/Max</c>) from the pre-generic URL format are
/// also accepted so old shared/bookmarked links keep working. TODO(2026-12): drop the legacy aliases.
/// </summary>
public sealed class GridFilterModelBinder : IModelBinder
{
    // Defensive cap on how many distinct columns a single request may filter on.
    private const int MaxColumns = 64;

    // Old page-URL param name → (column key, setter on the column's ColumnFilter). These are the pre-generic
    // per-column URL params; accepting them lets old shared/bookmarked links still hydrate the grid (the client
    // then rewrites the URL to the f.* format). Values are in km/raw display units so the registry scale applies.
    // TODO(2026-12): drop these once enough time has passed for old bookmarks to be re-saved.
    private static readonly Dictionary<string, (string Key, Action<ColumnFilter, string> Set)> LegacyAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["fName"]    = ("name",     (f, v) => f.Text = v),
            ["fDistMin"] = ("distance", (f, v) => f.Min = v),
            ["fDistMax"] = ("distance", (f, v) => f.Max = v),
            ["fAscMin"]  = ("ascent",   (f, v) => f.Min = v),
            ["fAscMax"]  = ("ascent",   (f, v) => f.Max = v),
        };

    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var result = new GridFilterCollection();
        var query = bindingContext.HttpContext.Request.Query;

        foreach (var (rawKey, values) in query)
        {
            if (rawKey.StartsWith("f.", StringComparison.Ordinal))
                ApplyGeneric(result, rawKey, values);
            else if (LegacyAliases.TryGetValue(rawKey, out var alias))
                ApplyLegacy(result, alias, values);

            if (result.Items.Count > MaxColumns)
                break;
        }

        bindingContext.Result = ModelBindingResult.Success(result);
        return Task.CompletedTask;
    }

    // f.<key>.<suffix> → set the matching field on the column's ColumnFilter.
    private static void ApplyGeneric(GridFilterCollection result, string rawKey, StringValues values)
    {
        var parts = rawKey.Split('.');
        if (parts.Length != 3) return; // not "f.<key>.<suffix>"

        var key = parts[1];
        var suffix = parts[2];
        if (string.IsNullOrEmpty(key)) return;

        var f = result.GetOrAdd(key);
        switch (suffix.ToUpperInvariant())
        {
            case "TEXT":  f.Text = values.ToString(); break;
            case "MIN":   f.Min = values.ToString(); break;
            case "MAX":   f.Max = values.ToString(); break;
            case "RANGE": f.Range = values.ToString(); break;
            case "FROM": f.From = values.ToString(); break;
            case "TO":   f.To = values.ToString(); break;
            case "IN":
                f.In.AddRange(values.OfType<string>().Where(v => v.Length > 0));
                break;
        }
    }

    private static void ApplyLegacy(
        GridFilterCollection result, (string Key, Action<ColumnFilter, string> Set) alias, StringValues values)
    {
        var v = values.ToString();
        if (!string.IsNullOrEmpty(v))
            alias.Set(result.GetOrAdd(alias.Key), v);
    }
}
