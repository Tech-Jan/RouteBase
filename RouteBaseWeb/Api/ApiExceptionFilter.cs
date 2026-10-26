using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace RouteBaseWeb.Api;

/// <summary>
/// Global MVC exception filter (OBS-01): the single log+500 path for every <c>api/</c> controller,
/// replacing the per-endpoint catch-all plumbing. Only LOGS — the Error+ store write
/// rides the ILoggerProvider mirror (OBS-03), so this stays a sync <see cref="IExceptionFilter"/> with
/// no async work. Semantic catches (OCE→499, IOE→400/409, inner-finalize) keep their own try blocks and
/// never reach here. The CC2-01 middleware net stays as the backstop for non-MVC throws.
/// </summary>
public sealed class ApiExceptionFilter : IExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        // Redacted BEFORE formatting: this log line fans out to every registered provider, and only the
        // Error_Log mirror redacts on its own — the console/stdout sink would otherwise persist a live
        // share credential into container logs. Non-share paths pass through unchanged.
        var path = ShareUrlRedaction.RedactPath(context.HttpContext.Request.Path.Value);

        // Client-aborted request: not an error (the client is gone). Mirrors the per-endpoint
        // OCE→499 convention; logged at Debug, never stored.
        if (context.Exception is OperationCanceledException
            && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug("Request aborted by client: {Method} {Path}",
                context.HttpContext.Request.Method, path);
            context.Result = new StatusCodeResult(499);
            context.ExceptionHandled = true;
            return;
        }

        _logger.LogError(context.Exception, "Unhandled exception: {Method} {Path}",
            context.HttpContext.Request.Method, path);
        context.Result = new ObjectResult(ApiError.ServerError("An unexpected error occurred."))
            { StatusCode = StatusCodes.Status500InternalServerError };
        context.ExceptionHandled = true;
    }
}
