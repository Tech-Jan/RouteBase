using RouteBaseWeb.Models.Queue;

namespace RouteBaseWeb.Services;

/// <summary>Activity-file batch import (GPX/TCX/FIT); BatchId identifies the staged batch directory.</summary>
public sealed record TrackImportPayload(string UserId, string BatchId) : OperationPayload(UserId);
