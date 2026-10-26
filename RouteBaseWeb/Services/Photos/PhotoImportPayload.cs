using RouteBaseWeb.Models.Queue;

namespace RouteBaseWeb.Services.Photos;

/// <summary>Photo batch import; BatchId identifies the staged batch directory.</summary>
public sealed record PhotoImportPayload(string UserId, string BatchId) : OperationPayload(UserId);
