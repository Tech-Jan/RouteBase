using RouteBaseWeb.Services;

namespace RouteBaseWeb.Models;

// Response contracts for StravaApiController. Serialized camelCase (web defaults);
// wire shapes pinned by StravaApiWireShapeTests. Each mirrors a TS type — edit both
// in the same commit.

/// <summary>
/// 409 body when an import is already running. Carries more than the sanctioned bare
/// <c>{error}</c> convention (the caller's live progress snapshot) — unread by the TS client
/// (pages/keys-and-data.ts checks response.status === 409 only), named regardless.
/// </summary>
// Uniform error body (D32): `{ code, message, status }` (this body is always a 409).
public sealed record StravaImportConflictBody(string Message, ImportProgressSnapshot Status, string Code = ApiErrorCodes.Conflict);

/// <summary>Response of POST /api/strava/import-all on success (202 Accepted). Unread by the TS client.</summary>
public sealed record StravaImportQueuedResponse(string Message);

/// <summary>Response of GET /api/strava/status. Mirrors TS <c>StravaStatusResponse</c> (pages/keys-and-data.ts).</summary>
public sealed record StravaStatusResponse(bool IsAuthenticated, long? AthleteId);

/// <summary>Response of POST /api/strava/auth-url. Mirrors TS <c>StravaAuthUrlResponse</c> (pages/keys-and-data.ts).</summary>
public sealed record StravaAuthUrlResponse(string AuthUrl);
