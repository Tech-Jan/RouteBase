using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Models;

// Response contracts for ActivityLifecycleApiController. Serialized camelCase (web defaults);
// wire shapes pinned by ActivityLifecycleApiWireShapeTests. Each mirrors a TS type — edit both
// in the same commit.

/// <summary>
/// Response of POST /api/activities/{id}/park and /release. The TS caller (activity/version-history.ts
/// togglePark) only checks resp.ok and reloads — unread, named per the DTO convention regardless.
/// </summary>
public sealed record LifecycleTransitionResponse(string? Lifecycle);

/// <summary>Response of GET /api/activities/{id}/versions. Mirrors TS <c>VersionsListResponse</c> (activity/version-history.ts).</summary>
public sealed record VersionsListResponse(IReadOnlyList<TrackVersionView> Versions);

/// <summary>
/// Response of POST /api/activities/{id}/versions/reset. The TS caller only checks resp.ok and
/// reloads — unread, named per the DTO convention regardless.
/// </summary>
public sealed record ResetResponse(bool Reset);

/// <summary>
/// Response of POST /api/activities/{id}/versions/{versionId}/restore. Its own record on purpose:
/// this used to reuse the trash slice's <c>RestoreResponse</c>, whose <c>Restored</c> field means
/// "count restored" — stuffing a version id into it forked one generated schema into two
/// incompatible semantics. The TS caller only checks resp.ok and reloads.
/// </summary>
public sealed record RestoreVersionResponse(int VersionId);
