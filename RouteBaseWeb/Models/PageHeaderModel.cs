using Microsoft.AspNetCore.Html;

namespace RouteBaseWeb.Models;

public record PageHeaderModel(
    string Title,
    string? Subtitle = null,
    IReadOnlyList<(string Href, string Label)>? Breadcrumbs = null,
    IReadOnlyList<string>? MetaChips = null,
    // Rendered unencoded by _PageHeader — CONSTANT, developer-authored HTML only (PI-08). The
    // IHtmlContent type forces callers through an explicit new HtmlString(...); never wrap a
    // string that interpolates a name/label — the first interpolated activity or segment name
    // here is stored XSS, and no encoder sits behind this sink.
    IHtmlContent? RightActionsHtml = null
);
