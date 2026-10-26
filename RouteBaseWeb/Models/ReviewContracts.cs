namespace RouteBaseWeb.Models;

// Response contracts for ReviewApiController. Serialized camelCase (web defaults);
// wire shapes pinned by ReviewApiWireShapeTests. The TS caller (live-recording/record-review.ts
// postReview) only checks resp.ok — these bodies are currently unread, named per the DTO
// convention regardless (same precedent as ActivitiesApiController's UpdateActivityResponse).

/// <summary>Response of POST /api/review/approve.</summary>
public sealed record ConfirmResponse(int Confirmed);

/// <summary>Response of POST /api/review/trash.</summary>
public sealed record TrashCountResponse(int Trashed);
