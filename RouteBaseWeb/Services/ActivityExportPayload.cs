using RouteBaseWeb.Models.Queue;

namespace RouteBaseWeb.Services;

/// <summary>
/// Arguments of <see cref="OperationQueue.OperationType.ActivityExport"/> — the activities page's
/// "Export GPX (N)" button. <paramref name="TrackIds"/> is the table's current filtered scope, so it is a
/// RESTRICTION, not an assertion: the handler intersects it with what the user owns and drops the rest
/// silently (a table scoped to Everyone legitimately contains other people's rides).
///
/// <paramref name="JobId"/> names the ZIP in the export store and in the download URL. It is minted at
/// enqueue time so the caller can be told where its file will appear before the job has run.
/// </summary>
public sealed record ActivityExportPayload(string UserId, IReadOnlyList<int> TrackIds, long JobId)
    : OperationPayload(UserId);
