using System.Text.Json;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services.Segments.Rules;

namespace RouteBaseWeb.Models.Requests;

/// <summary>
/// Payload for creating/updating a shape-defined segment, or for the live match-count preview.
/// Carries the rule document verbatim (see <see cref="RuleDocument"/>).
/// </summary>
public class CreateShapeSegmentRequest
{
    public string SegmentName { get; set; } = "";
    public string? ActivityType { get; set; }

    /// <summary>
    /// The rule document as raw JSON. Kept as a <see cref="JsonElement"/> so parsing goes through
    /// <see cref="RuleDocument.TryParse"/> (one parser, one set of serializer options) rather than
    /// the framework's model binding.
    /// </summary>
    public JsonElement? Rules { get; set; }

    /// <summary>
    /// Optional owner combo filter (same shape the activities grid sends) selecting whose visible tracks this
    /// segment matches. Null / empty preserves the legacy "own tracks only" behaviour. Resolved and authorised
    /// server-side via <see cref="RouteBaseWeb.Helpers.Grid.IOwnerScopeService"/>.
    /// </summary>
    public ColumnFilter? Owner { get; set; }

    /// <summary>Parse the rule document; null when absent or malformed.</summary>
    public RuleDocument? ParseRules() =>
        Rules is { } el ? RuleDocument.TryParse(el.GetRawText()) : null;
}
