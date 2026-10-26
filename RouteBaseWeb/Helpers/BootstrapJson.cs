using System.Text.Json;

namespace RouteBaseWeb.Helpers;

/// <summary>
/// Single serializer for page grid-bootstrap payloads (camelCase; default encoder so it HTML-escapes
/// '&lt;', which keeps Html.Raw inside a &lt;script&gt; tag safe). Rule of three: Index, Segments, and
/// Activities all emit window.*Bootstrap JSON identically (MD-03).
/// </summary>
public static class BootstrapJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
