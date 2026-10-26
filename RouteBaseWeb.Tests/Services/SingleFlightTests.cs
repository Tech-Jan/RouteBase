using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Characterization tests (CA-03) for <see cref="SingleFlight"/> — the process-wide coalescing
/// primitive. No sleeps: producers gate on <see cref="TaskCompletionSource"/> and joins are
/// deterministic because <c>RunAsync</c> runs <c>AddWaiter</c> synchronously before returning its
/// task (so a returned task == "this waiter has already joined the flight").
/// Tests 1-7 pin the pre-CA-02 behavior and must pass on unmodified code; test 8 lands with CA-02.
/// </summary>
public class SingleFlightTests
{
    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task RunAsync_ConcurrentSameKey_ProducerRunsOnce()
    {
        var sf = new SingleFlight();
        var gate = NewGate();
        var count = 0;
        var result = new object();
        Func<CancellationToken, Task<object>> producer = async _ =>
        {
            Interlocked.Increment(ref count);
            await gate.Task;
            return result;
        };

        var t1 = sf.RunAsync("k", producer, CancellationToken.None);
        var t2 = sf.RunAsync("k", producer, CancellationToken.None); // joins the same flight

        gate.SetResult();
        var r1 = await t1;
        var r2 = await t2;

        Assert.Same(r1, r2);
        Assert.Same(result, r1);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RunAsync_DifferentKeys_ProducersRunIndependently()
    {
        var sf = new SingleFlight();
        var gate = NewGate();
        var count = 0;
        Func<CancellationToken, Task<object>> producer = async _ =>
        {
            Interlocked.Increment(ref count);
            await gate.Task;
            return new object();
        };

        var ta = sf.RunAsync("a", producer, CancellationToken.None);
        var tb = sf.RunAsync("b", producer, CancellationToken.None);

        gate.SetResult();
        await ta;
        await tb;

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task RunAsync_AfterCompletion_RunsProducerAgain()
    {
        var sf = new SingleFlight();
        var count = 0;
        Func<CancellationToken, Task<object>> producer = async _ =>
        {
            Interlocked.Increment(ref count);
            await Task.Yield();
            return new object();
        };

        await sf.RunAsync("k", producer, CancellationToken.None);
        await sf.RunAsync("k", producer, CancellationToken.None); // no caching → runs again

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task RunAsync_OneWaiterCancels_OtherStillGetsResult()
    {
        var sf = new SingleFlight();
        var gate = NewGate();
        var started = NewGate();
        var count = 0;
        var result = new object();
        CancellationToken producerToken = default;
        Func<CancellationToken, Task<object>> producer = async ct =>
        {
            Interlocked.Increment(ref count);
            producerToken = ct;
            started.SetResult();
            await gate.Task;
            return result;
        };

        using var cts1 = new CancellationTokenSource();
        var t1 = sf.RunAsync("k", producer, cts1.Token);        // creates flight, 1 waiter
        await started.Task;
        var t2 = sf.RunAsync("k", producer, CancellationToken.None); // joins, 2 waiters

        cts1.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1);
        Assert.False(producerToken.IsCancellationRequested); // one waiter remains → shared work lives

        gate.SetResult();
        var r2 = await t2;

        Assert.Same(result, r2);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RunAsync_LastWaiterCancels_ProducerTokenCanceled()
    {
        var sf = new SingleFlight();
        var started = NewGate();
        var producerCancelled = NewGate();
        Func<CancellationToken, Task<int>> producer = async ct =>
        {
            ct.Register(() => producerCancelled.TrySetResult());
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        };

        using var cts = new CancellationTokenSource();
        var t = sf.RunAsync("k", producer, cts.Token);
        await started.Task;

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
        await producerCancelled.Task; // completing proves the producer's own token was canceled
    }

    [Fact]
    public async Task RunAsync_ProducerThrows_AllWaitersSeeException_AndNextCallRetries()
    {
        var sf = new SingleFlight();
        var gate = NewGate();
        var count = 0;
        Func<CancellationToken, Task<object>> producer = async _ =>
        {
            Interlocked.Increment(ref count);
            await gate.Task;
            throw new InvalidOperationException("boom");
        };

        var t1 = sf.RunAsync("k", producer, CancellationToken.None);
        var t2 = sf.RunAsync("k", producer, CancellationToken.None); // joins same flight

        gate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => t1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => t2);
        Assert.Equal(1, count); // both saw the single run's failure

        // Failure is not sticky: a fresh call re-runs the producer (gate already released → throws again).
        await Assert.ThrowsAsync<InvalidOperationException>(() => sf.RunAsync("k", producer, CancellationToken.None));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task RunAsync_PreCanceledToken_ThrowsWithoutRunningProducer()
    {
        var sf = new SingleFlight();
        var count = 0;
        Func<CancellationToken, Task<object>> producer = _ =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(new object());
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sf.RunAsync("k", producer, cts.Token));
        Assert.Equal(0, count);
    }

    // Test 8 (post-CA-02): a new caller arriving after a flight was abandoned must get a fresh
    // run, never inherit the dead flight's cancellation.
    [Fact]
    public async Task RunAsync_JoinAfterAbandonment_GetsFreshRun()
    {
        var sf = new SingleFlight();
        var started1 = NewGate();
        var gate2 = NewGate();
        var count = 0;
        var result = new object();
        Func<CancellationToken, Task<object>> producer = async ct =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                started1.SetResult();
                await Task.Delay(Timeout.Infinite, ct); // canceled when the lone waiter abandons
                return null!;
            }
            await gate2.Task;
            return result;
        };

        using var cts1 = new CancellationTokenSource();
        var t1 = sf.RunAsync("k", producer, cts1.Token);
        await started1.Task;

        cts1.Cancel(); // lone waiter → flight abandoned + producer token canceled
        // Awaiting the throw guarantees RemoveWaiter (finally) ran, so _cancelled is set before t2.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1);

        var t2 = sf.RunAsync("k", producer, CancellationToken.None);
        gate2.SetResult();
        var r2 = await t2; // must succeed, not surface the dead flight's cancellation

        Assert.Same(result, r2);
        Assert.Equal(2, count);
    }

