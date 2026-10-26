using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Models;

// Response contracts for TrashApiController. Serialized camelCase (web defaults);
// wire shapes pinned by TrashApiWireShapeTests. Each mirrors a TS type — edit both
// in the same commit.

/// <summary>Response of GET /api/trash.</summary>
public sealed record TrashListResponse(IReadOnlyList<TrashedActivity> Items);

/// <summary>Response of POST /api/trash/restore.</summary>
public sealed record RestoreResponse(int Restored);

/// <summary>Response of POST /api/trash/purge and /api/trash/empty.</summary>
public sealed record PurgeResponse(int Purged);

/// <summary>
/// 409 body when a purge would destroy an un-doable merge (invariant 4) and the caller hasn't
/// confirmed yet. Carries more than the sanctioned bare <c>{error}</c> convention — the TS caller
/// (trash/trash.ts) reads <c>error</c> as the discriminator and <c>message</c> for the re-confirm
/// dialog, same pattern as TrackEditErrorBody (Services/TrackEdit/TrackEditApiModels.cs).
/// </summary>
// Uniform error body (D32): `{ code, message, mergeSourceIds }`. `code`="mergeSourcesNeedConfirm" IS the machine
// token trash.ts branches on (it was the old redundant `error` field); `message` is the human confirm text.
public sealed record MergeSourcesConfirmBody(
    string Message, IReadOnlyList<int> MergeSourceIds, string Code = "mergeSourcesNeedConfirm");
