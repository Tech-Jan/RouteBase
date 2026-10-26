using System.Threading.Channels;

namespace RouteBaseWeb.Services.Imports;

/// <summary>
/// Shared skeleton for parallel imports (CR5-03): concurrent fetchers
/// (<see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>,
/// DoP <c>maxConcurrency</c>) prepare items into a bounded channel (capacity <c>capacity</c>)
/// drained by exactly ONE persister task — persistence and its completion callbacks stay serial,
/// which the post-batch incremental match depends on.
/// Feature-specific policy (Strava rate limits + dedup, GPX local-time resolution, all counters
/// and progress callbacks) lives in the callers' delegates.
/// </summary>
public static class ParallelImportPipeline
{
    /// <summary>
    /// Runs the fetcher → bounded-channel → single-persister pipeline over <paramref name="items"/>.
    /// </summary>
    /// <param name="prepareAsync">
    /// Concurrent fetch/parse stage. Returns the prepared item to enqueue, or null when the item
    /// was fully handled without persistence (fetch/parse failure the caller already counted and
    /// reported). Must do its own per-item error handling — an unhandled throw faults the whole run.
    /// </param>
    /// <param name="persistAsync">
    /// Serial persist stage (single consumer). IE-07: the persister wraps the WHOLE body —
    /// persistence and the caller's progress callbacks — in a try, so a throw anywhere is routed
    /// to <paramref name="onPersistError"/> and the loop continues. An unhandled persister throw
    /// used to fault the loop, fill the bounded channel, and wedge the fetcher's WriteAsync forever.
    /// </param>
    /// <param name="onPersistError">
    /// Invoked when <paramref name="persistAsync"/> throws; the caller counts the item failed and
    /// reports it. Must not throw (a throw here faults the persister — same wedge class).
    /// </param>
    /// <param name="fetchCancellation">
    /// Optional token governing only the fetch stage (e.g. Strava's rate-limit linked CTS);
    /// defaults to <paramref name="ct"/>.
    /// </param>
    /// <param name="isExpectedFetchCancellation">
    /// When the fetch loop ends in OperationCanceledException, returning true swallows it
    /// (caller-triggered stop, e.g. rate limit — the caller rethrows its own exception after);
    /// false/null lets the cancellation propagate as a real cancel.
    /// </param>
    public static async Task RunAsync<TItem, TPrepared>(
        IEnumerable<TItem> items,
        Func<TItem, CancellationToken, ValueTask<TPrepared?>> prepareAsync,
        Func<TPrepared, Task> persistAsync,
        Action<TPrepared, Exception> onPersistError,
        CancellationToken ct,
        int maxConcurrency = 4,
        int capacity = 8,
        CancellationToken? fetchCancellation = null,
        Func<bool>? isExpectedFetchCancellation = null)
        where TPrepared : class
    {
        var channel = Channel.CreateBounded<TPrepared>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });

        var fetchCt = fetchCancellation ?? ct;

        var fetcherTask = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(items,
                    new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = fetchCt },
                    async (item, t) =>
                    {
                        var prepared = await prepareAsync(item, t);
                        if (prepared != null)
                            await channel.Writer.WriteAsync(prepared, t);
                    });
            }
            catch (OperationCanceledException) when (isExpectedFetchCancellation?.Invoke() == true)
            {
                // Caller-triggered fetch stop (e.g. Strava rate limit) — expected, not a user cancel.
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        var persisterTask = Task.Run(async () =>
        {
            await foreach (var p in channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await persistAsync(p);
                }
                catch (Exception ex)
                {
                    onPersistError(p, ex);
                }
            }
        }, ct);

        await Task.WhenAll(fetcherTask, persisterTask);
    }
}
