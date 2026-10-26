using RouteBaseWeb.Services.Queue;

namespace RouteBaseWeb.Services;

/// <summary>
/// Single background worker that drains the shared <see cref="OperationQueue"/>.
/// All background jobs are serialized through this worker, so an "import then analyze" chain is
/// guaranteed to execute in order. Each operation type's behavior — including its queue-badge
/// stamping and import-slot reservation — lives in its <see cref="IOperationHandler"/> (CR5-01, the
/// QW-01 seam, extended by the queue-capabilities redesign); the worker owns only the loop
/// mechanics — discarded-item cleanup dispatch, cancel routing, and the centralized reservation
/// release / completion notification (QW-04). It knows only OperationType/OperationPayload/
/// IOperationHandler, never what an operation does.
/// </summary>
public class OperationQueueWorker : BackgroundService
{
    private readonly OperationQueue _queue;
    private readonly Dictionary<OperationQueue.OperationType, IOperationHandler> _handlers;
    private readonly ILogger<OperationQueueWorker> _logger;

    public OperationQueueWorker(
        OperationQueue queue,
        IEnumerable<IOperationHandler> handlers,
        ILogger<OperationQueueWorker> logger)
    {
        _queue = queue;
        _handlers = handlers.ToDictionary(h => h.Type); // throws on a duplicate registration — config bug
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.ReadAllAsync(stoppingToken))
        {
            var userId = item.UserId ?? "";
            _handlers.TryGetValue(item.Type, out var handler); // null only on a missing DI registration

            // Cancelled while still queued (the user cancelled before we dequeued it): let the
            // handler clean up (staged-batch delete, heatmap pending-row demote), then skip it.
            if (item.Cts.IsCancellationRequested)
            {
                // Warning, not Error: a cancel is user-initiated and expected, so routing it to
                // /Admin/Errors would be noise in the one place that must stay signal. But it was logged
                // NOWHERE before, which made "the derive silently never ran" indistinguishable from "it ran
                // and matched nothing" — this branch drops real work on the floor and has to say so.
                _logger.LogWarning("Operation {Type} was cancelled before it ran and is being skipped", item.Type);

                if (handler is not null && item.Payload is { } discarded)
                    await handler.OnDiscardedWhileQueuedAsync(discarded);
                if (handler?.HoldsImportSlot == true)
                    _queue.ReleaseExclusiveSlot(userId); // idempotent
                _queue.NotifyOperationCompleted(item);       // queue clears _current + disposes the CTS
                continue;
            }

            // Per-user burst position/total so the badge resets between bursts and never bleeds
            // another user's history (QW-05). Stamped only onto this item's own handler — a handler
            // with no toast-visible progress (e.g. photo import) just no-ops (default SetQueueInfo).
            var (position, total) = _queue.BurstInfoFor(item.UserId);
            handler?.SetQueueInfo(userId, position, total);

            // Per-item cancel token so a user's cancel aborts only their running op.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, item.Cts.Token);

            try
            {
                if (handler is null)
                    _logger.LogError("No IOperationHandler registered for operation type {Type}", item.Type);
                else if (item.Payload is null)
                    _logger.LogError("Operation {Type} was enqueued without a payload — skipped", item.Type);
                else
                    await handler.RunAsync(item.Payload, linked.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // User-requested cancel — fail only the progress class for the op that ran,
                // so an unrelated idle progress state isn't stamped "Cancelled by user".
                handler?.FailProgress(userId, "Cancelled by user");
                _logger.LogInformation("Operation {Type} cancelled by user", item.Type);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing operation {Type}", item.Type);
            }
            finally
            {
                // Central release covers every exit path of an import-type item, including throws
                // and early returns before the handler's own try block (QW-04). Capability-guarded:
                // only import-slot handlers hold the slot — an unconditional release let a user's
                // unrelated op free their still-queued import's reservation, opening a second-import
                // window against the shared per-user import progress state (B5).
                if (handler?.HoldsImportSlot == true)
                    _queue.ReleaseExclusiveSlot(userId);
                _queue.NotifyOperationCompleted(item); // queue clears _current + disposes the CTS
            }
        }
    }
}
