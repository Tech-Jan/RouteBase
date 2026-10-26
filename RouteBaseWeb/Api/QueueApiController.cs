using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Queue;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

[ApiController]
[Route("api/queue")]
public class QueueApiController : ControllerBase
{
    private readonly OperationQueue _queue;
    private readonly IReadOnlyList<IOperationHandler> _handlers;
    private readonly IReadOnlyDictionary<OperationQueue.OperationType, IOperationHandler> _handlersByType;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<QueueApiController> _logger;

    public QueueApiController(
        OperationQueue queue,
        IEnumerable<IOperationHandler> handlers,
        ICurrentUserService currentUser,
        ILogger<QueueApiController> logger)
    {
        _queue = queue;
        _handlers = handlers.ToList();
        _handlersByType = _handlers.ToDictionary(h => h.Type); // throws on a duplicate registration — config bug
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// Single unified progress endpoint for the UI toast.
    /// Returns whichever operation is currently active (or most recently finished),
    /// normalised into a common shape the client toast uses directly.
    /// The <c>operationType</c> discriminator tells the client which label/colour to apply.
    /// Every handler's own <see cref="IOperationHandler.GetQueueProgress"/> supplies its slice —
    /// this controller never knows what any operation type actually does (arch finding 1).
    /// </summary>
    [HttpGet("progress")]
    // TODO(openapi-review): D30 — the body is the [JsonPolymorphic] ProgressResponse base (idle|basic|import
    // via the additive $type discriminator, kept alongside operationType). The generated TS union narrows on
    // $type (queue-progress-contract.ts). Returned base-declared (Ok<ProgressResponse>) so STJ writes $type.
    [ProducesResponseType<ProgressResponse>(StatusCodes.Status200OK)]
    public ActionResult<ProgressResponse> GetProgress()
    {
        string userId = _currentUser.GetUserId();
        int pending = _queue.PendingCountFor(userId);

        var snapshots = _handlers
            .Select(h => h.GetQueueProgress(userId, pending))
            .OfType<QueueProgressSnapshot>()
            .ToList();

        // NOTE: return the ProgressResponse base directly (not Ok(...)) — the ActionResult<T> implicit
        // conversion sets DeclaredType = ProgressResponse, so the STJ output formatter serializes
        // base-declared and writes the $type shape discriminator (D30). Ok(object) would serialize the
        // runtime type and drop $type.
        var running = snapshots.FirstOrDefault(s => s.IsRunning);
        if (running is not null) return running.Body;

        // Items are queued but the worker hasn't started them yet.
        // Return a synthetic running state so the toast stays visible immediately
        // after enqueueing, before the worker picks the item up. Scope to THIS user's
        // pending items so another user's queue depth never pops a toast here.
        if (pending > 0)
        {
            var nextType = _queue.NextPendingTypeFor(userId);
            string discriminator = nextType.HasValue && _handlersByType.TryGetValue(nextType.Value, out var nextHandler)
                ? nextHandler.QueueDiscriminator
                : QueueOperationTypes.Analysis;

            return new BasicProgressResponse(
                OperationType: discriminator,
                IsRunning: true,
                Phase: "Queued...",
                Current: 0,
                Total: 0,
                Detail: "",
                CompletedSummary: "",
                Error: null,
                StartedAt: null,
                CompletedAt: null,
                QueuePosition: 1,
                QueueTotal: pending);
        }

        // Neither running — return the most recently finished one (if any).
        var lastFinished = snapshots
            .Where(s => s.CompletedAt.HasValue)
            .OrderByDescending(s => s.CompletedAt)
            .FirstOrDefault();
        if (lastFinished is not null) return lastFinished.Body;

        return new IdleProgressResponse(OperationType: QueueOperationTypes.Idle, IsRunning: false, Phase: "");
    }

    /// <summary>Acknowledge the most recently finished operation(s).</summary>
    [HttpPost("acknowledge")]
    [RejectCrossSiteBrowserRequests] // bodyless POSTs here: cross-site form/fetch-able without preflight (PI-01)
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Acknowledge()
    {
        string userId = _currentUser.GetUserId();
        foreach (var handler in _handlers) handler.Acknowledge(userId);
        return Ok();
    }

    /// <summary>Cancel the current user's running and pending operations (other users unaffected).</summary>
    [HttpPost("cancel")]
    [RejectCrossSiteBrowserRequests]
    [ProducesResponseType<CancelOperationsResponse>(StatusCodes.Status200OK)]
    public IActionResult Cancel()
    {
        string userId = _currentUser.GetUserId();
        _queue.CancelForUser(userId);
        foreach (var handler in _handlers) handler.Acknowledge(userId);
        return Ok(new CancelOperationsResponse("Your operations were cancelled"));
    }
}
