using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Queue;
using RouteBaseWeb.Services.Segments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// CR5-01 C1 pins: worker cancel semantics the handler extraction (I5.1) must preserve —
/// cancelled-while-queued cleanup per op type (staged-batch delete for Track/Photo import,
/// heatmap pending-row demote via IHeatmapLifecycle.FailPendingAsync) and user-cancel mid-run stamping
/// "Cancelled by user" on the owning progress class ONLY.
/// </summary>
// Serial with the other real-staging tests: stages a real batch under the shared routebase-import
// root, which TrackImportStagingTests' CleanupOrphans test wipes wholesale.
[Collection("TrackImportRealStagingSerial")]
public sealed class WorkerCancelSemanticsTests
{
    private sealed record Fixture(
        OperationQueue Queue, ActivityImportProgress Import, SegmentAnalysisProgress Analysis,
        HeatmapProgress Heatmap, PhotoImportProgress Photo);

    private static Fixture NewFixture() => new(
        new OperationQueue(TimeProvider.System), new ActivityImportProgress(TimeProvider.System), new SegmentAnalysisProgress(TimeProvider.System),
        new HeatmapProgress(TimeProvider.System), new PhotoImportProgress(TimeProvider.System));

    /// <summary>Builds the worker with the full six-handler registry, mirroring Program.cs DI.</summary>
    private static OperationQueueWorker CreateWorker(Fixture f, IServiceProvider provider)
    {
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var handlers = new IOperationHandler[]
        {
            new ActivityImportHandler(scopeFactory, f.Import, NullLogger<ActivityImportHandler>.Instance),
            new SegmentAnalysisHandler(scopeFactory, f.Analysis, NullLogger<SegmentAnalysisHandler>.Instance),
            new SingleSegmentAnalysisHandler(scopeFactory, f.Analysis, NullLogger<SingleSegmentAnalysisHandler>.Instance),
            new TrackImportHandler(scopeFactory, f.Import, NullLogger<TrackImportHandler>.Instance),
            new HeatmapGenerationHandler(scopeFactory, f.Heatmap, NullLogger<HeatmapGenerationHandler>.Instance),
            new PhotoImportHandler(scopeFactory, f.Photo, NullLogger<PhotoImportHandler>.Instance),
        };
        return new OperationQueueWorker(
            f.Queue, handlers,
            NullLogger<OperationQueueWorker>.Instance);
    }

    /// <summary>Polls a condition to a 5 s deadline (no Thread.Sleep). Fails the test on timeout.</summary>
    private static async Task PollAsync(Func<bool> condition, string timeoutMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, timeoutMessage);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task CancelledWhileQueued_TrackImport_DeletesStagedBatch()
    {
        var staging = new TrackImportStaging(NullLogger<TrackImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch("u", useFilenameAsId: false);
        try
        {
            var services = new ServiceCollection().AddSingleton(TimeProvider.System);
            services.AddSingleton(staging);
            await using var provider = services.BuildServiceProvider();

            var f = NewFixture();
            Assert.True(f.Queue.TryEnqueue(new OperationQueue.WorkItem
            {
                Type = OperationQueue.OperationType.TrackImport,
                UserId = "u",
                Payload = new TrackImportPayload("u", batchId),
            }));
            f.Queue.CancelForUser("u"); // cancelled while still queued

            var worker = CreateWorker(f, provider);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await PollAsync(() => !staging.BatchExists("u", batchId),
                    "cancelled-while-queued track import did not delete its staged batch");
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            staging.Delete("u", batchId); // idempotent safety net
        }
    }

    [Fact]
    public async Task CancelledWhileQueued_PhotoImport_DeletesStagedBatch()
    {
        var staging = new PhotoImportStaging(NullLogger<PhotoImportStaging>.Instance, TimeProvider.System);
        var batchId = staging.BeginBatch("u", previewMaxEdge: 1600);
        try
        {
            var services = new ServiceCollection().AddSingleton(TimeProvider.System);
            services.AddSingleton(staging);
            await using var provider = services.BuildServiceProvider();

            var f = NewFixture();
            Assert.True(f.Queue.TryEnqueue(new OperationQueue.WorkItem
            {
                Type = OperationQueue.OperationType.PhotoImport,
                UserId = "u",
                Payload = new PhotoImportPayload("u", batchId),
            }));
            f.Queue.CancelForUser("u");

            var worker = CreateWorker(f, provider);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await PollAsync(() => !staging.BatchExists("u", batchId),
                    "cancelled-while-queued photo import did not delete its staged batch");
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            staging.Delete("u", batchId);
        }
    }

    [Fact]
    public async Task CancelledWhileQueued_HeatmapGeneration_DemotesPendingRow()
    {
        var heatmaps = new RecordingHeatmapLifecycle();
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddSingleton<IHeatmapLifecycle>(heatmaps);
        await using var provider = services.BuildServiceProvider();

        var f = NewFixture();
        Assert.True(f.Queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type = OperationQueue.OperationType.HeatmapGeneration,
            UserId = "u",
            Payload = new HeatmapGenerationPayload("u", 7),
        }));
        f.Queue.CancelForUser("u");

