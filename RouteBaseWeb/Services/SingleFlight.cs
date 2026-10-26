using System.Collections.Concurrent;

namespace RouteBaseWeb.Services;

/// <summary>
/// Process-wide single-flight coalescing: concurrent callers with the same key share one
/// execution of the producer instead of each running it independently. Used to collapse a
/// zoom storm of identical bulk-GPS queries into a single DB round-trip.
///
/// The shared producer runs under its own <see cref="CancellationTokenSource"/> that is
/// cancelled only when the *last* live waiter cancels — so one caller's abort can't kill work
/// others are still awaiting, yet a fully-abandoned call still cancels server-side.
///
/// IMPORTANT: the producer must NOT close over a request-scoped resource (e.g. a scoped
/// DbContext), because it can outlive the request that started it. Have the producer create
/// its own scope/context and write only to singletons.
/// </summary>
public sealed class SingleFlight
{
    // Boxed Flight<T>; one entry per in-flight key. Removed when the producer settles.
    private readonly ConcurrentDictionary<string, object> _flights = new(StringComparer.Ordinal);

    public async Task<T> RunAsync<T>(string key, Func<CancellationToken, Task<T>> producer, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var flight = GetOrCreate(key, producer);
            if (!flight.TryAddWaiter())
            {
                // Dying flight (last waiter abandoned it, mid-teardown) — evict and retry with a fresh one.
                _flights.TryRemove(new KeyValuePair<string, object>(key, flight));
                continue;
            }
            try
            {
                // Each waiter observes its OWN token; bailing here doesn't disturb the shared task.
                return await flight.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                flight.RemoveWaiter();
            }
        }
    }

    private Flight<T> GetOrCreate<T>(string key, Func<CancellationToken, Task<T>> producer)
    {
        while (true)
        {
            if (_flights.TryGetValue(key, out var existing))
            {
                var f = (Flight<T>)existing;
                var task = f.Task; // always present — a flight owns its task from construction

                // Don't join a flight that already failed/cancelled and is mid-teardown — evict and retry.
                if (task.IsCompleted && !task.IsCompletedSuccessfully)
                {
                    _flights.TryRemove(new KeyValuePair<string, object>(key, f));
                    continue;
                }
                return f;
            }

            var flight = new Flight<T>();
            if (_flights.TryAdd(key, flight))
            {
                // Only the winner starts the producer; the loser's un-started flight is discarded (no work leaked).
                flight.Start(producer, () => _flights.TryRemove(new KeyValuePair<string, object>(key, flight)));
                return flight;
            }
        }
    }

    private sealed class Flight<T>
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private int _waiters;
        private bool _settled;
        private bool _cancelled;

        // The flight owns its task from construction, so there is no window in which a joiner can
        // observe a published-but-unstarted flight. Assigning the task inside Start() instead left
        // exactly that window open — the producer's synchronous prefix (a DI scope + service
        // resolutions, for the readers here) runs before Start() returns, and a joiner arriving in
        // it read a null task. RunContinuationsAsynchronously so the producer's completion doesn't
        // inline every waiter's continuation onto the producer's thread.
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _tcs.Task;

        public void Start(Func<CancellationToken, Task<T>> producer, Action onSettled) =>
            _ = Execute(producer, onSettled);

        // Settle BEFORE completing the task, so a waiter can never wake to find its flight still
        // published — the next caller must get a fresh run, not join one that is already finished.
        private async Task Execute(Func<CancellationToken, Task<T>> producer, Action onSettled)
        {
            try
            {
                var value = await producer(_cts.Token).ConfigureAwait(false);
                Settle(onSettled);
                _tcs.SetResult(value);
            }
            catch (OperationCanceledException oce)
            {
                Settle(onSettled);
                _tcs.SetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                Settle(onSettled);
                _tcs.SetException(ex);
            }
        }

        private void Settle(Action onSettled)
        {
            lock (_gate) { _settled = true; }
            onSettled();
            _cts.Dispose();
        }

        /// <summary>False when the flight was already abandoned (last waiter cancelled it) — the
        /// caller must retry with a fresh flight instead of joining a dying one.</summary>
        public bool TryAddWaiter()
        {
            lock (_gate)
            {
                if (_cancelled) return false;
                _waiters++;
                return true;
            }
        }

        public void RemoveWaiter()
        {
            lock (_gate)
            {
                // Last waiter left before the producer settled → cancel the shared work.
                if (--_waiters == 0 && !_settled)
                {
                    _cancelled = true; // slam the door before cancelling: late joiners must retry, not join a dying flight
                    try { _cts.Cancel(); }
                    catch (ObjectDisposedException) { /* producer settled concurrently */ }
                }
            }
        }
    }
}
