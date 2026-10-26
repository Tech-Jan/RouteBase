using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>Queue glue for the derive pass (A2 §5.2): type discriminator, impersonated-scope run under the
/// analysis-progress lifecycle, and swallow-and-fail on a service error (no rethrow).</summary>
public class TrackEditDeriveHandlerTests
{
    private const string User = "u1";

    private static (TrackEditDeriveHandler Handler, FakeSegmentAnalysis Seg, SegmentAnalysisProgress Progress) Build(
        FakeSegmentAnalysis? seg = null)
    {
        seg ??= new FakeSegmentAnalysis();
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddSingleton<ICurrentUserService>(new TestCurrentUserService());
        services.AddSingleton<ISegmentAnalysis>(seg);
        services.AddSingleton<IPhotoCommandService>(new FakePhotoCommand());
        services.AddSingleton<UserCacheInvalidator>();
        services.AddSingleton<ILogger<TrackEditDeriveService>>(NullLogger<TrackEditDeriveService>.Instance);
        services.AddScoped<TrackEditDeriveService>();
        var provider = services.BuildServiceProvider();

        var progress = new SegmentAnalysisProgress(TimeProvider.System);
        var handler = new TrackEditDeriveHandler(
            provider.GetRequiredService<IServiceScopeFactory>(), progress, NullLogger<TrackEditDeriveHandler>.Instance);
        return (handler, seg, progress);
    }

    [Fact]
    public void Type_IsTrackEditDerive()
    {
        var (handler, _, _) = Build();
        Assert.Equal(OperationQueue.OperationType.TrackEditDerive, handler.Type);
    }

    [Fact]
    public async Task RunAsync_RunsService_CompletesProgress()
    {
        var (handler, seg, progress) = Build();

        await handler.RunAsync(new TrackEditDerivePayload(User, 5, [7], [], []), default);

        var status = progress.GetStatus(User);
        Assert.False(status.IsRunning);
        Assert.Null(status.Error);
        Assert.NotNull(status.CompletedAt);
        Assert.Contains("remove:7:u1", seg.Calls, StringComparer.Ordinal); // scope user == payload user
    }

    [Fact]
    public async Task RunAsync_ServiceThrows_FailsProgress_NoRethrow()
    {
        var (handler, _, progress) = Build(new FakeSegmentAnalysis { ThrowOnThreshold = true });

        await handler.RunAsync(new TrackEditDerivePayload(User, 5, [7], [], []), default); // must not throw

        Assert.NotNull(progress.GetStatus(User).Error);
    }
}
