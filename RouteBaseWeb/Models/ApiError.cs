namespace RouteBaseWeb.Models;

/// <summary>
/// THE canonical JSON error body for <c>api/</c> endpoints. Serialized camelCase:
/// <code>{ "code": "...", "message": "..." }</code>
/// <list type="bullet">
/// <item><c>code</c> — a stable machine token a client can branch on (see <see cref="ApiErrorCodes"/>): the
///   status-tier code by default, a specific token only where a client actually branches.</item>
/// <item><c>message</c> — the human-readable text.</item>
/// </list>
/// The legacy <c>error</c> alias (D31) was removed (D32): the only external client, GpsRecorder, never parsed
/// error bodies (it branches on HTTP status and logs the raw body), and the web client ships in lockstep with
/// the codegen finding every read — so the duplicate field earned nothing. Rich conflict bodies
/// (SegmentAnalysisConflictBody, PolicyRejectedBody, …) keep their own record with a <c>Message</c> + a
/// defaulted <c>Code</c>, so every error body on the wire is uniformly <c>{ code, message, … }</c>.
/// </summary>
public sealed record ApiError(string Code, string Message)
{
    static ApiError Of(string code, string message) => new(code, message);

    // One factory per status tier — the call site's MVC helper maps 1:1 (BadRequest(...) → ApiError.BadRequest(...)),
    // so `code` is the status tier without the body having to know its own HTTP status.
    public static ApiError BadRequest(string message) => Of(ApiErrorCodes.BadRequest, message);
    public static ApiError Unauthorized(string message) => Of(ApiErrorCodes.Unauthorized, message);
    public static ApiError NotFound(string message) => Of(ApiErrorCodes.NotFound, message);
    public static ApiError Conflict(string message) => Of(ApiErrorCodes.Conflict, message);
    public static ApiError RateLimited(string message) => Of(ApiErrorCodes.RateLimited, message);
    public static ApiError ServerError(string message) => Of(ApiErrorCodes.ServerError, message);
    public static ApiError ServiceUnavailable(string message) => Of(ApiErrorCodes.ServiceUnavailable, message);

    /// <summary>The model-binding/validation 400 (API-07). Its own code, not the BadRequest tier: a
    /// malformed body is a different thing from input a rule rejected, and a client may branch on it.</summary>
    public static ApiError WithValidationFailure(string message) => Of(ApiErrorCodes.ValidationFailed, message);
}

/// <summary>
/// The generic status-tier error codes (D7B). Specific per-feature codes live with their feature (e.g.
/// <c>TrackEditApiErrors</c>); add one here only when a client must branch on it.
/// </summary>
public static class ApiErrorCodes
{
    public const string BadRequest = "badRequest";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "notFound";
    public const string Conflict = "conflict";
    public const string RateLimited = "rateLimited";
    public const string ServerError = "serverError";
    public const string ServiceUnavailable = "serviceUnavailable";

    /// <summary>The [ApiController] model-binding/validation 400 (API-07) — the one 400 a client CAN
    /// branch on, because it means "your request body was malformed", not "your input was rejected by a
    /// rule". Configured in Program.cs via InvalidModelStateResponseFactory.</summary>
    public const string ValidationFailed = "validationFailed";
}