    // Regression: a caller arriving while the WINNER IS STILL INSIDE Start() must join that flight.
    // The producer's synchronous prefix (ContentTrackReader's CreateScope + GetRequiredService, say)
    // runs inside Start, and the flight is published to the map before Start is called — so this
    // window is reachable in production. It used to surface "Start was not called" out to a 500,
    // because the flight had no task until Start returned. A flight now owns its task from
    // construction, so there is no such state: the joiner coalesces and the producer runs ONCE.
    [Fact]
    public async Task RunAsync_JoinWhileWinnerIsInsideStart_CoalescesWithoutThrowing()
    {
        var sf = new SingleFlight();
        var inPrefix = NewGate();
        var releasePrefix = NewGate();
        var finish = NewGate();
        var count = 0;
        var result = new object();

        Func<CancellationToken, Task<object>> producer = async _ =>
        {
            Interlocked.Increment(ref count);
            inPrefix.SetResult();
            // Blocking BEFORE the first await keeps the winner inside Start(), holding the window open.
            releasePrefix.Task.GetAwaiter().GetResult();
            await finish.Task;
            return result;
        };

        var winner = Task.Run(() => sf.RunAsync("k", producer, CancellationToken.None));
        await inPrefix.Task; // flight is published; the winner has not left Start()

        // Deterministic per this file's header: RunAsync joins synchronously, so by the time the
        // call returns a task, this waiter is already attached to the winner's flight.
        var joiner = sf.RunAsync("k", producer, CancellationToken.None);

        releasePrefix.SetResult();
        finish.SetResult();

        Assert.Same(result, await winner);
        Assert.Same(result, await joiner);
        Assert.Equal(1, count); // joined the in-flight run — no second producer
    }
}
