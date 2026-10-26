using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models.GridFilter;

/// <summary>
/// (De)serializes a <see cref="GridFilterCollection"/> as a plain JSON map of column key →
/// <see cref="ColumnFilter"/> — <c>{"name":{"text":"*x*"},"distance":{"range":"3-6"}}</c>, the same
/// per-column shape rule documents embed — instead of exposing the wrapper's <c>Items</c> property.
/// Rebuilding through <see cref="GridFilterCollection.Items"/>'s indexer keeps the case-insensitive
/// key comparer, which System.Text.Json's default dictionary handling would silently drop.
/// </summary>
public sealed class GridFilterCollectionJsonConverter : JsonConverter<GridFilterCollection>
{
    /// <summary>Defensive cap on filtered columns per request — mirrors <see cref="GridFilterModelBinder"/>.</summary>
    private const int MaxColumns = 64;

    public override GridFilterCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var items = JsonSerializer.Deserialize<Dictionary<string, ColumnFilter>>(ref reader, options);
        var result = new GridFilterCollection();
        if (items is null) return result;

        foreach (var (key, filter) in items)
        {
            if (result.Items.Count >= MaxColumns) break;
            result.Items[key] = filter; // last-wins on case-duplicate keys instead of throwing
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, GridFilterCollection value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value.Items, options);
}
