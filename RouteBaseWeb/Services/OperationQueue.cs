using System.Threading.Channels;
using RouteBaseWeb.Models.Queue;

namespace RouteBaseWeb.Services;

/// <summary>
/// Singleton cross-service FIFO queue.
/// Serialises all background operations (Strava import, segment analysis)
/// and exposes per-user cancellation so a user can abort only their own operations.
/// </summary>
public class OperationQueue
{
    public enum OperationType { ActivityImport, SegmentAnalysis, SingleSegmentAnalysis, TrackImport, HeatmapGeneration, PhotoImport, TrackEditDerive, ActivityExport }

    public sealed class WorkItem
    {
        public OperationType Type { get; init; }
        /// <summary>The authenticated user who enqueued this operation.</summary>
        public string? UserId { get; init; }
        /// <summary>Typed per-operation arguments (CR5-01): one payload record per <see cref="OperationType"/>
        /// replaced the old nullable-property smear here. Every production enqueuer sets it; the worker
        /// dispatches it to the matching <c>IOperationHandler</c>.</summary>
        public OperationPayload? Payload { get; init; }
        /// <summary>Per-item cancellation, so one user's cancel aborts only their own operations.</summary>
        public CancellationTokenSource Cts { get; } = new();
    }

    private readonly Channel<WorkItem> _channel =
        Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    private int _processedCount;    // monotonic count of completed items (incremented via NotifyOperationCompleted)

    // Mirror of the items the channel holds, used for per-user cancellation. The channel
    // itself isn't enumerable without consuming it, so we track pending items alongside it.
    private readonly object _pendingLock = new();
    private readonly List<WorkItem> _pending = new();
    private WorkItem? _current;

    // Per-user "burst" = the user's ops from their first enqueue-while-idle until their last
    // in-flight item completes. Drives the op-toast "Op N / M" badge per-user instead of via
    // lifetime-global counters, which never reset and bled one user's history into another's badge
    // (QW-05). Guarded by _pendingLock; an entry exists only while the user has in-flight work, so
    // the dictionary is self-bounding and the numbering resets at the end of every burst.
    private sealed class Burst { public int Done; public int Total; }
    private readonly Dictionary<string, Burst> _bursts = new(StringComparer.Ordinal);

    /// <summary>Number of operations completed since startup (monotonic).</summary>
    public int ProcessedCount => _processedCount;

    /// <summary>
    /// Enqueue a work item. Returns false only if the channel is full (capacity 100).
    /// </summary>
    public bool TryEnqueue(WorkItem item)
    {
        // Mirror the item (and grow its user's burst) before publishing: releasing _pendingLock is
        // the memory barrier, so any consumer that dequeues the channel item also sees the Add
        // (QW-03: publishing first let the worker's Remove run before this Add, stranding a phantom
        // pending item forever).
        var key = item.UserId ?? "";
        lock (_pendingLock)
        {
            _pending.Add(item);
            if (!_bursts.TryGetValue(key, out var b)) _bursts[key] = b = new Burst();
            b.Total++;
        }
        if (_channel.Writer.TryWrite(item))
            return true;
        lock (_pendingLock)
        {
            _pending.Remove(item);
            if (_bursts.TryGetValue(key, out var b) && --b.Total <= b.Done) _bursts.Remove(key);
        }
        return false;
    }

    // ── Per-user exclusive-slot reservation ────────────────────────────────
    //
    // A single per-user reservation slot the queue offers operation-agnostically: a caller reserves
    // it before staging work, and the worker releases it when the reserving item finishes. The queue
    // names no operation here — which operation types acquire the slot is a handler concern
    // (<see cref="Queue.IOperationHandler.HoldsImportSlot"/>). Today the import family uses it, because
    // Strava/GPX/photo imports share per-user progress state a second concurrent import would corrupt.

    private readonly TimeProvider _clock;

    public OperationQueue(TimeProvider clock) => _clock = clock;

    private readonly object _reservationLock = new();
    // Reservation timestamps, not a bare set (PI-06): a reserve with no follow-up (tab closed before
    // commit/abort, or the staging sweep deleting the dir out from under a held slot) must not lock the
    // user out until process restart. A reservation older than the TTL is reclaimable.
    private static readonly TimeSpan ReservationTtl = TimeSpan.FromHours(24); // matches the staging sweep TTL
    private readonly Dictionary<string, DateTime> _slotReservations = new(StringComparer.Ordinal);

    /// <summary>
    /// Reserve the exclusive slot for <paramref name="userId"/>. Returns false if the user already
    /// holds it. Caller MUST release it (via <see cref="ReleaseExclusiveSlot"/>) on any failure path
    /// before enqueue.
    /// </summary>
    public bool TryReserveExclusiveSlot(string userId) => TryReserveExclusiveSlot(userId, _clock.GetUtcNow().UtcDateTime);

