namespace RouteBaseWeb.Models.Uploads;

// Response contracts shared by ImportApiController and PhotosApiController's chunked batch-upload
// endpoints (begin → files… → commit → abort) — both map the SAME Services/Uploads/BatchUploadCoordinator
// outcomes to HTTP, so the wire shapes are identical apart from BeginBatch (each controller has its own,
// richer for Photos). Wire shapes pinned by ImportApiWireShapeTests/PhotosApiWireShapeTests. Each mirrors
// a TS type — edit both in the same commit.

/// <summary>Response of POST batch/begin (ImportApiController — no extra fields beyond the batch id;
/// PhotosApiController has its own richer PhotoBeginBatchResponse).</summary>
public sealed record BeginBatchResponse(string? BatchId);

/// <summary>Response of POST batch/{batchId}/files on success (BatchAppendOutcome.Staged).</summary>
public sealed record StagedFilesResponse(int Staged);

/// <summary>
/// 400 body when the upload policy rejects some files (BatchAppendOutcome.PolicyRejected), and
/// PhotosApiController's separate extension-allowlist check. Carries more than the sanctioned bare
/// <c>{error}</c> convention — the TS caller (pages/import.ts) reads <c>files</c> to badge the specific
/// rejected entries.
/// </summary>
// Uniform error body (D32): `{ code, message, files }` (this body is always a 400).
public sealed record PolicyRejectedBody(string? Message, IEnumerable<string>? Files, string Code = ApiErrorCodes.BadRequest);

/// <summary>Response of POST batch/{batchId}/commit on success (BatchCommitOutcome.Queued).</summary>
public sealed record CommitBatchResponse(string BatchId, int Queued);

/// <summary>Response of POST batch/{batchId}/abort.</summary>
public sealed record AbortBatchResponse(bool Aborted);
