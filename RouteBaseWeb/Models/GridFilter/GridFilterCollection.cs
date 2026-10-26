using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models.GridFilter;

/// <summary>
/// The set of per-column filter-row values for one grid request, keyed by column key. Populated from the
/// generic <c>f.&lt;key&gt;.&lt;suffix&gt;</c> query format (plus legacy aliases) by <see cref="GridFilterModelBinder"/>
/// on GET endpoints, or from a plain JSON key→filter map by <see cref="GridFilterCollectionJsonConverter"/>
/// on POST bodies, then applied via a <see cref="RouteBaseWeb.Helpers.Grid.GridColumnRegistry{TEntity}"/>.
/// Entity-agnostic, so every grid reuses it.
/// </summary>
[JsonConverter(typeof(GridFilterCollectionJsonConverter))]
public sealed class GridFilterCollection
{
    /// <summary>Column key → its filter value. Case-insensitive keys.</summary>
    public Dictionary<string, ColumnFilter> Items { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when nothing here would constrain the query.</summary>
    public bool IsEmpty => Items.Count == 0 || Items.Values.All(v => v.IsEmpty);

    public static GridFilterCollection Empty { get; } = new();

    /// <summary>Gets the filter for a column, creating an empty one if absent.</summary>
    public ColumnFilter GetOrAdd(string key)
    {
        if (!Items.TryGetValue(key, out var f))
            Items[key] = f = new ColumnFilter();
        return f;
    }
}
