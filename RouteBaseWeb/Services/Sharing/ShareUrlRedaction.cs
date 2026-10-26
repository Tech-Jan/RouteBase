using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// Strips a share token out of a request path before it is persisted or displayed.
///
/// A share token IS the credential, so anywhere a request path is stored is somewhere a live token would
/// otherwise sit in plaintext — the Error_Log table (rendered on /Admin/Errors) being the concrete case.
/// Redaction happens at the write, not the read: a token already written cannot be un-written.
/// </summary>
public static partial class ShareUrlRedaction
{
    /// <summary>Route prefix every share URL lives under.</summary>
    public const string SharePathPrefix = "/s/";

    private const string Placeholder = "/s/{token}";

    /// <summary>
    /// Matches a share token wherever it appears in free text — <c>/s/</c> followed by exactly
    /// <see cref="ShareTokens.TokenLength"/> characters of the token alphabet. Anchored to that exact
    /// length so it cannot swallow an unrelated <c>/s/…</c> path. IgnoreCase because routing is
    /// case-insensitive — <c>/S/&lt;token&gt;</c> serves normally, so it must redact too. The alphabet
    /// class spans both cases explicitly, so the flag only widens the prefix match.
    /// </summary>
    [GeneratedRegex(@"/s/[A-Za-z0-9_-]{43}", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex TokenInText();

    /// <summary>
    /// Redacts every share token ANYWHERE in a free-text string, not just at the start of a path.
    ///
    /// <see cref="RedactPath"/> only helps when the whole string IS a path. Log call sites format the
    /// request path into their own prose ("Unhandled exception: GET /s/&lt;token&gt;/api/photos"), and an
    /// exception's ToString can carry a URL deep inside a stack trace — both are persisted to Error_Log
    /// and rendered on /Admin/Errors, so both need this.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? RedactAnywhere(string? text)
        => string.IsNullOrEmpty(text) ? text : TokenInText().Replace(text, Placeholder);

    /// <summary>
    /// Collapses <c>/s/&lt;token&gt;/rest</c> to <c>/s/{token}/rest</c>. Any other path is returned
    /// unchanged. Case-insensitive on the prefix because routing is.
    /// </summary>
    public static string? RedactPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (!path.StartsWith(SharePathPrefix, StringComparison.OrdinalIgnoreCase)) return path;

        // Keep whatever follows the token segment: "/s/{token}/api/photos/3/image" stays diagnosable.
        var afterPrefix = SharePathPrefix.Length;
        var nextSlash = path.IndexOf('/', afterPrefix);

        return nextSlash < 0 ? Placeholder : Placeholder + path[nextSlash..];
    }

    /// <summary>
    /// Redacts a "<c>METHOD /path</c>" pair as stored by the error log. Splits on the first space only —
    /// the path itself may contain no space, but being lenient here is cheaper than being wrong.
    /// </summary>
    public static string? RedactMethodAndPath(string? methodAndPath)
    {
        if (string.IsNullOrEmpty(methodAndPath)) return methodAndPath;

        var space = methodAndPath.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0) return RedactPath(methodAndPath);

        var method = methodAndPath[..space];
        var path = methodAndPath[(space + 1)..];
        return $"{method} {RedactPath(path)}";
    }
}
