using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models.GridFilter;

/// <summary>
/// One column's filter-row value, as received on the wire (see the generic <c>f.&lt;key&gt;.&lt;suffix&gt;</c>
/// format bound by <see cref="GridFilterModelBinder"/>). Range/date bounds are kept as raw strings and parsed
/// (with the column's display→storage scale) inside the column descriptor, so a stray value is treated as
/// "no constraint" rather than failing model binding.
/// </summary>
public sealed class ColumnFilter
{
    /// <summary>Wildcard text (<c>*</c>/<c>?</c>) for a text column.</summary>
    public string? Text { get; set; }

    /// <summary>Selected values for a multi-select column (SQL <c>IN</c>).</summary>
    public List<string> In { get; set; } = new();

    /// <summary>Lower bound for a number range (display units, e.g. km). Legacy two-box param.</summary>
    public string? Min { get; set; }

    /// <summary>Upper bound for a number range (display units, e.g. km). Legacy two-box param.</summary>
    public string? Max { get; set; }

    /// <summary>
    /// Numeric range expression for the single-box multi-range filter (e.g. <c>3-6, 8-12</c>, <c>&gt;5</c>),
    /// display units. Parsed by <see cref="RouteBaseWeb.Helpers.GridFilterParsing.ParseRanges"/>.
    /// </summary>
    public string? Range { get; set; }

    /// <summary>Lower bound for a date range (inclusive).</summary>
    public string? From { get; set; }

    /// <summary>Upper bound for a date range (inclusive of the whole day).</summary>
    public string? To { get; set; }

    /// <summary>True when this carries no usable constraint, so the column predicate can be skipped.
    /// <see cref="JsonIgnoreAttribute"/>: this is derived state — it must never serialize into a stored rule document.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Text) &&
        In is not { Count: > 0 } &&   // SC-03: explicit JSON "in": null deserializes to a null list
        string.IsNullOrWhiteSpace(Min) && string.IsNullOrWhiteSpace(Max) &&
        string.IsNullOrWhiteSpace(Range) &&
        string.IsNullOrWhiteSpace(From) && string.IsNullOrWhiteSpace(To);
}
