using System.Globalization;
using System.Security.Claims;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Http;

namespace RouteBaseWeb.Services.ErrorLog;

/// <summary>
/// ILoggerProvider that mirrors Error+ logs from every category into the <see cref="ErrorLogChannel"/>
/// (OBS-03). Being the single store-write path, it captures the exception filter's LogError, every
/// hosted-service catch, and the CC2-01 net's own middleware log with no extra plumbing. Source is
/// structural: an ambient HttpContext ⇒ "api" (+ path + user), none ⇒ "background".
/// Recursion is guarded twice — ignored category prefixes (own namespace + EF) and the AsyncLocal
/// <see cref="ErrorLogSuppression"/> flag set around the store write.
/// </summary>
public sealed class ErrorLogLoggerProvider : ILoggerProvider
{
    private readonly ErrorLogChannel _channel;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private readonly TimeProvider _clock;

    public ErrorLogLoggerProvider(ErrorLogChannel channel, IHttpContextAccessor httpContextAccessor, TimeProvider clock)
    {
        _clock = clock;
        _channel = channel;
        _httpContextAccessor = httpContextAccessor;
    }

    public ILogger CreateLogger(string categoryName) =>
        new ErrorLogLogger(categoryName, _channel, _httpContextAccessor, _clock);

    public void Dispose() { }

    private sealed class ErrorLogLogger : ILogger
    {
        // EF logs command failures at Error *before* the exception propagates to the throwing caller (which
        // logs the same failure with full context) — a guaranteed duplicate that would also recurse during a
        // store write. The own-namespace prefix stops the store/provider/drain re-entering themselves.
        private static readonly string[] IgnoredPrefixes =
        {
            "RouteBaseWeb.Services.ErrorLog",
            "Microsoft.EntityFrameworkCore",
        };

        private readonly string _category;
        private readonly ErrorLogChannel _channel;
        private readonly IHttpContextAccessor _http;

        private readonly TimeProvider _clock;

        public ErrorLogLogger(string category, ErrorLogChannel channel, IHttpContextAccessor http, TimeProvider clock)
        {
            _category = category;
            _channel = channel;
            _http = http;
            _clock = clock;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Internal guard independent of AddFilter config: suppression off, Error+, non-ignored category.
        public bool IsEnabled(LogLevel logLevel) =>
            !ErrorLogSuppression.Active && logLevel >= LogLevel.Error && !IsIgnored();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                var http = _http.HttpContext;
                var (source, path, userId) = http is null
                    ? ("background", (string?)null, (string?)null)
                    : ("api",
                       // Redacted at the WRITE: a share token is a credential, and this path is persisted
                       // to Error_Log and rendered on /Admin/Errors. A token written in plaintext cannot
                       // be un-written.
                       ShareUrlRedaction.RedactMethodAndPath(
                           $"{http.Request.Method} {http.Request.Path}"),
                       http.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                           ?? http.User?.FindFirst("sub")?.Value);

                var entry = new ErrorLogEntry(
                    _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    source,
                    _category,
                    logLevel == LogLevel.Critical ? "Critical" : "Error",
                    // The MESSAGE needs redacting too, not just the path column above: any call site that
                    // formats the request path into its own text (ApiExceptionFilter's
                    // "Unhandled exception: {Method} {Path}") would otherwise persist a live share token
                    // in plaintext and render it on /Admin/Errors. Redacting the structured field alone
                    // left the credential in the prose next to it.
                    ShareUrlRedaction.RedactAnywhere(formatter(state, exception)),
                    exception?.GetType().FullName,
                    ShareUrlRedaction.RedactAnywhere(exception?.ToString()),
                    path,
                    userId);

                _channel.TryWrite(entry); // non-blocking; drops under storm
            }
            catch
            {
                // Fail-silent: the mirror must never throw into the logging call site (PROCESS invariant 4).
            }
        }

        private bool IsIgnored()
        {
            foreach (var prefix in IgnoredPrefixes)
                if (_category.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
