using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Queue;

/// <summary>
/// Normalized op-toast progress for one handler's feature (<see cref="IOperationHandler.GetQueueProgress"/>).
/// IsRunning/CompletedAt let the queue layer pick which handler's Body to serve — running wins, else the
/// most recently completed — without the layer knowing what any operation actually does. Body is the
/// polymorphic <see cref="ProgressResponse"/> base (D13) so the controller serves it base-declared and STJ
/// writes the <c>$type</c> shape discriminator.
/// </summary>
public sealed record QueueProgressSnapshot(bool IsRunning, DateTimeOffset? CompletedAt, ProgressResponse Body);