    /// <summary>Testable seam: reserve as of <paramref name="nowUtc"/>. A held reservation older than
    /// <see cref="ReservationTtl"/> is treated as leaked and reclaimed.</summary>
    internal bool TryReserveExclusiveSlot(string userId, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(userId)) return true; // anonymous: skip the gate
        lock (_reservationLock)
        {
            if (_slotReservations.TryGetValue(userId, out var since) && nowUtc - since < ReservationTtl)
                return false;
            _slotReservations[userId] = nowUtc; // fresh, or reclaimed-stale
            return true;
        }
    }

    /// <summary>Release the per-user exclusive-slot reservation. Safe to call when none exists.</summary>
    public void ReleaseExclusiveSlot(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        lock (_reservationLock) { _slotReservations.Remove(userId); }  // Remove: dict keyed by userId (PI-06)
    }

    /// <summary>
    /// Called by the worker when it finishes (or skips) one dequeued item. The queue owns the
    /// item's completion so clearing <c>_current</c> and disposing its CTS happen under
    /// <see cref="_pendingLock"/> — mutually exclusive with <see cref="CancelForUser"/>'s
    /// <c>Cancel()</c>, so a cancel racing completion can never touch a disposed CTS (QW-02).
    /// </summary>
    public void NotifyOperationCompleted(WorkItem item)
    {
        Interlocked.Increment(ref _processedCount);
        lock (_pendingLock)
        {
            var key = item.UserId ?? "";
            // Advance the user's burst; removing the entry when it drains is what resets the badge
            // numbering and bounds the dictionary (QW-05).
            if (_bursts.TryGetValue(key, out var b) && ++b.Done >= b.Total) _bursts.Remove(key);
            if (ReferenceEquals(_current, item)) _current = null;
            item.Cts.Dispose();
        }
    }

    /// <summary>
    /// 1-based position of the user's current op within their burst, and the burst size. Returns
    /// (1, 1) when the user has no in-flight work — a single op then renders no "Op N / M" badge.
    /// </summary>
    public (int Position, int Total) BurstInfoFor(string? userId)
    {
        lock (_pendingLock)
            return _bursts.TryGetValue(userId ?? "", out var b)
                ? (Math.Min(b.Done + 1, b.Total), b.Total)
                : (1, 1);
    }

    /// <summary>
    /// Cancel the pending and currently-running operations belonging to <paramref name="userId"/>,
    /// leaving every other user's operations untouched. Cancelled pending items stay in the channel
    /// and are skipped by the worker at dequeue.
    ///
    /// Deliberately does NOT release the exclusive-slot reservation (CONC-P2): the reservation is
    /// user-scoped, but the worker is the single authoritative releaser — it frees the slot for every
    /// enqueued import when it drains that item (normal finish, cancelled-while-queued, and
    /// cancelled-while-running all route through <see cref="OperationQueueWorker"/>'s
    /// <c>HoldsImportSlot</c>-guarded release). An eager release here freed a slot a fresh re-import may
    /// already own — cancel A, immediately re-import B, then the worker's later cleanup of the cancelled
    /// A frees B's reservation, defeating the one-import-at-a-time gate. The only cost of dropping it is
    /// that a re-import in the brief window before the worker finishes the cancelled item is refused
    /// ("import already running") rather than admitted-then-corrupted; the serial worker makes that
    /// window near-instant for the running item.
    /// </summary>
    public void CancelForUser(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        lock (_pendingLock)
        {
            foreach (var item in _pending.Where(i => i.UserId == userId)) item.Cts.Cancel();
            if (_current?.UserId == userId) _current.Cts.Cancel();
        }
    }

    /// <summary>
    /// Number of this user's own items still waiting in the channel (excludes the running item —
    /// that reports through its progress class — and cancelled items). Used so one user's queue
    /// depth never bleeds into another user's toast.
    /// </summary>
    public int PendingCountFor(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return 0;
        lock (_pendingLock)
            return _pending.Count(i => i.UserId == userId && !i.Cts.IsCancellationRequested);
    }

    /// <summary>Type of this user's first still-pending (non-cancelled) item, else null.</summary>
    public OperationType? NextPendingTypeFor(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        lock (_pendingLock)
            return _pending.FirstOrDefault(i => i.UserId == userId && !i.Cts.IsCancellationRequested)?.Type;
    }

    public async IAsyncEnumerable<WorkItem> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            lock (_pendingLock) { _pending.Remove(item); _current = item; }
            yield return item;
            // Reaching here = the consumer asked for the next item, so the current op finished.
            lock (_pendingLock) _current = null;
        }
        lock (_pendingLock) _current = null;
    }
}
