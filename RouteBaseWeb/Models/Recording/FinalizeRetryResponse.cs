namespace RouteBaseWeb.Models.Recording;

/// <summary>
/// The response to <c>POST sessions/{id}/retry-finalize</c>. <c>Finalized: false</c> means the retry
/// itself threw and the session was re-parked at 'finalize_failed' (the recent-sessions row keeps its
/// Retry/Discard actions) — not an error response, since the manual retry attempt did run to completion.
/// </summary>
public sealed record FinalizeRetryResponse(bool Finalized);
