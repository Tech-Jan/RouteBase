using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Queue;

namespace RouteBaseWeb.Services.Queue;

/// <summary>
/// One background-operation type = one handler (CR5-01, adopting the QW-01 seam). The worker
/// dispatches dequeued items to the handler registered for their
/// <see cref="OperationQueue.OperationType"/>; handlers are plain singletons registered in
/// Program.cs. Adding an operation type = payload record + handler class + one DI line —
/// no worker/queue edits.
///
/// Ownership rule (B5): A handler and its payload record are feature code that happens to run on
/// the queue — they live with their feature slice and may call other contexts only through those
/// contexts' explicit seams; the queue/worker/QueueApiController layer knows only OperationType,
/// OperationPayload, and IOperationHandler, and never contains a rule about what an operation
/// does, sequences, or cleans up.
/// </summary>
public interface IOperationHandler
{
    OperationQueue.OperationType Type { get; }

    /// <summary>
    /// Runs the operation. Owns scope creation, the CurrentUser override
    /// (<see cref="OperationScope"/>), and its progress class's start/complete/fail lifecycle —
    /// the body of the worker's old Run*Async. Throws OperationCanceledException on cancel.
    /// </summary>
    Task RunAsync(OperationPayload payload, CancellationToken ct);

    /// <summary>
    /// Cleanup when the item was cancelled while still queued (it never ran). Default no-op;
    /// Track/Photo import delete their staged batch, heatmap generation demotes its pending row.
    /// </summary>
    Task OnDiscardedWhileQueuedAsync(OperationPayload payload) => Task.CompletedTask;

    /// <summary>
    /// Stamp a user-cancel failure on this operation's progress class (the worker's cancel
    /// routing), so an unrelated idle progress state is never marked "Cancelled by user".
    /// </summary>
    void FailProgress(string userId, string message);

    // ── Queue/toast capabilities ────────────────────────────────────────────
    // The worker and QueueApiController dispatch to these instead of switching on OperationType or
    // holding a reference to any concrete progress class (arch finding 1) — a handler that needs
    // none of them (e.g. photo import's queue badge, which polls its own endpoint) simply keeps the
    // default.

    /// <summary>True if enqueuing this operation type reserves the per-user exclusive slot
    /// (<see cref="OperationQueue.TryReserveExclusiveSlot"/>). Default false; Activity/Track/Photo import
    /// override true. The worker releases the slot through this instead of a type switch.</summary>
    bool HoldsImportSlot => false;

    /// <summary>Stamp this user's burst position/total (QW-05) onto this handler's own progress
    /// state, for the op-toast queue badge. Default no-op for a handler with no toast-visible
    /// progress.</summary>
    void SetQueueInfo(string userId, int position, int total) { }

    /// <summary>Normalized op-toast snapshot for this handler's progress, or null if this handler
    /// doesn't participate in the op-toast rotation. <paramref name="pendingCountForUser"/> is the
    /// user's total queue-pending count, used to compute a live queueTotal.</summary>
    QueueProgressSnapshot? GetQueueProgress(string userId, int pendingCountForUser) => null;

    /// <summary>Op-toast discriminator for the "Queued..." synthetic state shown before this
    /// handler's progress state exists yet (the worker hasn't dequeued the item). Default matches
    /// the analysis toast, the historical fallback for an unmapped type.</summary>
    string QueueDiscriminator => QueueOperationTypes.Analysis;

    /// <summary>Clear this handler's finished progress state so the toast stops re-showing it on
    /// the next poll/page load. Default no-op.</summary>
    void Acknowledge(string userId) { }
}
