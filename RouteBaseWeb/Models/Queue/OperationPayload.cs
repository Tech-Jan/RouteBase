namespace RouteBaseWeb.Models.Queue;

/// <summary>
/// Base of the typed per-operation arguments (CR5-01, the recorded QW-01 seam): one sealed record
/// per <c>OperationQueue.OperationType</c> replaces the old nullable-property smear on WorkItem
/// (each prop "only set for X", read back with <c>!</c> in the worker). Enqueuers construct the
/// payload; the worker dispatches it to the matching <c>IOperationHandler</c>, which casts to its
/// concrete type. The base stays here (the infra contract); each sealed record lives beside its
/// handler (feature code — see <see cref="RouteBaseWeb.Services.Queue.IOperationHandler"/>).
/// Adding an operation type = one record + one handler + one DI line.
/// </summary>
public abstract record OperationPayload(string UserId);
