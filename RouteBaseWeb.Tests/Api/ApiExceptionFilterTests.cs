using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// Unit tests for the global <see cref="ApiExceptionFilter"/> (OBS-01) — no HTTP harness exists, so the
/// filter is exercised by constructing an <see cref="ExceptionContext"/> directly. Pins the wire shape
/// (500 <c>{ code, message }</c> — the canonical ApiError), the client-abort OCE→499 branch, and the
/// real-timeout OCE→500 branch.
/// </summary>
public class ApiExceptionFilterTests
{
    private static ExceptionContext MakeContext(HttpContext http, Exception ex)
    {
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = ex };
    }

    [Fact]
    public void GenericException_Returns500FrozenBody_AndLogsError()
    {
        var logger = new CapturingLogger<ApiExceptionFilter>();
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/api/thing";
        var ctx = MakeContext(http, new InvalidOperationException("boom"));

        new ApiExceptionFilter(logger).OnException(ctx);

        var obj = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, obj.StatusCode);
        var body = Assert.IsType<ApiError>(obj.Value);
        Assert.Equal("An unexpected error occurred.", body.Message);
        Assert.Equal(ApiErrorCodes.ServerError, body.Code);
        Assert.True(ctx.ExceptionHandled);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void ClientAbortedOce_Returns499_AndDoesNotLogError()
    {
        var logger = new CapturingLogger<ApiExceptionFilter>();
        var http = new DefaultHttpContext();
        http.RequestAborted = new CancellationToken(canceled: true);
        var ctx = MakeContext(http, new OperationCanceledException());

        new ApiExceptionFilter(logger).OnException(ctx);

        var status = Assert.IsType<StatusCodeResult>(ctx.Result);
        Assert.Equal(499, status.StatusCode);
        Assert.True(ctx.ExceptionHandled);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void ShareEndpointException_LogsTheRedactedPath_NeverTheToken()
    {
        // ErrorLogLoggerProvider redacts only its OWN store; this log line fans out to every registered
        // provider, so the console/stdout sink would otherwise receive a live share credential.
        var logger = new CapturingLogger<ApiExceptionFilter>();
        var http = new DefaultHttpContext();
        var token = ShareTokens.New();
        http.Request.Method = "GET";
        http.Request.Path = $"/s/{token}/api/photos";
        var ctx = MakeContext(http, new InvalidOperationException("boom"));

        new ApiExceptionFilter(logger).OnException(ctx);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.DoesNotContain(token, entry.Message, StringComparison.Ordinal);
        Assert.Contains("/s/{token}/api/photos", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareEndpointClientAbort_DebugLog_NeverContainsTheToken()
    {
        var logger = new CapturingLogger<ApiExceptionFilter>();
        var http = new DefaultHttpContext();
        var token = ShareTokens.New();
        http.Request.Method = "GET";
        http.Request.Path = $"/s/{token}/api/segment/tracks";
        http.RequestAborted = new CancellationToken(canceled: true);
        var ctx = MakeContext(http, new OperationCanceledException());

        new ApiExceptionFilter(logger).OnException(ctx);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public void OceWithoutAbortedRequest_TreatedAsGeneric500()
    {
        // A real timeout (not a client abort): RequestAborted is not cancelled → generic 500.
        var logger = new CapturingLogger<ApiExceptionFilter>();
        var http = new DefaultHttpContext();
        var ctx = MakeContext(http, new OperationCanceledException());

        new ApiExceptionFilter(logger).OnException(ctx);

        var obj = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, obj.StatusCode);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
    }
}

file sealed class CapturingLogger<T> : ILogger<T>
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
