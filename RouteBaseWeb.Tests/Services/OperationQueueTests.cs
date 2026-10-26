using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Queue;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Queue;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Per-user cancellation tests for <see cref="OperationQueue"/> (IQ-01). Plain class — no host,
/// no DbContext; exercises the in-memory pending mirror and reservation set directly.
/// </summary>
public class OperationQueueTests
{
    private static OperationQueue.WorkItem Item(string userId) => new()
    {
        Type   = OperationQueue.OperationType.SegmentAnalysis,
        UserId = userId,
    };

    private static OperationQueue.WorkItem Item(string userId, OperationQueue.OperationType type) => new()
    {
        Type   = type,
        UserId = userId,
    };

    [Fact]
    public void CancelForUser_CancelsOnlyThatUsersPendingItems()
    {
        var queue = new OperationQueue(TimeProvider.System);
        var a1 = Item("A");
        var b1 = Item("B");
        var a2 = Item("A");
        Assert.True(queue.TryEnqueue(a1));
        Assert.True(queue.TryEnqueue(b1));
        Assert.True(queue.TryEnqueue(a2));

        queue.CancelForUser("A");

        Assert.True(a1.Cts.IsCancellationRequested);
        Assert.True(a2.Cts.IsCancellationRequested);
        Assert.False(b1.Cts.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelForUser_CancelsRunningItemOfThatUserOnly()
    {
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryEnqueue(Item("A")));

        // Dequeue one item so it becomes _current (the loop body ran up to the yield).
        var e = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        var running = e.Current;

        queue.CancelForUser("other");
        Assert.False(running.Cts.IsCancellationRequested);

        queue.CancelForUser(running.UserId!);
        Assert.True(running.Cts.IsCancellationRequested);

        await e.DisposeAsync();
    }

    [Fact]
    public async Task CancelForUser_AfterOperationCompleted_DoesNotThrow()
    {
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryEnqueue(Item("A")));

        // Dequeue so the item becomes _current, then hand it back as completed. The queue disposes
        // its CTS and clears _current under the lock (QW-02) — a subsequent racing cancel must not
        // call Cancel() on the disposed CTS.
        var e = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        var running = e.Current;

        queue.NotifyOperationCompleted(running);

        var ex = Record.Exception(() => queue.CancelForUser("A"));
        Assert.Null(ex);
        Assert.Equal(0, queue.PendingCountFor("A"));

        await e.DisposeAsync();
    }

    [Fact]
    public void TryEnqueue_FullChannel_ReturnsFalseAndLeavesPendingUnchanged()
    {
        var queue = new OperationQueue(TimeProvider.System);
        for (int i = 0; i < 100; i++)   // fill to bounded-channel capacity (100)
            Assert.True(queue.TryEnqueue(Item("A")));

        Assert.False(queue.TryEnqueue(Item("A"))); // 101st: channel full, write fails
        Assert.Equal(100, queue.PendingCountFor("A")); // failed enqueue undoes its pending mirror add (QW-03)
    }

