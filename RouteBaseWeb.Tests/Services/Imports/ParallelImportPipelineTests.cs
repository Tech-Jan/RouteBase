using RouteBaseWeb.Services.Imports;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Imports;

/// <summary>
/// Direct pins for the <see cref="ParallelImportPipeline"/> skeleton (CR5-03). The invariants
/// asserted here ARE the perf/correctness contract both import orchestrators rely on:
/// capacity 8, DoP 4, exactly one (serial) persister, and the IE-07 whole-body persister try
/// (a throwing persist delegate is routed to onPersistError and the loop continues — it must
/// never fault the persister, fill the bounded channel, and wedge the fetcher).
/// </summary>
public class ParallelImportPipelineTests
{
    private static readonly TimeSpan NoHang = TimeSpan.FromSeconds(30);

    [Fact]
    public void Defaults_PinCapacity8_AndMaxConcurrency4()
    {
        var ps = typeof(ParallelImportPipeline).GetMethod(nameof(ParallelImportPipeline.RunAsync))!
            .GetParameters().ToDictionary(p => p.Name!, StringComparer.Ordinal);

        Assert.Equal(4, ps["maxConcurrency"].DefaultValue);
        Assert.Equal(8, ps["capacity"].DefaultValue);
    }

    [Fact]
    public async Task PersistThrowOnEveryItem_RoutesToOnPersistError_AndNeverWedges()
    {
        // (a) wedge pin: >8 prepared items queued, persist always throws — every item must still
        // flow through onPersistError and RunAsync must complete (no bounded-channel deadlock).
        int persistAttempts = 0, errors = 0;

        await ParallelImportPipeline.RunAsync<int, string>(
            Enumerable.Range(1, 30),
            prepareAsync: (i, _) => ValueTask.FromResult<string?>($"item-{i}"),
            persistAsync: _ =>
            {
                Interlocked.Increment(ref persistAttempts);
                throw new InvalidOperationException("boom");
            },
            onPersistError: (_, _) => Interlocked.Increment(ref errors),
            CancellationToken.None).WaitAsync(NoHang);

        Assert.Equal(30, persistAttempts);
        Assert.Equal(30, errors);
    }

    [Fact]
    public async Task PrepareReturningNull_SkipsPersist_OthersPersistExactlyOnce()
    {
        // (b)+(c): a null prepare result (caller-handled failure) never reaches the persister;
        // every non-null item is persisted exactly once.
        var persisted = new List<string>();

        await ParallelImportPipeline.RunAsync<int, string>(
            Enumerable.Range(1, 20),
            prepareAsync: (i, _) => ValueTask.FromResult<string?>(i % 2 == 0 ? null : $"item-{i}"),
            persistAsync: p => { lock (persisted) persisted.Add(p); return Task.CompletedTask; },
            onPersistError: (_, _) => { },
            CancellationToken.None).WaitAsync(NoHang);

        Assert.Equal(
            Enumerable.Range(1, 20).Where(i => i % 2 == 1).Select(i => $"item-{i}").OrderBy(s => s, StringComparer.Ordinal),
            persisted.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PreCanceledToken_ThrowsOperationCanceled()
    {
        // (d) cancellation drains cleanly: a canceled token surfaces as OCE, never a hang.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ParallelImportPipeline.RunAsync<int, string>(
                Enumerable.Range(1, 5),
                prepareAsync: (i, _) => ValueTask.FromResult<string?>($"item-{i}"),
                persistAsync: _ => Task.CompletedTask,
                onPersistError: (_, _) => { },
                cts.Token).WaitAsync(NoHang));
    }

