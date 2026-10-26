using RouteBaseWeb.Models.Queue;

namespace RouteBaseWeb.Services;

/// <summary>Provider-based activity import (e.g. "strava"); <paramref name="After"/> limits the fetch window.</summary>
public sealed record ActivityImportPayload(string UserId, string ProviderId, DateTime? After) : OperationPayload(UserId);
