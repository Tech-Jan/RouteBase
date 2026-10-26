using System.Security.Claims;
using RouteBaseWeb.Services.ErrorLog;
using RouteBaseWeb.Services.Sharing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RouteBaseWeb.Tests.Services.ErrorLog;

/// <summary>
/// Capture rules for <see cref="ErrorLogLoggerProvider"/>: level/category/exception gating, source/path/user
/// discrimination from the ambient HttpContext, the two recursion guards, and non-blocking drop when full.
/// Exercised by direct logger invocation + draining the channel (no HTTP harness exists).
/// </summary>
public class ErrorLogLoggerProviderTests
{
    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static ILogger MakeLogger(ErrorLogChannel channel, IHttpContextAccessor http, string category)
    {
        using var provider = new ErrorLogLoggerProvider(channel, http, TimeProvider.System);
        return provider.CreateLogger(category);
    }

    private static ErrorLogEntry? Drain(ErrorLogChannel channel) =>
        channel.Reader.TryRead(out var e) ? e : null;

    [Fact]
    public void LogError_WithException_CapturesAllFields()
    {
        var channel = new ErrorLogChannel();
        var logger = MakeLogger(channel, new FakeHttpContextAccessor(), "My.Category");

        var ex = new InvalidOperationException("kaboom");
        logger.LogError(ex, "failed {Thing}", "widget");

        var e = Drain(channel);
        Assert.NotNull(e);
        Assert.Equal("Error", e!.Level);
        Assert.Equal("My.Category", e.Category);
        Assert.Equal("failed widget", e.Message);
        Assert.Equal("System.InvalidOperationException", e.ExceptionType);
        Assert.Contains("kaboom", e.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public void LogCritical_MapsToCriticalLevel()
    {
        var channel = new ErrorLogChannel();
        var logger = MakeLogger(channel, new FakeHttpContextAccessor(), "C");

        logger.LogCritical("meltdown");

        Assert.Equal("Critical", Drain(channel)!.Level);
    }

    [Fact]
    public void LogInformation_NotCaptured_InternalGuard()
    {
        var channel = new ErrorLogChannel();
        var logger = MakeLogger(channel, new FakeHttpContextAccessor(), "C");

        logger.LogInformation("just fyi");

        Assert.Null(Drain(channel));
    }

    [Theory]
    [InlineData("RouteBaseWeb.Services.ErrorLog.ErrorLogStore")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    public void IgnoredPrefixCategories_NotCaptured(string category)
    {
        var channel = new ErrorLogChannel();
        var logger = MakeLogger(channel, new FakeHttpContextAccessor(), category);

        logger.LogError("boom");

        Assert.Null(Drain(channel));
    }

    [Fact]
    public void SuppressionActive_NotCaptured_RecursionGuard()
    {
        var channel = new ErrorLogChannel();
        var logger = MakeLogger(channel, new FakeHttpContextAccessor(), "C");

        ErrorLogSuppression.Active = true;
        try
        {
            logger.LogError("boom");
        }
        finally
        {
            ErrorLogSuppression.Active = false;
        }

        Assert.Null(Drain(channel));
    }

    [Fact]
    public void WithHttpContext_SourceIsApi_WithPathAndUser()
    {
        var channel = new ErrorLogChannel();
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/api/recording/1/points";
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "user-42") }));
        var logger = MakeLogger(channel, new FakeHttpContextAccessor { HttpContext = ctx }, "C");

        logger.LogError("boom");

        var e = Drain(channel)!;
        Assert.Equal("api", e.Source);
        Assert.Equal("POST /api/recording/1/points", e.Path);
        Assert.Equal("user-42", e.UserId);
    }

    [Fact]
    public void NullHttpContext_SourceIsBackground_NullPathAndUser()
    {
        var channel = new ErrorLogChannel();
        var logger = MakeLogger(channel, new FakeHttpContextAccessor { HttpContext = null }, "C");

        logger.LogError("boom");

        var e = Drain(channel)!;
        Assert.Equal("background", e.Source);
        Assert.Null(e.Path);
        Assert.Null(e.UserId);
    }

    [Fact]
    public void ShareToken_IsRedactedAtTheWrite_InPathMessageAndStackTrace()
    {
        // Pins the WIRING, not the pure functions (ShareHardeningTests covers those): remove the
        // ShareUrlRedaction calls inside ErrorLogLoggerProvider and this goes red — otherwise a live
        // share token persists into Error_Log and renders on /Admin/Errors, the exact bug class
        // the share contract states ("redacted at the write").
        var token = new string('a', ShareTokens.TokenLength);
        var channel = new ErrorLogChannel();
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = $"/s/{token}/api/photos/3/image";
        var logger = MakeLogger(channel, new FakeHttpContextAccessor { HttpContext = ctx }, "C");

        var ex = new InvalidOperationException($"upstream failed for /s/{token}/api/photos/3/image");
        logger.LogError(ex, "Unhandled exception: GET /s/{Token}/api/photos/3/image", token);

        var e = Drain(channel)!;
        Assert.Equal("GET /s/{token}/api/photos/3/image", e.Path);
        Assert.DoesNotContain(token, e.Message, StringComparison.Ordinal);
        Assert.Contains("/s/{token}", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, e.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public void FullChannel_DropsSilently_NoThrow()
    {
        var channel = new ErrorLogChannel();
        var logger = MakeLogger(channel, new FakeHttpContextAccessor(), "C");

        // Capacity is 1024; log well past it — every call must be swallowed, not thrown.
        for (int i = 0; i < 1100; i++) logger.LogError("boom {I}", i);

        var drained = 0;
        while (channel.Reader.TryRead(out _)) drained++;
        Assert.Equal(1024, drained); // DropWrite kept the earliest 1024, dropped the rest
    }
}