    [Fact]
    public async Task ImportReservation_ReleasedWhenProviderUnknown()
    {
        // Drives the real worker with a real queue + progress classes, but no IActivitySyncService
        // registered → RunImportAsync hits the pre-try "unknown provider" early return. QW-04 moved
        // the release to ExecuteAsync's finally, so the reservation must be freed even on that path.
        var queue = new OperationQueue(TimeProvider.System);
        var importProgress = new ActivityImportProgress(TimeProvider.System);

        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentUserService, StubCurrentUserService>();
        services.AddScoped<IActivityImportService, StubImportService>();
        // Deliberately NO IActivitySyncService registered.
        await using var provider = services.BuildServiceProvider();

        var worker = new OperationQueueWorker(
            queue,
            new IOperationHandler[]
            {
                new ActivityImportHandler(provider.GetRequiredService<IServiceScopeFactory>(),
                    importProgress, NullLogger<ActivityImportHandler>.Instance),
            },
            NullLogger<OperationQueueWorker>.Instance);

        Assert.True(queue.TryReserveExclusiveSlot("u"));
        Assert.True(queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type    = OperationQueue.OperationType.ActivityImport,
            UserId  = "u",
            Payload = new ActivityImportPayload("u", "unknown", null),
        }));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await PollAsync(() => importProgress.GetStatus("u").Phase == "Failed",
                "worker did not fail the unknown-provider item in time");
            Assert.Equal("Unknown provider: unknown", importProgress.GetStatus("u").Error);

            // Release happens in the loop finally, just after RunImportAsync returns — poll for it.
            await PollAsync(() => queue.TryReserveExclusiveSlot("u"),
                "import reservation was not released after the unknown-provider failure");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Polls an async condition to a 5 s deadline (no Thread.Sleep). Fails the test on timeout.</summary>
    private static async Task PollAsync(Func<bool> condition, string timeoutMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, timeoutMessage);
            await Task.Delay(20);
        }
    }

    /// <summary>Minimal handler for worker-loop tests that need no real operation body.</summary>
    private sealed class StubHandler(OperationQueue.OperationType type) : IOperationHandler
    {
        public OperationQueue.OperationType Type => type;
        public Task RunAsync(OperationPayload payload, CancellationToken ct) => Task.CompletedTask;
        public void FailProgress(string userId, string message) { }
    }

    /// <summary>Import-slot-holding handler that signals when it starts and then blocks until released,
    /// deliberately ignoring the cancellation token — so the worker cannot reach its finally (and its
    /// slot release) until the test lets it, isolating CancelForUser's release behaviour.</summary>
    private sealed class GatedImportHandler(TaskCompletionSource started, Task release) : IOperationHandler
    {
        public OperationQueue.OperationType Type => OperationQueue.OperationType.ActivityImport;
        public bool HoldsImportSlot => true;
        public async Task RunAsync(OperationPayload payload, CancellationToken ct)
        {
            started.SetResult();
            await release;
        }
        public void FailProgress(string userId, string message) { }
    }

    [Fact]
    public async Task NonSlotHolderCompletion_DoesNotReleaseExclusiveSlot()
    {
        // The user holds the import slot (their import is queued/running) while an unrelated op of
        // theirs completes. The worker's central release must be type-guarded: an unconditional
        // release freed the slot here, letting a second import enqueue against the shared per-user
        // import progress state (B5).
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryReserveExclusiveSlot("u"));
        Assert.True(queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type    = OperationQueue.OperationType.SegmentAnalysis,
            UserId  = "u",
            Payload = new SegmentAnalysisPayload("u"),
        }));

        var worker = new OperationQueueWorker(
            queue,
            new IOperationHandler[] { new StubHandler(OperationQueue.OperationType.SegmentAnalysis) },
            NullLogger<OperationQueueWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // ProcessedCount increments after the finally's release call, so at 1 the buggy release
            // would already have happened.
            await PollAsync(() => queue.ProcessedCount == 1,
                "worker did not complete the analysis item in time");
            Assert.False(queue.TryReserveExclusiveSlot("u")); // slot still held by the queued import
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void HoldsImportSlot_MapsExactlyTheReservingTypes()
    {
        // Pins the type→slot capability to the three op types whose controllers call
        // TryReserveExclusiveSlot (GPX, Strava, photo) — now an IOperationHandler.HoldsImportSlot
        // capability (arch finding 1) instead of a queue-layer type switch. A new handler defaults
        // to NOT holding the slot; opt in only if its controller reserves.
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        IOperationHandler[] handlers =
        {
            new ActivityImportHandler(scopeFactory, new ActivityImportProgress(TimeProvider.System), NullLogger<ActivityImportHandler>.Instance),
            new TrackImportHandler(scopeFactory, new ActivityImportProgress(TimeProvider.System), NullLogger<TrackImportHandler>.Instance),
            new SegmentAnalysisHandler(scopeFactory, new SegmentAnalysisProgress(TimeProvider.System), NullLogger<SegmentAnalysisHandler>.Instance),
            new SingleSegmentAnalysisHandler(scopeFactory, new SegmentAnalysisProgress(TimeProvider.System), NullLogger<SingleSegmentAnalysisHandler>.Instance),
            new HeatmapGenerationHandler(scopeFactory, new HeatmapProgress(TimeProvider.System), NullLogger<HeatmapGenerationHandler>.Instance),
            new RouteBaseWeb.Services.Photos.PhotoImportHandler(scopeFactory, new RouteBaseWeb.Services.Photos.PhotoImportProgress(TimeProvider.System), NullLogger<RouteBaseWeb.Services.Photos.PhotoImportHandler>.Instance),
            new RouteBaseWeb.Services.TrackEdit.TrackEditDeriveHandler(scopeFactory, new SegmentAnalysisProgress(TimeProvider.System), NullLogger<RouteBaseWeb.Services.TrackEdit.TrackEditDeriveHandler>.Instance),
        };

        var holding = handlers.Where(h => h.HoldsImportSlot).Select(h => h.Type).ToArray();
        Assert.Equal(new[]
        {
            OperationQueue.OperationType.ActivityImport,
            OperationQueue.OperationType.TrackImport,
            OperationQueue.OperationType.PhotoImport,
        }, holding);
    }

    [Fact]
    public void CancelForUser_DoesNotReleaseTheImportSlot()
    {
        // Intent-changed (CONC-P2): cancel used to eagerly free the reservation. That was the defect —
        // a re-import could reserve the just-freed slot, then the worker's later cleanup of the cancelled
        // import freed the re-import's reservation, running two imports at once. The worker is now the
        // single authoritative releaser; cancel leaves the reservation held until the item drains.
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryReserveExclusiveSlot("A"));
        Assert.True(queue.TryReserveExclusiveSlot("B"));

        queue.CancelForUser("A");

        Assert.False(queue.TryReserveExclusiveSlot("A"));  // NOT freed by cancel — worker frees it on drain
        Assert.False(queue.TryReserveExclusiveSlot("B"));  // other users untouched, as before
    }

    [Fact]
    public async Task CancelForUser_WhileImportRunning_DoesNotFreeSlotForAReimport()
    {
        // The cancel-then-reimport double-release (CONC-P2) end-to-end. The handler blocks (ignoring the
        // cancel token) so the worker cannot reach its own release yet — the ONLY thing that could free
        // the slot while the import is still draining is an eager release in CancelForUser. If it fired, a
        // fresh re-import would reserve the very slot the worker's cleanup then frees, running two imports
        // against the shared per-user import progress state at once.
        var queue = new OperationQueue(TimeProvider.System);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(queue.TryReserveExclusiveSlot("u"));
        Assert.True(queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type    = OperationQueue.OperationType.ActivityImport,
            UserId  = "u",
            Payload = new ActivityImportPayload("u", "strava", null),
        }));

        var worker = new OperationQueueWorker(
            queue,
            new IOperationHandler[] { new GatedImportHandler(started, release.Task) },
            NullLogger<OperationQueueWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // import running, blocked in the handler

            queue.CancelForUser("u");
            // The handler is still blocked, so the worker has NOT released — a re-import must be refused.
            Assert.False(queue.TryReserveExclusiveSlot("u"));

            release.SetResult();                                    // let the import finish
            await PollAsync(() => queue.TryReserveExclusiveSlot("u"),
                "worker did not release the slot after the import drained");
        }
        finally
        {
            release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void TryReserveExclusiveSlot_HeldReservation_Rejected()
    {
        var queue = new OperationQueue(TimeProvider.System);
        var t = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(queue.TryReserveExclusiveSlot("u", t));
        Assert.False(queue.TryReserveExclusiveSlot("u", t.AddHours(1))); // within TTL → still held
    }

    [Fact]
    public void TryReserveExclusiveSlot_StaleReservation_Reclaimed()
    {
        var queue = new OperationQueue(TimeProvider.System);
        var t = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(queue.TryReserveExclusiveSlot("u", t));
        Assert.True(queue.TryReserveExclusiveSlot("u", t.AddHours(25))); // past 24h TTL → leaked slot reclaimed
    }

    [Fact]
    public void PendingCountFor_CountsOnlyThatUser()
    {
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryEnqueue(Item("A")));
        Assert.True(queue.TryEnqueue(Item("B")));
        Assert.True(queue.TryEnqueue(Item("A")));

        Assert.Equal(2, queue.PendingCountFor("A"));
        Assert.Equal(1, queue.PendingCountFor("B"));
        Assert.Equal(0, queue.PendingCountFor("C"));
    }

    [Fact]
    public void PendingCountFor_ExcludesCancelled()
    {
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryEnqueue(Item("A")));
        Assert.True(queue.TryEnqueue(Item("A")));

        queue.CancelForUser("A");

        Assert.Equal(0, queue.PendingCountFor("A"));
    }

    [Fact]
    public void NextPendingTypeFor_ReturnsFirstMatch()
    {
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryEnqueue(Item("B", OperationQueue.OperationType.HeatmapGeneration)));
        Assert.True(queue.TryEnqueue(Item("A", OperationQueue.OperationType.TrackImport)));
        Assert.True(queue.TryEnqueue(Item("A", OperationQueue.OperationType.SegmentAnalysis)));

        Assert.Equal(OperationQueue.OperationType.TrackImport, queue.NextPendingTypeFor("A"));
        Assert.Equal(OperationQueue.OperationType.HeatmapGeneration, queue.NextPendingTypeFor("B"));
        Assert.Null(queue.NextPendingTypeFor("C"));
    }

    [Fact]
    public async Task BurstInfoFor_SingleOp_Is1Of1()
    {
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryEnqueue(Item("A")));

        var e = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());

        Assert.Equal((1, 1), queue.BurstInfoFor("A"));

        await e.DisposeAsync();
    }

    [Fact]
    public async Task BurstInfoFor_TracksPerUserBurst()
    {
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryEnqueue(Item("A")));
        Assert.True(queue.TryEnqueue(Item("A")));
        Assert.True(queue.TryEnqueue(Item("B")));

        var e = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        var a1 = e.Current;

        Assert.Equal((1, 2), queue.BurstInfoFor("A"));
        Assert.Equal((1, 1), queue.BurstInfoFor("B"));

        queue.NotifyOperationCompleted(a1);
        Assert.True(await e.MoveNextAsync()); // a2

        Assert.Equal((2, 2), queue.BurstInfoFor("A"));

        await e.DisposeAsync();
    }

    [Fact]
    public async Task BurstInfoFor_ResetsAfterBurstCompletes()
    {
        var queue = new OperationQueue(TimeProvider.System);
        Assert.True(queue.TryEnqueue(Item("A")));
        Assert.True(queue.TryEnqueue(Item("A")));

        var e = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        queue.NotifyOperationCompleted(e.Current);
        Assert.True(await e.MoveNextAsync());
        queue.NotifyOperationCompleted(e.Current); // burst A drained → entry removed

        // A new single op starts a fresh burst → (1,1), so op-toast hides the badge again.
        Assert.True(queue.TryEnqueue(Item("A")));
        Assert.Equal((1, 1), queue.BurstInfoFor("A"));

        await e.DisposeAsync();
    }

    [Fact]
    public void BurstInfoFor_UnknownUser_Is1Of1()
    {
        var queue = new OperationQueue(TimeProvider.System);
        Assert.Equal((1, 1), queue.BurstInfoFor("nobody"));
    }

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

    // Resolved before the unknown-provider check, but none of its methods run on that path.
    private sealed class StubImportService : IActivityImportService
    {
        public Task<ExistingActivity?> FindExistingAsync(long externalId, string source, string userId) => throw new NotImplementedException();
        public Task<Activity?> ImportFromSummaryAsync(ExternalActivitySummary summary, List<ExternalGpsPoint> gpsPoints, string userId) => throw new NotImplementedException();
        public Task WriteTrackBlobAsync(TrackId trackId, List<ExternalGpsPoint> points) => throw new NotImplementedException();
        public double ComputeTotalDescent(List<ExternalGpsPoint> points) => throw new NotImplementedException();
        public Task UpsertActivityBboxAsync(TrackId trackId, IReadOnlyList<ExternalGpsPoint> points) => throw new NotImplementedException();
        public Task UpsertActivityBboxAsync(TrackId trackId) => throw new NotImplementedException();
    }
}