    [Fact]
    public async Task ExpectedFetchCancellation_IsSwallowed_AndPipelineDrains()
    {
        // Strava rate-limit shape: the fetch-only token cancels mid-run; with
        // isExpectedFetchCancellation → true the run completes instead of throwing
        // (the caller rethrows its own domain exception afterwards).
        //
        // CONC-01 (test determinism — same intent, no weakening): the old body returned an
        // already-completed ValueTask and cancelled on the 3rd prepare, then asserted `prepared < 30`.
        // Parallel.ForEachAsync cancellation is cooperative (the token is only re-checked when a worker
        // pulls the NEXT element), so with a never-suspending body the assert became a RACE between
        // cancel-visibility and workers ripping through all 30 synchronous bodies — green when idle, red
        // under concurrent load (all 30 pulled before the cancel is observed → prepared == 30). This
        // rewrite parks every fetcher on a gate so exactly DoP(4) prepares are in flight, cancels while
        // they are parked, then releases: the resuming workers observe the cancelled token before pulling
        // element 5, so `prepared` is pinned at DoP instead of racing. Both intent legs are still
        // exercised — the loop was curtailed (== 4, i.e. < 30) AND the expected cancel was swallowed
        // (WaitAsync returns instead of throwing). Mirrors the reach-the-plateau-then-assert technique of
        // BlockedPersister_StallsFetchAtCapacityPlusHeldPlusDoP below.
        using var fetchCts = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int prepared = 0;

        var run = ParallelImportPipeline.RunAsync<int, string>(
            Enumerable.Range(1, 30),
            prepareAsync: async (i, _) =>       // park on release, NOT on the token
            {
                Interlocked.Increment(ref prepared);
                await release.Task;
                return $"item-{i}";
            },
            persistAsync: _ => Task.CompletedTask,
            onPersistError: (_, _) => { },
            CancellationToken.None,
            fetchCancellation: fetchCts.Token,
            isExpectedFetchCancellation: () => true);

        // Wait until all DoP=4 fetchers are parked at the gate (steady state). DoP=4 forbids a 5th
        // concurrent body and no slot frees until release, so `prepared` cannot exceed 4 here.
        var deadline = DateTime.UtcNow + NoHang;
        while (Volatile.Read(ref prepared) < 4 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(4, Volatile.Read(ref prepared)); // DoP parked; none escaped past the gate

        fetchCts.Cancel();   // request cancel while every worker is parked
        release.SetResult(); // resume → each re-checks the token on the next pull and the loop stops

        await run.WaitAsync(NoHang);                   // expected-cancel swallowed → completes, no throw
        Assert.Equal(4, Volatile.Read(ref prepared));  // deterministic: nothing prepared after the cancel
    }

    [Fact]
    public async Task Persister_IsExactlyOne_NeverConcurrent()
    {
        // Serial-persister invariant: onActivityImported / completion callbacks run single-threaded
        // (the post-batch incremental match depends on it).
        int inPersist = 0, persistedCount = 0;
        bool sawConcurrent = false;

        await ParallelImportPipeline.RunAsync<int, string>(
            Enumerable.Range(1, 20),
            prepareAsync: (i, _) => ValueTask.FromResult<string?>($"item-{i}"),
            persistAsync: async _ =>
            {
                if (Interlocked.Increment(ref inPersist) > 1) sawConcurrent = true;
                await Task.Delay(5);
                Interlocked.Increment(ref persistedCount);
                Interlocked.Decrement(ref inPersist);
            },
            onPersistError: (_, _) => { },
            CancellationToken.None).WaitAsync(NoHang);

        Assert.False(sawConcurrent);
        Assert.Equal(20, persistedCount);
    }

    [Fact]
    public async Task BlockedPersister_StallsFetchAtCapacityPlusHeldPlusDoP()
    {
        // Behavioral capacity/DoP pin: with the persister blocked, prepared items stall at exactly
        // capacity(8) + 1 held by the persister + DoP(4) writers blocked in WriteAsync = 13.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int prepared = 0, concurrentPrepares = 0, maxConcurrentPrepares = 0, persistedCount = 0;

        var run = ParallelImportPipeline.RunAsync<int, string>(
            Enumerable.Range(1, 30),
            prepareAsync: (i, _) =>
            {
                var cur = Interlocked.Increment(ref concurrentPrepares);
                int seen;
                while (cur > (seen = Volatile.Read(ref maxConcurrentPrepares)))
                    Interlocked.CompareExchange(ref maxConcurrentPrepares, cur, seen);
                Interlocked.Increment(ref prepared);
                Interlocked.Decrement(ref concurrentPrepares);
                return ValueTask.FromResult<string?>($"item-{i}");
            },
            persistAsync: async _ =>
            {
                await release.Task;
                Interlocked.Increment(ref persistedCount);
            },
            onPersistError: (_, _) => { },
            CancellationToken.None);

        // Wait until the pipeline is fully stalled at the invariant, then confirm it holds.
        var deadline = DateTime.UtcNow + NoHang;
        while (Volatile.Read(ref prepared) < 13 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(13, Volatile.Read(ref prepared));
        await Task.Delay(250);
        Assert.Equal(13, Volatile.Read(ref prepared)); // no 14th prepare while blocked

        Assert.InRange(maxConcurrentPrepares, 1, 4);   // DoP never exceeds 4

        release.SetResult();
        await run.WaitAsync(NoHang);
        Assert.Equal(30, persistedCount);
    }
}
