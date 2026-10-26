namespace RouteBaseWeb.Services;

/// <summary>
/// Shared sanitizer for imported activity names. Every source funnels through the same rule:
/// strip control chars (&lt; 0x20), trim, cap at the app-wide 200-char track-name limit, and
/// treat an empty result as "no name" (null). Applied once at the <see cref="TrackFileParserRouter"/>
/// seam so filename-derived names (GPX &lt;name&gt;-less fallback, all TCX/FIT names) get the same
/// treatment as GPX &lt;name&gt; values; <see cref="GpxParsingService"/> also calls it to decide the
/// &lt;name&gt;-vs-filename fallback. Idempotent, so double-application at the seam is harmless.
/// </summary>
public static class ImportNameSanitizer
{
    /// <summary>Matches the app-wide track-name limit (RecordingRequestValidator/ActivitiesApiController).</summary>
    public const int MaxNameLength = 200;

    /// <summary>Sanitized name, or null when the input is null/blank or reduces to nothing.</summary>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Where(c => c >= 0x20).ToArray()).Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength] : cleaned;
    }
}