        var worker = CreateWorker(f, provider);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var (segmentId, error) = await heatmaps.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new SegmentId(7), segmentId);
            Assert.Equal("Cancelled by user", error);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task UserCancelMidRun_FailsOnlyOwningProgress()
    {
        var generation = new BlockingHeatmapService();
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentUserService, StubCurrentUserService>();
        services.AddSingleton<IHeatmapGenerationService>(generation);
        await using var provider = services.BuildServiceProvider();

        var f = NewFixture();
        Assert.True(f.Queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type = OperationQueue.OperationType.HeatmapGeneration,
            UserId = "u",
            Payload = new HeatmapGenerationPayload("u", 7),
        }));

        var worker = CreateWorker(f, provider);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await generation.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Queue.CancelForUser("u");

            await PollAsync(() => f.Heatmap.GetStatus("u").Phase == "Failed",
                "mid-run cancel did not fail the owning heatmap progress");
            Assert.Equal("Cancelled by user", f.Heatmap.GetStatus("u").Error);

            // The other progress classes must stay untouched (no "Cancelled by user" bleed).
            Assert.Equal("", f.Import.GetStatus("u").Phase);
            Assert.Equal("", f.Analysis.GetStatus("u").Phase);
            Assert.Equal("", f.Photo.GetStatus("u").Phase);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ── Stubs ──────────────────────────────────────────────────────────────

    private sealed class StubCurrentUserService : ICurrentUserService
    {
        private string _userId = "";
        public string GetUserId() => _userId;
        public string? TryGetUserId() => _userId;
        public string? GetDisplayName() => null;
        public bool IsAdmin() => false;
        public void SetUserIdOverride(string userId) => _userId = userId;
        public IDisposable BeginUserScope(string userId)
        {
            var previous = _userId;
            _userId = userId;
            return new Restore(this, previous);
        }
        private sealed class Restore(StubCurrentUserService s, string previous) : IDisposable
        {
            public void Dispose() => s._userId = previous;
        }
    }

    /// <summary>Signals GenerateAsync entry, then blocks until the operation token is cancelled.</summary>
    private sealed class BlockingHeatmapService : IHeatmapGenerationService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task GenerateAsync(SegmentId segmentId, string userId, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    /// <summary>Records the FailPendingAsync call the cancelled-while-queued arm must make (B2).</summary>
    private sealed class RecordingHeatmapLifecycle : IHeatmapLifecycle
    {
        public readonly TaskCompletionSource<(SegmentId SegmentId, string Error)> Failed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FailPendingAsync(SegmentId segmentId, string error)
        {
            Failed.TrySetResult((segmentId, error));
            return Task.CompletedTask;
        }

        public Task<HeatmapAttachResult> AttachAsync(SegmentId segmentId, IEnumerable<string> activityTypes) => throw new NotImplementedException();
        public Task<Heatmap?> GetVisibleAsync(SegmentId segmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task WithdrawAsync(SegmentId segmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkStaleAsync(SegmentId segmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkStaleForTrackAsync(TrackId trackId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> CaptureIdForSegmentAsync(SegmentId segmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public void PurgeTiles(int heatmapId) => throw new NotImplementedException();
    }
}
